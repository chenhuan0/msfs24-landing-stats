using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.Presentation;

internal enum LandingScoreCategory { Light, Turboprop, Airliner, Heavy }

internal enum LandingScoreCriterion { VerticalSpeed, GForce, TouchdownZone, Centerline, Crab, Bank, Pitch }

internal enum LandingScorePenaltyKind { Crab, Centerline, Bank, GForce, LateTouchdown }

internal sealed class LandingScoreComponent
{
    public LandingScoreComponent(LandingScoreCriterion criterion, double weight, double? value, double? score, (double X, double Score)[] curve)
    { Criterion = criterion; Weight = weight; Value = value; Score = score; Curve = curve; }
    public LandingScoreCriterion Criterion { get; }
    /// <summary>Maximum points; the weights add up to 100.</summary>
    public double Weight { get; }
    /// <summary>Measured magnitude in the criterion's display unit, or null when not measured.</summary>
    public double? Value { get; }
    /// <summary>
    /// 0–100 on <see cref="Curve"/>. An unmeasured criterion is scored as TouchdownFX scores
    /// a missing reading; null only when no score is possible.
    /// </summary>
    public double? Score { get; }
    /// <summary>Whole points, rounded down as TouchdownFX does.</summary>
    public double? Points => Score is double score ? Math.Floor(score * Weight / 100 + 1e-9) : null;
    /// <summary>Piecewise-linear score curve over <see cref="Value"/>.</summary>
    public IReadOnlyList<(double X, double Score)> Curve { get; }
}

/// <summary>A parameter beyond its safe limit, which multiplies the total.</summary>
internal sealed class LandingScorePenalty
{
    public LandingScorePenalty(LandingScorePenaltyKind kind, double value, double limit, double multiplier)
    { Kind = kind; Value = value; Limit = limit; Multiplier = multiplier; }
    public LandingScorePenaltyKind Kind { get; }
    /// <summary>Measured magnitude in the display unit; percent of runway length for a late touchdown.</summary>
    public double Value { get; }
    public double Limit { get; }
    public double Multiplier { get; }
}

/// <summary>
/// The TouchdownFX 2.1.7 landing score, reproduced from the scorecard on touchdownfx.com,
/// whose formula gives the stored score of its public landings. Each criterion scores on a
/// piecewise-linear curve from full marks to zero and is rounded down to whole points; the sum
/// is multiplied by a severity penalty for each parameter beyond its safe limit. Scores the
/// selected contact only. The captured rules are recorded in docs/touchdownfx-scoring.md.
/// </summary>
internal sealed class LandingScore
{
    /// <summary>Bump whenever a weight, curve or penalty changes; cached history scores are then recomputed.</summary>
    internal const int RulesVersion = 4;
    internal const double LightMaximumKnots = 70;
    internal const double TurbopropMaximumKnots = 110;
    internal const double AirlinerMaximumKnots = 155;
    /// <summary>The airspeed TouchdownFX assumes when none is recorded.</summary>
    internal const double DefaultAirspeedKnots = 120;
    internal const double GForceFullMarks = 1.2;
    internal const double GForceZero = 1.5;
    internal const double CenterlineFullMarksMeters = 1;
    internal const double CenterlineZeroMeters = 20;
    internal const double CrabFullMarksDegrees = .5;
    internal const double CrabZeroDegrees = 5;
    /// <summary>Banking into at least this crosswind component widens the bank curve.</summary>
    internal const double CrosswindBankKnots = 5;
    /// <summary>A touchdown beyond this fraction of the runway length is penalised.</summary>
    internal const double LateTouchdownFraction = .6;
    internal const double CrabSampleSeconds = 0.5;
    private static readonly IReadOnlyDictionary<LandingScoreCriterion, double> Weights = new Dictionary<LandingScoreCriterion, double>
    {
        [LandingScoreCriterion.VerticalSpeed] = 30, [LandingScoreCriterion.GForce] = 15, [LandingScoreCriterion.TouchdownZone] = 15,
        [LandingScoreCriterion.Centerline] = 10, [LandingScoreCriterion.Crab] = 10, [LandingScoreCriterion.Bank] = 10, [LandingScoreCriterion.Pitch] = 10,
    };

