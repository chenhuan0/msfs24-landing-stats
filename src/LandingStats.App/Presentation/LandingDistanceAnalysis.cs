using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.ReplayMath;

namespace LandingStats.App.Presentation;

internal enum LandingDistanceZone { BeforeThreshold, ShortOfTarget, Target, LongInTouchdownZone, BeyondTouchdownZone }

internal enum LandingAimSource { Ils, Papi, Nominal }

/// <summary>One recorded pose in the landing runway frame.</summary>
internal readonly struct LandingDistanceSample
{
    public LandingDistanceSample(double timeSeconds, double alongMeters, double lateralMeters, double heightFeet, double groundSpeedKnots, bool onGround)
    {
        TimeSeconds = timeSeconds; AlongMeters = alongMeters; LateralMeters = lateralMeters;
        HeightFeet = heightFeet; GroundSpeedKnots = groundSpeedKnots; OnGround = onGround;
    }
    public double TimeSeconds { get; }
    /// <summary>Distance past the landing threshold; negative before it.</summary>
    public double AlongMeters { get; }
    /// <summary>Positive is right of the landing direction.</summary>
    public double LateralMeters { get; }
    /// <summary>Aircraft reference height above its own height at the analysed contact.</summary>
    public double HeightFeet { get; }
    public double GroundSpeedKnots { get; }
    public bool OnGround { get; }
}

internal readonly struct LandingDistanceContact
{
    public LandingDistanceContact(int? number, double timeSeconds, double alongMeters, double lateralMeters, bool selected)
    { Number = number; TimeSeconds = timeSeconds; AlongMeters = alongMeters; LateralMeters = lateralMeters; Selected = selected; }
    public int? Number { get; }
    public double TimeSeconds { get; }
    public double AlongMeters { get; }
    public double LateralMeters { get; }
    public bool Selected { get; }
}

/// <summary>
/// Runway-relative touchdown distances derived from the recorded track and the
/// simulator runway facility. Positions are the aircraft reference point, as in
/// the 3D reconstruction, not a gear contact point. Runway slope is ignored.
/// </summary>
internal sealed class LandingDistanceAnalysis
{
    /// <summary>The target zone spans the first 1,000 ft beyond the aiming point.</summary>
    internal const double TargetZoneLengthMeters = 304.8;
    internal const double FaaTouchdownZoneMeters = 914.4;
    internal const double IcaoTouchdownZoneMeters = 900;
    internal const double FaaNominalAimingPointMeters = 310.896;
    internal const double ExitSpeedKnots = 30;
    internal const double ReferenceHeightFeet = 50;
    internal const double NominalGlidePathDegrees = 3;
    internal const double DecelerationWindowSeconds = 5;
    internal const double MinimumProjectedDeceleration = 0.3;
    private const double KnotsToMetersPerSecond = 0.514444;

    private LandingDistanceAnalysis() { }

    public string RunwayName { get; private set; } = string.Empty;
    public string AirportIdent { get; private set; } = string.Empty;
    public bool UsesFaa { get; private set; }
    public double RunwayLengthMeters { get; private set; }
    public double RunwayWidthMeters { get; private set; }
    /// <summary>True heading of the landing direction.</summary>
    public double LandingHeadingTrueDegrees { get; private set; }
    /// <summary>Displaced-threshold distance from the physical landing end.</summary>
    public double ThresholdOffsetMeters { get; private set; }
    public bool ThresholdOffsetKnown { get; private set; }
    /// <summary>Geometric pavement beyond the landing threshold; not a declared LDA.</summary>
    public double AvailableLengthMeters => RunwayLengthMeters - ThresholdOffsetMeters;
    public double AimingPointMeters { get; private set; }
    public LandingAimSource AimSource { get; private set; }
    public double GlidePathDegrees { get; private set; }
    public double TargetZoneStartMeters { get; private set; }
    public double TargetZoneEndMeters { get; private set; }
    public double TouchdownZoneEndMeters { get; private set; }

    public IReadOnlyList<LandingDistanceSample> Samples { get; private set; } = Array.Empty<LandingDistanceSample>();
    public IReadOnlyList<LandingDistanceContact> Contacts { get; private set; } = Array.Empty<LandingDistanceContact>();

