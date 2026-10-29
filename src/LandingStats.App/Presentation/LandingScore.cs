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
/// published split. Each curve scores 100 within normal operation, 60 at the
/// first published flag level or SOP go-around limit, 20 at the severe level
/// where one is published and 0 at a structural or geometric limit; thresholds without a published
/// source are marked as this application's own. Scores the selected contact only.
/// </summary>
internal sealed class LandingScore
{
    /// <summary>Bump whenever a weight, curve, cap or bounce rule changes; cached history scores are then recomputed.</summary>
    internal const int RulesVersion = 3;
    internal const double LightMaximumPounds = 12500;
    internal const double RegionalMaximumPounds = 100000;
    internal const double HeavyMinimumPounds = 300000;
    internal const double TouchdownZoneOverrunMeters = 300;
    internal const double TouchdownZoneSevereMeters = 150;
    internal const double CrabSampleSeconds = 0.5;
    /// <summary>A criterion or bounce scoring below this, the first flag level, caps the total; a pass does not.</summary>
    internal const double CapBelowScore = 60;
    /// <summary>Up to <see cref="CapReleaseScore"/>, a capping score limits the total to itself plus this many points.</summary>
    internal const double WeakestCriterionMargin = 25;
    /// <summary>From this score the cap opens linearly to 100 at <see cref="CapBelowScore"/>, so it has no step.</summary>
    internal const double CapReleaseScore = 50;
    /// <summary>
    /// Highest total over the capping score. The margin and the release between 50 and the
    /// first flag are this application's own.
    /// </summary>
    internal static readonly (double X, double Score)[] CapCurve =
        { (0, WeakestCriterionMargin), (CapReleaseScore, CapReleaseScore + WeakestCriterionMargin), (CapBelowScore, 100) };
    /// <summary>FSF ALAR Briefing Note 6.4 continues the landing after a bounce up to about 5 ft and goes around above it.</summary>
    internal const double HighBounceFeet = 5;
    /// <summary>
    /// Bounce score over the highest bounce in feet. Any later contact is an FDM bounced-landing
    /// event (EASA RE30), so even a skip scores below 100; a light bounce at the 5 ft limit sits at
    /// the first flag (60) and a high bounce at the severe level (20). The skip score and the
    /// 1 ft ramp are this application's own.
    /// </summary>
    internal static readonly (double X, double Score)[] BounceScoreCurve =
        { (0, 92), (HighBounceFeet, 60), (HighBounceFeet + 1, 20) };
    /// <summary>Scored when the stored series does not reach the bounce: the light-bounce limit.</summary>
    internal const double UnmeasuredBounceScore = 60;
    /// <summary>A bounce deducts as if it were a criterion carrying this many extra points.</summary>
    internal const double BounceWeight = 25;
    /// <summary>Contacts no more than this far apart belong to one landing episode.</summary>
    internal const double MaximumBounceSeconds = 10;
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
    public double? WeightedTotal => AvailableWeight <= 0 ? null : Components.Sum(component => component.Points ?? 0) / AvailableWeight * 100;
    /// <summary>Lowest-scoring available criterion; the first in order on a tie.</summary>
    public LandingScoreComponent? Weakest => Components.Where(component => component.Score.HasValue).OrderBy(component => component.Score).FirstOrDefault();
    /// <summary>Height of each bounce, in order; null where the stored series cannot measure it.</summary>
    public IReadOnlyList<double?> BounceHeightsFeet { get; private set; } = Array.Empty<double?>();
    /// <summary>Later contacts of the same landing; each one ends a bounce after the scored first contact.</summary>
    public int BounceCount => BounceHeightsFeet.Count;
    /// <summary>Each bounce scored on <see cref="BounceScoreCurve"/>, or at the light-bounce limit when unmeasured.</summary>
    public IEnumerable<double> BounceScores => BounceHeightsFeet.Select(height =>
        height is double measured ? Evaluate(BounceScoreCurve, measured) : UnmeasuredBounceScore);
    /// <summary>100 less the shortfall of every bounce, so each bounce counts separately; not below zero.</summary>
    public double BounceScore => Math.Max(0, 100 - BounceScores.Sum(score => 100 - score));
    public double BounceDeduction => (100 - BounceScore) * BounceWeight / 100;
    /// <summary>Weighted total less the bounce deduction, before any cap.</summary>
    public double? UncappedTotal => WeightedTotal - BounceDeduction;
    /// <summary>Weighted total capped by the weakest criterion; see <see cref="CapFor"/>.</summary>
    private double? CriteriaTotal => WeightedTotal is double weighted && Weakest?.Score is double weakest
        ? Math.Min(weighted, CapFor(weakest)) : null;
    /// <summary>
    /// The capped criteria total less the bounce deduction, so a bounce always costs points,
    /// and also capped by the bounce score.
    /// </summary>
    public double? Total => CriteriaTotal is double criteria
        ? Math.Max(0, Math.Min(criteria - BounceDeduction, CapFor(BounceScore))) : null;
    public bool Capped => UncappedTotal is double uncapped && Total is double total && total < uncapped;
    /// <summary>True when the bounce, not a weighted criterion, sets the cap.</summary>
    public bool LimitedByBounce => CriteriaTotal is double criteria && CapFor(BounceScore) < criteria - BounceDeduction;
    public double? LimitingScore => LimitedByBounce ? BounceScore : Weakest?.Score;
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
        return Create(record, track, LandingDistanceAnalysis.Create(record, track, contactTimes), episode);
    }

    /// <param name="episode">Other contacts of the same landing, used to measure the bounces they end.</param>
    public static LandingScore Create(LandingRecord record, LandingReplayTrack? track, LandingDistanceAnalysis? distance,
        IEnumerable<LandingRecord>? episode = null)
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
            var centerline = CenterlineCurve(distance.RunwayWidthMeters, result.Category);
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
        result.BounceHeightsFeet = BounceHeights(record, episode ?? Array.Empty<LandingRecord>());
        return result;
    }

    /// <summary>
    /// Each bounce is measured on the contact that ends it, whose series holds the airborne
    /// stretch just before it, and otherwise on the matching airborne stretch after the first contact.
    /// </summary>
    private static IReadOnlyList<double?> BounceHeights(LandingRecord first, IEnumerable<LandingRecord> episode)
    {
        var contacts = episode.Where(contact => contact.ContactNumber > 1 && contact.ContactNumber <= first.ContactCount)
            .GroupBy(contact => contact.ContactNumber).ToDictionary(group => group.Key, group => group.First());
        var afterFirst = HeightsAfterContact(first);
        var heights = new double?[Math.Max(0, first.ContactCount - 1)];
        for (var index = 0; index < heights.Length; index++)
        {
            var previous = index == 0 ? first : contacts.TryGetValue(index + 1, out var before) ? before : null;
            heights[index] = contacts.TryGetValue(index + 2, out var ending) ? HeightBeforeContact(ending, previous) : null;
            heights[index] ??= index < afterFirst.Count ? afterFirst[index] : null;
        }
        return heights;
    }

    /// <summary>Highest height above the first contact in each airborne stretch after being on the ground.</summary>
    private static IReadOnlyList<double> HeightsAfterContact(LandingRecord record)
    {
        var heights = new List<double>();
        double? reference = null, highest = null;
        var landed = false;
        foreach (var point in record.Series.Where(point => point.TimeSeconds >= 0 && Finite(point.AglFeet)).OrderBy(point => point.TimeSeconds))
        {
            reference ??= point.AglFeet;
            if (point.OnGround)
            {
                landed = true;
                if (highest is double ended) heights.Add(ended);
                highest = null;
            }
            else if (landed) highest = Math.Max(highest ?? 0, point.AglFeet - reference.Value);
        }
        if (highest is double open) heights.Add(open);
        return heights;
    }

    /// <summary>
    /// Highest height above this contact while airborne since the previous one: back to the
    /// previous contact's clock when both are recorded, otherwise back to the last sample on the
    /// ground within <see cref="MaximumBounceSeconds"/>. Null when neither bounds the bounce.
    /// </summary>
    private static double? HeightBeforeContact(LandingRecord contact, LandingRecord? previous)
    {
        var reference = contact.Series.Where(point => point.TimeSeconds >= 0 && Finite(point.AglFeet))
            .OrderBy(point => point.TimeSeconds).Select(point => (double?)point.AglFeet).FirstOrDefault();
        if (reference == null) return null;
        var previousClock = ContactClock(previous);
        var contactClock = ContactClock(contact);
        var clocked = previousClock.HasValue && contactClock.HasValue;
        var earliest = clocked ? previousClock!.Value - contactClock!.Value : -MaximumBounceSeconds;
        var highest = 0.0;
        foreach (var point in contact.Series.Where(point => point.TimeSeconds < 0 && point.TimeSeconds > earliest && Finite(point.AglFeet))
                     .OrderByDescending(point => point.TimeSeconds))
        {
            if (point.OnGround) return highest;
            highest = Math.Max(highest, point.AglFeet - reference.Value);
        }
        return clocked ? highest : null;
    }

    private static double? ContactClock(LandingRecord? record) =>
        record?.EstimatedContactTimeSeconds is double clock && Finite(clock) && clock != LandingRecord.NonFiniteStorageSentinel ? clock : null;

    internal static (double X, double Score)[] VerticalSpeedCurve(LandingScoreCategory category)
    {
        // 2 ft/s is the mean sink speed measured in FAA video landing surveys;
        // Boeing reports that crews and cabin staff typically report a hard
        // landing near 4 ft/s; FAR 25.473 designs the gear for 6 ft/s at design
        // takeoff weight and 10 ft/s at design landing weight. Light aircraft
        // scale the curve to the 7 ft/s minimum of FAR 23.473(d).
        var scale = category == LandingScoreCategory.Light ? .7 : 1;
        return new[] { (0.0, 100.0), (120 * scale, 100.0), (240 * scale, 60.0), (360 * scale, 20.0), (600 * scale, 0.0) };
    }

    // ATR's FDM guidance raises its first touchdown-acceleration event at
    // 1.4 G, CAAC's FOQA specification calls 1.8 G a severe deviation, and the
    // 737 maintenance manual indicates a hard landing above 2.2 G.
    internal static readonly (double X, double Score)[] GForceCurve =
        { (0, 100), (AnalysisNumericPolicy.SmoothImpactG, 100), (1.4, 60), (1.8, 20), (2.2, 0) };

    internal static (double X, double Score)[] TouchdownZoneCurve(LandingDistanceAnalysis distance)
    {
        // Full marks across the target zone, the first 1,000 ft beyond the
        // aiming point: GAPPRE's normal touchdown area of 300-600 m, which held
        // about 90% of A320 and 737 line landings in DOT/FAA/AR-07/7. GAPPRE's
        // recommended SOP goes around when not touched down within the
        // touchdown zone, so its end is the pass mark. On short runways a third
        // of the remaining zone is kept for the decline. The short side and the
        // overrun distances are this application's own.
        var end = distance.TouchdownZoneEndMeters;
        var aim = distance.TargetZoneStartMeters;
        var full = Math.Min(distance.TargetZoneEndMeters, end - (end - aim) / 3);
        return new[]
        {
            (0.0, 20.0), (aim, 100.0), (full, 100.0), (end, 60.0), (end + TouchdownZoneSevereMeters, 20.0),
            (end + TouchdownZoneOverrunMeters, 0.0),
        };
    }

    /// <summary>
    /// Main gear track of the most numerous type of each category in the HAW Hamburg aircraft
    /// database, compiled from manufacturer airport planning documents: Twin Otter, ATR 72,
    /// 737-800 and 777-300ER.
    /// </summary>
    internal static double MainGearTrackMeters(LandingScoreCategory category) => category switch
    {
        LandingScoreCategory.Light => 3.7,
        LandingScoreCategory.Regional => 4.1,
        LandingScoreCategory.Heavy => 10.97,
        _ => 5.72,
    };

    internal static (double X, double Score)[] CenterlineCurve(double runwayWidthMeters, LandingScoreCategory category)
    {
        // GAPPRE's recommended SOP goes around when all main gear are on one
        // side of the centreline; the pass mark is where the gear centre
        // crosses it, half the category's main gear track. Zero where an
        // A320's outer main wheel, 4.5 m out, would use up the 4.5 m edge
        // clearance that ICAO runway widths provide. No level in between is
        // published, so the score falls linearly. The full-marks band, 1 m or
        // half the pass mark, is this application's own.
        var straddle = MainGearTrackMeters(category) / 2;
        var zero = Math.Max(8, runwayWidthMeters / 2 - 9);
        return new[] { (0.0, 100.0), (Math.Min(1, straddle / 2), 100.0), (straddle, 60.0), (zero, 0.0) };
    }

    // No published limit; this application's own. Boeing does not recommend a
    // crab-only touchdown on a dry runway.
    internal static readonly (double X, double Score)[] CrabCurve = { (0, 100), (1, 100), (3, 60), (5, 20), (8, 0) };

    // Boeing's 2.2 G hard-landing indication assumes no more than 2° of roll;
    // CAAC flags roll from 50 ft to touchdown above 4° and 6°.
    internal static readonly (double X, double Score)[] BankCurve = { (0, 100), (2, 100), (4, 60), (6, 20), (8, 0) };

    internal static (double X, double Score)[] PitchCurve(LandingScoreCategory category)
    {
        // CAAC flags touchdown pitch below 1.0° and 0.5°, and above 80% and 90%
        // of the tail-strike attitude. The nose-up band and the generic
        // tail-strike attitude per category are this application's own.
        var (low, high, tailStrike) = category switch
        {
            LandingScoreCategory.Light => (3.0, 10.0, 15.0),
            LandingScoreCategory.Regional => (2.0, 6.5, 9.0),
            LandingScoreCategory.Heavy => (2.5, 6.0, 9.0),
            _ => (3.0, 6.5, 10.0),
        };
        return new[]
        {
            (0.0, 0.0), (0.5, 20.0), (1.0, 60.0), (low, 100.0), (high, 100.0),
            (tailStrike * .8, 60.0), (tailStrike * .9, 20.0), (tailStrike, 0.0),
        };
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
        Category = CategoryFor(record.WeightPounds, record.AirspeedKnots, out var fromWeight);
        CategoryFromWeight = fromWeight;
    }

    /// <summary>The category from landing weight, otherwise from contact airspeed.</summary>
    internal static LandingScoreCategory CategoryFor(double weightPounds, double airspeedKnots, out bool fromWeight)
    {
        fromWeight = Finite(weightPounds) && weightPounds > 0;
        if (fromWeight)
            return weightPounds < LightMaximumPounds ? LandingScoreCategory.Light :
                weightPounds < RegionalMaximumPounds ? LandingScoreCategory.Regional :
                weightPounds >= HeavyMinimumPounds ? LandingScoreCategory.Heavy : LandingScoreCategory.Airliner;
        var speed = Finite(airspeedKnots) ? airspeedKnots : 0;
        return speed < 90 ? LandingScoreCategory.Light : speed < 115 ? LandingScoreCategory.Regional :
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

    /// <summary>The highest total a score allows on <see cref="CapCurve"/>; a passing score sets no limit.</summary>
    internal static double CapFor(double score) => score < CapBelowScore ? Evaluate(CapCurve, score) : double.PositiveInfinity;

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