    private LandingScore() { }

    public LandingScoreCategory Category { get; private set; }
    public IReadOnlyList<LandingScoreComponent> Components { get; private set; } = Array.Empty<LandingScoreComponent>();
    public IReadOnlyList<LandingScorePenalty> Penalties { get; private set; } = Array.Empty<LandingScorePenalty>();
    /// <summary>Sum of the whole points before penalties; null without a vertical speed.</summary>
    public double? BaseScore => Components.All(component => component.Points.HasValue) ? Components.Sum(component => component.Points!.Value) : null;
    public double Multiplier => Penalties.Aggregate(1.0, (product, penalty) => product * penalty.Multiplier);
    /// <summary>The base score times every penalty, rounded down and kept within 0–100.</summary>
    public double? Total => BaseScore is double baseScore ? Math.Max(0, Math.Min(100, Math.Floor(baseScore * Multiplier + 1e-9))) : null;
    /// <summary>Signed crab for display: positive when the nose points right of the ground track.</summary>
    public double? CrabSignedDegrees { get; private set; }

    /// <summary>
    /// Scores a first contact from its detail as the Score tab does, without preparing a replay
    /// scene; <paramref name="records"/> supplies the details of its later contacts. Null for a
    /// later contact, which is a bounce and is not scored.
    /// </summary>
    internal static LandingScore? ForFirstContact(LandingRecord record, IEnumerable<LandingRecord> records)
    {
        if (record.ContactNumber > 1) return null;
        var episode = LandingReplayContacts.ForEpisode(record, records);
        var track = LandingReplayTrack.TryCreate(record, out var created) ? created : null;
        var contactTimes = track == null ? Array.Empty<double>() : LandingReplayContacts.ContactTimes(record, track, episode);
        return Create(record, track, LandingDistanceAnalysis.Create(record, track, contactTimes));
    }

    public static LandingScore Create(LandingRecord record, LandingReplayTrack? track, LandingDistanceAnalysis? distance)
    {
        var result = new LandingScore { Category = CategoryFor(record.AirspeedKnots) };
        var components = new List<LandingScoreComponent>();
        // Like TouchdownFX, an unmeasured reading scores as the fallback value.
        void Add(LandingScoreCriterion criterion, double? value, (double X, double Score)[] curve, double? fallback) =>
            components.Add(new LandingScoreComponent(criterion, Weights[criterion], value,
                (value ?? fallback) is double scored ? Evaluate(curve, scored) : null, curve));

        Add(LandingScoreCriterion.VerticalSpeed, Finite(record.InertialFpm) ? Math.Abs(record.InertialFpm) : null,
            VerticalSpeedCurve(result.Category), null);
        var peakG = new LandingPresentation(record).ValidImpactPeakG;
        Add(LandingScoreCriterion.GForce, peakG, GForceCurve, 1);

        var lateral = distance == null ? (double?)null : Math.Abs(distance.TouchdownLateralMeters);
        if (distance != null)
        {
            var zone = TouchdownZoneCurve(distance, result.Category);
            components.Add(new LandingScoreComponent(LandingScoreCriterion.TouchdownZone, Weights[LandingScoreCriterion.TouchdownZone],
                distance.TouchdownMeters, distance.TouchdownMeters < 0 ? 0 : Evaluate(zone, distance.TouchdownMeters), zone));
        }
        else
        {
            // TouchdownFX awards no touchdown-zone points without a runway.
            components.Add(new LandingScoreComponent(LandingScoreCriterion.TouchdownZone, Weights[LandingScoreCriterion.TouchdownZone],
                null, 0, Array.Empty<(double, double)>()));
        }
        Add(LandingScoreCriterion.Centerline, lateral, CenterlineCurve, 0);

        result.CrabSignedDegrees = track == null ? null : CrabDegrees(track);
        var crab = result.CrabSignedDegrees is double signedCrab ? Math.Abs(signedCrab) : (double?)null;
        Add(LandingScoreCriterion.Crab, crab, CrabCurve, 0);
        var bank = Finite(record.BankDegrees) ? Math.Abs(record.BankDegrees) : (double?)null;
        var intoCrosswind = Finite(record.BankDegrees) && BanksIntoCrosswind(record, distance);
        Add(LandingScoreCriterion.Bank, bank, BankCurve(intoCrosswind), 0);
        // MSFS reports nose-up pitch as negative.
        Add(LandingScoreCriterion.Pitch, Finite(record.PitchDegrees) ? -record.PitchDegrees : null, PitchCurve(result.Category), 0);
        result.Components = components;
        result.Penalties = PenaltiesFor(crab ?? 0, lateral ?? 0, bank ?? 0, BankCurve(intoCrosswind).Last().X, peakG ?? 1, distance);
        return result;
    }