    public double TouchdownMeters { get; private set; }
    public double TouchdownLateralMeters { get; private set; }
    public double AimDeviationMeters => TouchdownMeters - AimingPointMeters;
    public LandingDistanceZone Zone { get; private set; }
    public bool OffRunwayEdge => Math.Abs(TouchdownLateralMeters) > RunwayWidthMeters / 2;
    /// <summary>Pavement from the touchdown to the far physical runway end.</summary>
    public double RunwayRemainingMeters => AvailableLengthMeters - TouchdownMeters;
    public double? ThresholdCrossingHeightFeet { get; private set; }
    public double? ReferenceHeightCrossingMeters { get; private set; }
    public double? AirDistanceMeters => ReferenceHeightCrossingMeters is double crossing ? TouchdownMeters - crossing : null;
    public double? FlareStartMeters { get; private set; }
    public double? ExitSpeedMeters { get; private set; }
    public double ExitSpeedLateralMeters { get; private set; }
    public double? ExitSpeedRemainingMeters => ExitSpeedMeters is double exit ? AvailableLengthMeters - exit : null;
    /// <summary>
    /// Stored landings keep a short rollout. When 30 kt was not recorded, the
    /// point is extrapolated from the last recorded deceleration.
    /// </summary>
    public bool ExitSpeedProjected { get; private set; }
    public double? ProjectionStartMeters { get; private set; }
    public double? ProjectedDecelerationMetersPerSecondSquared { get; private set; }

    public static LandingDistanceAnalysis? Create(LandingRecord record, LandingReplayTrack? track, IReadOnlyList<double> contactTimes)
    {
        var runway = record.RunwayGeometry;
        if (track == null || runway == null || !runway.HasValidGeometry || runway.IsWater) return null;
        // Share the landing-end decision with the 3D centreline readout.
        if (!LandingReplayRunwayOffset.TryCreate(record, track, out var offset) || offset == null) return null;
        if (!track.TrySample(0, out var contact)) return null;
        var landingHeading = offset.HeadingTrueDegrees;
        var secondary = Math.Abs((landingHeading - runway.HeadingTrueDegrees + 540) % 360 - 180) > 90;
        var heading = landingHeading * Math.PI / 180;
        var forward = new Vector3D(Math.Sin(heading), 0, -Math.Cos(heading));
        var right = new Vector3D(Math.Cos(heading), 0, Math.Sin(heading));
        var center = track.ProjectGeo(runway.LatitudeDegrees, runway.LongitudeDegrees, runway.AltitudeMeters / LandingReplayTrack.FeetToMeters);
        var thresholdOffset = secondary ? runway.SecondaryThresholdOffsetMeters : runway.PrimaryThresholdOffsetMeters;
        var result = new LandingDistanceAnalysis
        {
            RunwayName = ((secondary ? runway.SecondaryName : runway.PrimaryName) ?? string.Empty).Trim(),
            AirportIdent = (runway.AirportIdent ?? string.Empty).Trim(),
            UsesFaa = RunwayMarkingConvention.UsesFaa(runway),
            RunwayLengthMeters = runway.LengthMeters,
            RunwayWidthMeters = runway.WidthMeters,
            LandingHeadingTrueDegrees = landingHeading,
            ThresholdOffsetMeters = thresholdOffset ?? 0,
            ThresholdOffsetKnown = thresholdOffset.HasValue,
        };
        var thresholdFromCenter = result.ThresholdOffsetMeters - runway.LengthMeters / 2;
        double Along(Point3D position) => Vector3D.DotProduct(position - center, forward) - thresholdFromCenter;
        double Lateral(Point3D position) => Vector3D.DotProduct(position - center, right);

        result.ResolveAim(runway, secondary);
        var contactHeight = contact.Position.Y;
        result.Samples = track.Frames.Select(frame => new LandingDistanceSample(frame.TimeSeconds, Along(frame.Position), Lateral(frame.Position),
            (frame.Position.Y - contactHeight) / LandingReplayTrack.FeetToMeters, frame.Sample.GroundSpeedKnots, frame.Sample.OnGround)).ToArray();
        result.TouchdownMeters = Along(contact.Position);
        result.TouchdownLateralMeters = Lateral(contact.Position);
        result.Zone = result.Classify(result.TouchdownMeters);

        var times = contactTimes.Where(LandingReplayTrack.Finite).Concat(new[] { 0.0 }).Distinct().OrderBy(time => time).ToArray();
        var selectedRank = Array.IndexOf(times, 0.0);
        // Contact numbers are exact only when every episode contact was mapped.
        var numbered = times.Length == record.ContactCount;
        var contacts = new List<LandingDistanceContact>();
        for (var index = 0; index < times.Length; index++)
        {
            if (!track.TrySample(times[index], out var pose)) continue;
            var selected = index == selectedRank;
            int? number = selected ? record.ContactNumber : numbered ? record.ContactNumber + index - selectedRank : null;
            contacts.Add(new LandingDistanceContact(number, times[index], Along(pose.Position), Lateral(pose.Position), selected));
        }
        result.Contacts = contacts;

        result.ResolveApproachMarkers(record, track, Along);
        result.ResolveExitSpeed(times.Length == 0 ? 0 : times[times.Length - 1]);
        return result;
    }

