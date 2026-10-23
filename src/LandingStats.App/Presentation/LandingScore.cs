using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Presentation;

internal enum LandingScoreCategory { Light, Regional, Airliner, Heavy }

internal enum LandingScoreCriterion { VerticalSpeed, GForce, TouchdownZone, Centerline, Crab, Bank, Pitch }

internal sealed class LandingScoreComponent
{
    public LandingScoreComponent(LandingScoreCriterion criterion, double weight, double? value, double? score, (double X, double Score)[] curve)
    { Criterion = criterion; Weight = weight; Value = value; Score = score; Curve = curve; }
    public LandingScoreCriterion Criterion { get; }
    /// <summary>Maximum points; the weights add up to 100.</summary>
    public double Weight { get; }
    /// <summary>Measured magnitude in the criterion's display unit.</summary>
    public double? Value { get; }
    /// <summary>0–100, or null when the measurement is unavailable.</summary>
    public double? Score { get; }
    public double? Points => Score * Weight / 100;
    /// <summary>Piecewise-linear score curve over <see cref="Value"/>.</summary>
    public IReadOnlyList<(double X, double Score)> Curve { get; }
}

/// <summary>
/// A transparent touchdown rating. Component weights follow TouchdownFX's
/// published split; every threshold is this application's own and is exposed
/// with the result. Scores the selected contact only.
/// </summary>
internal sealed class LandingScore
{
    internal const double LightMaximumPounds = 12500;
    internal const double RegionalMaximumPounds = 100000;
    internal const double HeavyMinimumPounds = 300000;
    internal const double TouchdownZoneOverrunMeters = 300;
    internal const double CrabSampleSeconds = 0.5;
    private static readonly IReadOnlyDictionary<LandingScoreCriterion, double> Weights = new Dictionary<LandingScoreCriterion, double>
    {
        [LandingScoreCriterion.VerticalSpeed] = 30, [LandingScoreCriterion.GForce] = 15, [LandingScoreCriterion.TouchdownZone] = 15,
        [LandingScoreCriterion.Centerline] = 10, [LandingScoreCriterion.Crab] = 10, [LandingScoreCriterion.Bank] = 10, [LandingScoreCriterion.Pitch] = 10,
    };

    private LandingScore() { }

    public LandingScoreCategory Category { get; private set; }
    /// <summary>True when the category comes from landing weight, false from contact airspeed.</summary>
    public bool CategoryFromWeight { get; private set; }
    public IReadOnlyList<LandingScoreComponent> Components { get; private set; } = Array.Empty<LandingScoreComponent>();
    public double AvailableWeight => Components.Where(component => component.Score.HasValue).Sum(component => component.Weight);
    /// <summary>Points over the available weights, normalised to 0–100; null without any measurement.</summary>
    public double? Total => AvailableWeight <= 0 ? null : Components.Sum(component => component.Points ?? 0) / AvailableWeight * 100;
    /// <summary>Signed crab for display: positive when the nose points right of the ground track.</summary>
    public double? CrabSignedDegrees { get; private set; }

    public static LandingScore Create(LandingRecord record, LandingReplayTrack? track, LandingDistanceAnalysis? distance)
    {
        var result = new LandingScore();
        result.ResolveCategory(record);
        var components = new List<LandingScoreComponent>();
        void Add(LandingScoreCriterion criterion, double? value, (double X, double Score)[] curve, double? score = null) =>
            components.Add(new LandingScoreComponent(criterion, Weights[criterion], value,
                score ?? (value is double measured && Finite(measured) ? Evaluate(curve, measured) : null), curve));

        Add(LandingScoreCriterion.VerticalSpeed, Finite(record.InertialFpm) ? Math.Abs(record.InertialFpm) : null, VerticalSpeedCurve(result.Category));
        Add(LandingScoreCriterion.GForce, new LandingPresentation(record).ValidImpactPeakG, GForceCurve);

        if (distance != null)
        {
            var touchdownZone = TouchdownZoneCurve(distance);
            Add(LandingScoreCriterion.TouchdownZone, distance.TouchdownMeters, touchdownZone,
                distance.TouchdownMeters < 0 ? 0 : Evaluate(touchdownZone, distance.TouchdownMeters));
            var lateral = Math.Abs(distance.TouchdownLateralMeters);
            var centerline = CenterlineCurve(distance.RunwayWidthMeters);
            Add(LandingScoreCriterion.Centerline, lateral, centerline, distance.OffRunwayEdge ? 0 : Evaluate(centerline, lateral));
        }
        else
        {
            Add(LandingScoreCriterion.TouchdownZone, null, Array.Empty<(double, double)>());
            Add(LandingScoreCriterion.Centerline, null, Array.Empty<(double, double)>());
        }

        result.CrabSignedDegrees = track == null ? null : CrabDegrees(track);
        Add(LandingScoreCriterion.Crab, result.CrabSignedDegrees is double crab ? Math.Abs(crab) : null, CrabCurve);
        Add(LandingScoreCriterion.Bank, Finite(record.BankDegrees) ? Math.Abs(record.BankDegrees) : null, BankCurve);
        // MSFS reports nose-up pitch as negative.
        Add(LandingScoreCriterion.Pitch, Finite(record.PitchDegrees) ? -record.PitchDegrees : null, PitchCurve(result.Category));
        result.Components = components;
        return result;
    }