    private static IReadOnlyList<LandingScorePenalty> PenaltiesFor(double crab, double lateral, double bank, double bankZero, double peakG,
        LandingDistanceAnalysis? distance)
    {
        var penalties = new List<LandingScorePenalty>();
        if (crab > CrabZeroDegrees)
            penalties.Add(new LandingScorePenalty(LandingScorePenaltyKind.Crab, crab, CrabZeroDegrees, Math.Max(.4, 1 - (crab - CrabZeroDegrees) / 10 * .6)));
        if (lateral > CenterlineZeroMeters)
            penalties.Add(new LandingScorePenalty(LandingScorePenaltyKind.Centerline, lateral, CenterlineZeroMeters,
                Math.Max(.5, 1 - (lateral - CenterlineZeroMeters) / 20 * .5)));
        if (bank > bankZero)
            penalties.Add(new LandingScorePenalty(LandingScorePenaltyKind.Bank, bank, bankZero, Math.Max(.5, 1 - (bank - bankZero) / 5 * .5)));
        if (peakG > GForceZero)
            penalties.Add(new LandingScorePenalty(LandingScorePenaltyKind.GForce, peakG, GForceZero, Math.Max(.4, 1 - (peakG - GForceZero) * .6)));
        if (distance != null && distance.RunwayLengthMeters > 0)
        {
            // Measured from the physical runway end; a touchdown off either end is not penalised.
            var fraction = (distance.TouchdownMeters + distance.ThresholdOffsetMeters) / distance.RunwayLengthMeters;
            if (fraction > LateTouchdownFraction && fraction <= 1)
                penalties.Add(new LandingScorePenalty(LandingScorePenaltyKind.LateTouchdown, fraction * 100, LateTouchdownFraction * 100,
                    Math.Max(.4, 1 - (fraction - LateTouchdownFraction) / .2 * .6)));
        }
        return penalties;
    }

    /// <summary>
    /// True when the bank lowers the wing into a crosswind component of at least
    /// <see cref="CrosswindBankKnots"/> across the landing direction.
    /// </summary>
    private static bool BanksIntoCrosswind(LandingRecord record, LandingDistanceAnalysis? distance)
    {
        var speed = record.WindSpeedKnotsAtContact;
        if (distance == null || !Finite(speed) || speed <= 0 || !Finite(record.WindDirectionDegreesAtContact)) return false;
        // Positive when the wind blows from the right of the landing direction.
        var relative = (record.WindDirectionDegreesAtContact - distance.LandingHeadingTrueDegrees + 540) % 360 - 180;
        var crosswind = Math.Abs(speed * Math.Sin(Math.Abs(relative) * Math.PI / 180));
        // MSFS reports a lowered left wing as positive bank.
        return crosswind >= CrosswindBankKnots && record.BankDegrees * relative < 0;
    }