    /// <summary>
    /// Touchdown position and zones only, for a contact measured live before
    /// a recorded track exists.
    /// </summary>
    public static LandingDistanceAnalysis? ForTouchdown(RunwayFacility runway, bool secondary, double touchdownMeters, double lateralMeters)
    {
        if (!runway.HasValidGeometry || runway.IsWater || !LandingReplayTrack.Finite(touchdownMeters) || !LandingReplayTrack.Finite(lateralMeters)) return null;
        var thresholdOffset = secondary ? runway.SecondaryThresholdOffsetMeters : runway.PrimaryThresholdOffsetMeters;
        var result = new LandingDistanceAnalysis
        {
            RunwayName = ((secondary ? runway.SecondaryName : runway.PrimaryName) ?? string.Empty).Trim(),
            AirportIdent = (runway.AirportIdent ?? string.Empty).Trim(),
            UsesFaa = RunwayMarkingConvention.UsesFaa(runway),
            RunwayLengthMeters = runway.LengthMeters,
            RunwayWidthMeters = runway.WidthMeters,
            LandingHeadingTrueDegrees = (runway.HeadingTrueDegrees + (secondary ? 180 : 0)) % 360,
            ThresholdOffsetMeters = thresholdOffset ?? 0,
            ThresholdOffsetKnown = thresholdOffset.HasValue,
            TouchdownMeters = touchdownMeters,
            TouchdownLateralMeters = lateralMeters,
        };
        result.ResolveAim(runway, secondary);
        result.Zone = result.Classify(touchdownMeters);
        return result;
    }

    public LandingDistanceZone Classify(double alongMeters) =>
        alongMeters < 0 ? LandingDistanceZone.BeforeThreshold :
        alongMeters < TargetZoneStartMeters ? LandingDistanceZone.ShortOfTarget :
        alongMeters <= TargetZoneEndMeters ? LandingDistanceZone.Target :
        alongMeters <= TouchdownZoneEndMeters ? LandingDistanceZone.LongInTouchdownZone :
        LandingDistanceZone.BeyondTouchdownZone;

    /// <summary>Nominal glide-path height above the runway plane through the aiming point.</summary>
    public double GlidePathHeightFeet(double alongMeters) =>
        (AimingPointMeters - alongMeters) * Math.Tan(GlidePathDegrees * Math.PI / 180) / LandingReplayTrack.FeetToMeters;

    private void ResolveAim(RunwayFacility runway, bool secondary)
    {
        TouchdownZoneEndMeters = Math.Min(UsesFaa ? FaaTouchdownZoneMeters : IcaoTouchdownZoneMeters, AvailableLengthMeters / 3);
        var approach = RunwayMarkingModel.ResolveApproach(runway, secondary);
        var aim = approach == null ? double.NaN : approach.AimingFromEndMeters - ThresholdOffsetMeters;
        if (approach != null && LandingReplayTrack.Finite(aim) && aim >= 0 && aim < AvailableLengthMeters)
        {
            AimingPointMeters = aim;
            AimSource = approach.UsesIls ? LandingAimSource.Ils : LandingAimSource.Papi;
            var angle = approach.UsesIls
                ? (secondary ? runway.SecondaryIlsGlideSlope : runway.PrimaryIlsGlideSlope)?.AngleDegrees
                : (secondary ? runway.SecondaryPapi : runway.PrimaryPapi)?.AngleDegrees;
            GlidePathDegrees = angle is double value && LandingReplayTrack.Finite(value) && value > 0 && value < 10 ? value : NominalGlidePathDegrees;
        }
        else
        {
            // FAA aiming marks begin 1,020 ft past the threshold; ICAO Annex 14
            // Table 5-1 places them by landing distance available.
            var available = AvailableLengthMeters;
            AimingPointMeters = UsesFaa ? FaaNominalAimingPointMeters :
                available < 800 ? 150 : available < 1200 ? 250 : available < 2400 ? 300 : 400;
            AimingPointMeters = Math.Min(AimingPointMeters, available / 3);
            AimSource = LandingAimSource.Nominal;
            GlidePathDegrees = NominalGlidePathDegrees;
        }
        TargetZoneStartMeters = Math.Min(AimingPointMeters, TouchdownZoneEndMeters);
        TargetZoneEndMeters = Math.Max(TargetZoneStartMeters, Math.Min(AimingPointMeters + TargetZoneLengthMeters, TouchdownZoneEndMeters));
    }