    internal static (double X, double Score)[] VerticalSpeedCurve(LandingScoreCategory category)
    {
        // FAR 25.473 designs transport landing gear for 10 ft/s (600 fpm) at
        // design landing weight; lighter categories are expected to land softer.
        var (full, good, zero) = category switch
        {
            LandingScoreCategory.Light => (100.0, 200.0, 500.0),
            LandingScoreCategory.Regional => (150.0, 300.0, 600.0),
            LandingScoreCategory.Heavy => (220.0, 400.0, 600.0),
            _ => (180.0, 360.0, 600.0),
        };
        return new[] { (0.0, 100.0), (full, 100.0), (good, 75.0), (zero, 0.0) };
    }

    // The application's own SMOOTH and FIRM impact limits anchor the curve.
    internal static readonly (double X, double Score)[] GForceCurve =
        { (0, 100), (AnalysisNumericPolicy.SmoothImpactG, 100), (AnalysisNumericPolicy.FirmImpactG, 60), (2.0, 0) };

    internal static (double X, double Score)[] TouchdownZoneCurve(LandingDistanceAnalysis distance) => new[]
    {
        (0.0, 60.0), (distance.TargetZoneStartMeters, 100.0), (distance.TargetZoneEndMeters, 100.0),
        (Math.Max(distance.TargetZoneEndMeters, distance.TouchdownZoneEndMeters), 50.0),
        (Math.Max(distance.TargetZoneEndMeters, distance.TouchdownZoneEndMeters) + TouchdownZoneOverrunMeters, 0.0),
    };

    internal static (double X, double Score)[] CenterlineCurve(double runwayWidthMeters)
    {
        var edge = Math.Max(8, runwayWidthMeters / 2);
        return new[] { (0.0, 100.0), (1.5, 100.0), (3.0, 85.0), (6.0, 50.0), (edge, 0.0) };
    }

    internal static readonly (double X, double Score)[] CrabCurve = { (0, 100), (1, 100), (3, 80), (5, 40), (8, 0) };

    internal static readonly (double X, double Score)[] BankCurve = { (0, 100), (2, 100), (4, 70), (7, 20), (10, 0) };

    internal static (double X, double Score)[] PitchCurve(LandingScoreCategory category)
    {
        // Nose-up band per category; scores fall toward a nose-first touchdown
        // and toward typical tail-strike attitudes.
        var (low, high) = category switch
        {
            LandingScoreCategory.Light => (3.0, 10.0),
            LandingScoreCategory.Regional => (1.0, 7.0),
            LandingScoreCategory.Heavy => (2.0, 6.5),
            _ => (2.0, 7.0),
        };
        return new[] { (low - 4, 0.0), (low - 2, 50.0), (low, 100.0), (high, 100.0), (high + 2, 50.0), (high + 4, 0.0) };
    }

    internal static double Evaluate(IReadOnlyList<(double X, double Score)> curve, double value)
    {
        if (value <= curve[0].X) return curve[0].Score;
        for (var index = 1; index < curve.Count; index++)
        {
            var (x0, s0) = curve[index - 1];
            var (x1, s1) = curve[index];
            if (value <= x1) return x1 == x0 ? s1 : s0 + (s1 - s0) * (value - x0) / (x1 - x0);
        }
        return curve[curve.Count - 1].Score;
    }

    private void ResolveCategory(LandingRecord record)
    {
        if (Finite(record.WeightPounds) && record.WeightPounds > 0)
        {
            CategoryFromWeight = true;
            Category = record.WeightPounds < LightMaximumPounds ? LandingScoreCategory.Light :
                record.WeightPounds < RegionalMaximumPounds ? LandingScoreCategory.Regional :
                record.WeightPounds >= HeavyMinimumPounds ? LandingScoreCategory.Heavy : LandingScoreCategory.Airliner;
            return;
        }
        var speed = Finite(record.AirspeedKnots) ? record.AirspeedKnots : 0;
        Category = speed < 90 ? LandingScoreCategory.Light : speed < 115 ? LandingScoreCategory.Regional :
            speed < 150 ? LandingScoreCategory.Airliner : LandingScoreCategory.Heavy;
    }

    /// <summary>Nose heading minus ground track over the last half second before contact.</summary>
    private static double? CrabDegrees(LandingReplayTrack track)
    {
        if (!track.TrySample(-CrabSampleSeconds, out var before) || !track.TrySample(0, out var contact)) return null;
        var east = contact.Position.X - before.Position.X;
        var north = -(contact.Position.Z - before.Position.Z);
        if (Math.Sqrt(east * east + north * north) < 5) return null;
        var groundTrack = Math.Atan2(east, north) * 180 / Math.PI;
        var crab = (contact.HeadingDegrees - groundTrack + 540) % 360 - 180;
        return Finite(crab) ? crab : null;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