    internal static (double X, double Score)[] VerticalSpeedCurve(LandingScoreCategory category)
    {
        var (full, zero) = category switch
        {
            LandingScoreCategory.Light => (80.0, 400.0),
            LandingScoreCategory.Turboprop => (120.0, 500.0),
            LandingScoreCategory.Heavy => (240.0, 600.0),
            _ => (180.0, 600.0),
        };
        return new[] { (0.0, 100.0), (full, 100.0), (zero, 0.0) };
    }

    internal static readonly (double X, double Score)[] GForceCurve = { (0, 100), (GForceFullMarks, 100), (GForceZero, 0) };

    /// <summary>
    /// Over the distance past the landing threshold: rising from zero at the threshold to full
    /// marks around the ICAO Annex 14 aiming point for the landing distance available, then
    /// falling to zero at the earlier of a category distance past it and the runway midpoint.
    /// </summary>
    internal static (double X, double Score)[] TouchdownZoneCurve(LandingDistanceAnalysis distance, LandingScoreCategory category)
    {
        var large = IsLarge(category);
        var available = Math.Max(1, distance.AvailableLengthMeters);
        var aim = available <= 800 ? 150.0 : available <= 1200 ? 250.0 : available <= 2400 ? 300.0 : 400.0;
        var tolerance = large ? 100.0 : 150.0;
        var zero = Math.Min(distance.RunwayLengthMeters * .5 - distance.ThresholdOffsetMeters, aim + (large ? 500 : 400));
        if (zero <= 0) return new[] { (0.0, 0.0) };
        var fullEnd = Math.Min(aim + tolerance, zero);
        var fullStart = Math.Max(0, Math.Min(aim - tolerance, fullEnd));
        return fullStart > 0
            ? new[] { (0.0, 0.0), (fullStart, 100.0), (fullEnd, 100.0), (zero, 0.0) }
            : new[] { (0.0, 100.0), (fullEnd, 100.0), (zero, 0.0) };
    }

    internal static readonly (double X, double Score)[] CenterlineCurve =
        { (0, 100), (CenterlineFullMarksMeters, 100), (CenterlineZeroMeters, 0) };

    internal static readonly (double X, double Score)[] CrabCurve = { (0, 100), (CrabFullMarksDegrees, 100), (CrabZeroDegrees, 0) };

    /// <summary>Full marks to 1° and zero at 5°, or to 3° and zero at 6° when banking into a crosswind.</summary>
    internal static (double X, double Score)[] BankCurve(bool intoCrosswind) => intoCrosswind
        ? new[] { (0.0, 100.0), (3.0, 100.0), (6.0, 0.0) }
        : new[] { (0.0, 100.0), (1.0, 100.0), (5.0, 0.0) };

    /// <summary>
    /// Over nose-up pitch: full marks from 3° to 5°, or 7° for airliners and heavies, losing an
    /// eighth per degree below and a sixth per degree above.
    /// </summary>
    internal static (double X, double Score)[] PitchCurve(LandingScoreCategory category)
    {
        var top = IsLarge(category) ? 7.0 : 5.0;
        return new[] { (-5.0, 0.0), (3.0, 100.0), (top, 100.0), (top + 6, 0.0) };
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

    /// <summary>The category from contact airspeed, as TouchdownFX detects it.</summary>
    internal static LandingScoreCategory CategoryFor(double airspeedKnots)
    {
        var speed = Finite(airspeedKnots) && airspeedKnots != 0 ? airspeedKnots : DefaultAirspeedKnots;
        return speed < LightMaximumKnots ? LandingScoreCategory.Light : speed < TurbopropMaximumKnots ? LandingScoreCategory.Turboprop :
            speed < AirlinerMaximumKnots ? LandingScoreCategory.Airliner : LandingScoreCategory.Heavy;
    }

    private static bool IsLarge(LandingScoreCategory category) => category is LandingScoreCategory.Airliner or LandingScoreCategory.Heavy;

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