    private void ResolveApproachMarkers(LandingRecord record, LandingReplayTrack track, Func<Point3D, double> along)
    {
        LandingDistanceSample? previous = null;
        foreach (var sample in Samples)
        {
            if (sample.TimeSeconds > 0) break;
            if (previous is LandingDistanceSample before &&
                sample.TimeSeconds - before.TimeSeconds <= LandingReplayTrack.MaximumInterpolationGapSeconds)
            {
                // Keep the last crossings before the analysed contact; an earlier
                // pass (for example a go-around) must not define this approach.
                if (before.AlongMeters < 0 && sample.AlongMeters >= 0)
                    ThresholdCrossingHeightFeet = Interpolate(before.HeightFeet, sample.HeightFeet, before.AlongMeters, sample.AlongMeters, 0);
                if (before.HeightFeet > ReferenceHeightFeet && sample.HeightFeet <= ReferenceHeightFeet)
                    ReferenceHeightCrossingMeters = Interpolate(before.AlongMeters, sample.AlongMeters, before.HeightFeet, sample.HeightFeet, ReferenceHeightFeet);
            }
            previous = sample;
        }
        if (record.FlareStartSeconds is double flare && LandingReplayTrack.Finite(flare) && track.TrySample(flare, out var pose))
            FlareStartMeters = along(pose.Position);
    }

    private void ResolveExitSpeed(double lastContactSeconds)
    {
        var rollout = Samples.Where(sample => sample.TimeSeconds > lastContactSeconds && sample.OnGround &&
            LandingReplayTrack.Finite(sample.GroundSpeedKnots)).ToArray();
        foreach (var sample in rollout)
        {
            if (sample.GroundSpeedKnots > ExitSpeedKnots) continue;
            ExitSpeedMeters = sample.AlongMeters;
            ExitSpeedLateralMeters = sample.LateralMeters;
            return;
        }
        if (rollout.Length < 3) return;
        // Least-squares ground-speed slope over the final recorded seconds,
        // continued at constant deceleration. Never invented across a gap.
        var end = rollout[rollout.Length - 1];
        var window = rollout.Where(sample => sample.TimeSeconds >= end.TimeSeconds - DecelerationWindowSeconds).ToArray();
        if (window.Length < 3 || end.TimeSeconds - window[0].TimeSeconds < 2) return;
        for (var index = 1; index < window.Length; index++)
            if (window[index].TimeSeconds - window[index - 1].TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds) return;
        var meanTime = window.Average(sample => sample.TimeSeconds);
        var meanSpeed = window.Average(sample => sample.GroundSpeedKnots);
        var covariance = window.Sum(sample => (sample.TimeSeconds - meanTime) * (sample.GroundSpeedKnots - meanSpeed));
        var variance = window.Sum(sample => (sample.TimeSeconds - meanTime) * (sample.TimeSeconds - meanTime));
        var deceleration = -covariance / variance * KnotsToMetersPerSecond;
        var speed = end.GroundSpeedKnots * KnotsToMetersPerSecond;
        var exitSpeed = ExitSpeedKnots * KnotsToMetersPerSecond;
        if (!LandingReplayTrack.Finite(deceleration) || deceleration < MinimumProjectedDeceleration || speed <= exitSpeed) return;
        ExitSpeedMeters = end.AlongMeters + (speed * speed - exitSpeed * exitSpeed) / (2 * deceleration);
        ExitSpeedLateralMeters = end.LateralMeters;
        ExitSpeedProjected = true;
        ProjectionStartMeters = end.AlongMeters;
        ProjectedDecelerationMetersPerSecondSquared = deceleration;
    }

    private static double Interpolate(double fromValue, double toValue, double fromKey, double toKey, double key) =>
        toKey == fromKey ? toValue : fromValue + (toValue - fromValue) * (key - fromKey) / (toKey - fromKey);
}
