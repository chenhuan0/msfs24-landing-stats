using System;
using System.Collections.Generic;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

/// <summary>
/// Preliminary first-contact readings for the touchdown card, taken from the
/// live frames about a second after contact. The saved landing is analysed
/// later from the complete capture and may differ slightly.
/// </summary>
internal sealed class TouchdownNoticeEventArgs : EventArgs
{
    public TouchdownNoticeEventArgs(long episodeId, DateTime observedUtc, double verticalSpeedFpm, double? peakG,
        double weightPounds, double airspeedKnots, RunwayFacility? runway, bool secondaryEnd, double touchdownMeters, double lateralMeters)
    {
        EpisodeId = episodeId; ObservedUtc = observedUtc; VerticalSpeedFpm = verticalSpeedFpm; PeakG = peakG;
        WeightPounds = weightPounds; AirspeedKnots = airspeedKnots; Runway = runway; SecondaryEnd = secondaryEnd;
        TouchdownMeters = touchdownMeters; LateralMeters = lateralMeters;
    }

    public long EpisodeId { get; }
    /// <summary>Journal time of the frame that completed the readings.</summary>
    public DateTime ObservedUtc { get; }
    /// <summary>World vertical speed of the last airborne frame; negative while descending, NaN without one.</summary>
    public double VerticalSpeedFpm { get; }
    /// <summary>Peak of the initial load response, or null without valid telemetry.</summary>
    public double? PeakG { get; }
    public double WeightPounds { get; }
    public double AirspeedKnots { get; }
    /// <summary>A detached copy of the runway containing the contact, or null when none is known.</summary>
    public RunwayFacility? Runway { get; }
    /// <summary>True when the landing direction is the runway's secondary end.</summary>
    public bool SecondaryEnd { get; }
    public string RunwayName => ((SecondaryEnd ? Runway?.SecondaryName : Runway?.PrimaryName) ?? string.Empty).Trim();
    /// <summary>Distance of the aircraft reference point past the landing threshold; NaN without a runway.</summary>
    public double TouchdownMeters { get; }
    /// <summary>Positive right of the landing direction; NaN without a runway.</summary>
    public double LateralMeters { get; }
}

internal static class TouchdownNoticeBuilder
{
    /// <summary>An impact still developing after this long is reported with its peak so far.</summary>
    internal const double MaximumWaitSeconds = 2.0;
    /// <summary>Approach history kept for the ground track, as in <see cref="RunwayResolver"/>.</summary>
    private const double TrackSeconds = 2.0;
    private const double MaximumTrackGapSeconds = .5;

    /// <summary>
    /// Returns null while the initial load response is still developing.
    /// <paramref name="contactIndex"/> is the airborne-to-ground frame in <paramref name="samples"/>.
    /// </summary>
    internal static TouchdownNoticeEventArgs? TryCreate(long episodeId, DateTime observedUtc, IReadOnlyList<TelemetrySample> samples,
        int contactIndex, IReadOnlyList<RunwayFacility> runways)
    {
        if (contactIndex < 0 || contactIndex >= samples.Count) return null;
        var contactTime = TelemetryAnalysisFrames.TimeOf(samples[contactIndex]);
        if (!Finite(contactTime)) return null;
        var start = contactIndex;
        while (start > 0 && contactTime - TelemetryAnalysisFrames.TimeOf(samples[start - 1]) <= TrackSeconds) start--;
        var window = new List<TelemetrySample>(samples.Count - start);
        for (var index = start; index < samples.Count; index++) window.Add(samples[index]);
        var prepared = TelemetryAnalysisFrames.Prepare(window);
        var contact = -1;
        for (var index = 0; index < prepared.Count && contact < 0; index++)
            if (prepared[index].OnGround && prepared[index].MotionSimulation && TelemetryAnalysisFrames.TimeOf(prepared[index]) == contactTime) contact = index;
        if (contact < 0) return null;

        var impact = TouchdownImpactAnalysis.Analyze(prepared, contact);
        var elapsed = TelemetryAnalysisFrames.TimeOf(prepared[prepared.Count - 1]) - contactTime;
        var invalid = impact.Status == TouchdownImpactStatus.InvalidTelemetry || impact.Status == TouchdownImpactStatus.Unavailable;
        if (!impact.IsComplete && !invalid && elapsed < MaximumWaitSeconds) return null;
        // Same validity rule as the headline peak G of a saved landing.
        double? peakG = !invalid && Finite(impact.PeakG) && impact.PeakG > 0 ? impact.PeakG : null;

        var touchdown = prepared[contact];
        var lastAirborne = contact > 0 && !prepared[contact - 1].OnGround && prepared[contact - 1].MotionSimulation ? prepared[contact - 1] : null;
        var verticalSpeedFpm = lastAirborne != null && Finite(lastAirborne.VelocityWorldYFps) ? lastAirborne.VelocityWorldYFps * 60 : double.NaN;

        RunwayFacility? runway = null;
        var secondary = false;
        double along = double.NaN, lateral = double.NaN;
        if (TryTrackHeading(prepared, contact, out var heading) &&
            RunwayResolver.Select(heading, touchdown.LatitudeDegrees, touchdown.LongitudeDegrees, runways) is RunwayFacility match)
        {
            runway = match.Snapshot();
            secondary = Math.Abs(RunwayResolver.NormalizeAngle(heading - runway.HeadingTrueDegrees)) > 90;
            // As in the replay track, project the runway centre into the tangent
            // plane at the contact, so the readings match the Distance tab.
            LocalOffsetMeters(touchdown.LatitudeDegrees, touchdown.LongitudeDegrees, runway.LatitudeDegrees, runway.LongitudeDegrees,
                out var east, out var north);
            var axis = runway.HeadingTrueDegrees * Math.PI / 180;
            var primaryAlong = -(east * Math.Sin(axis) + north * Math.Cos(axis));
            var primaryRight = -(east * Math.Cos(axis) - north * Math.Sin(axis));
            // Measured from the landing threshold, including a recorded displacement.
            along = secondary
                ? runway.LengthMeters / 2 - (runway.SecondaryThresholdOffsetMeters ?? 0) - primaryAlong
                : primaryAlong + runway.LengthMeters / 2 - (runway.PrimaryThresholdOffsetMeters ?? 0);
            lateral = secondary ? -primaryRight : primaryRight;
        }
        return new TouchdownNoticeEventArgs(episodeId, observedUtc, verticalSpeedFpm, peakG, touchdown.TotalWeightPounds,
            touchdown.IndicatedAirspeedKnots, runway, secondary, along, lateral);
    }

    /// <summary>Ground track over the last two seconds up to contact, never the nose heading.</summary>
    private static bool TryTrackHeading(IReadOnlyList<TelemetrySample> samples, int contact, out double heading)
    {
        heading = 0;
        var contactTime = TelemetryAnalysisFrames.TimeOf(samples[contact]);
        var first = contact;
        for (var index = contact; index >= 0; index--)
        {
            var sample = samples[index];
            var time = TelemetryAnalysisFrames.TimeOf(sample);
            if (contactTime - time > TrackSeconds) break;
            if (!sample.MotionSimulation || !RunwayResolver.ValidPosition(sample.LatitudeDegrees, sample.LongitudeDegrees) ||
                (index < contact && (time >= TelemetryAnalysisFrames.TimeOf(samples[index + 1]) ||
                                     TelemetryAnalysisFrames.TimeOf(samples[index + 1]) - time > MaximumTrackGapSeconds))) break;
            first = index;
        }
        var from = samples[first];
        var to = samples[contact];
        var elapsed = contactTime - TelemetryAnalysisFrames.TimeOf(from);
        if (elapsed < .2 || !RunwayResolver.ValidPosition(to.LatitudeDegrees, to.LongitudeDegrees)) return false;
        RunwayResolver.OffsetMeters(from.LatitudeDegrees, from.LongitudeDegrees, to.LatitudeDegrees, to.LongitudeDegrees, out var east, out var north);
        var distance = Math.Sqrt(east * east + north * north);
        if (distance < 5 || distance / elapsed > 200) return false;
        heading = (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360;
        return true;
    }

    /// <summary>East and north of the second point in the WGS 84 tangent plane at the first.</summary>
    private static void LocalOffsetMeters(double latitude, double longitude, double otherLatitude, double otherLongitude,
        out double east, out double north)
    {
        var phi = latitude * Math.PI / 180;
        var lambda = longitude * Math.PI / 180;
        var origin = EarthPosition(phi, lambda);
        var point = EarthPosition(otherLatitude * Math.PI / 180, otherLongitude * Math.PI / 180);
        double dx = point.X - origin.X, dy = point.Y - origin.Y, dz = point.Z - origin.Z;
        east = -Math.Sin(lambda) * dx + Math.Cos(lambda) * dy;
        north = -Math.Sin(phi) * Math.Cos(lambda) * dx - Math.Sin(phi) * Math.Sin(lambda) * dy + Math.Cos(phi) * dz;
    }

    private static (double X, double Y, double Z) EarthPosition(double latitude, double longitude)
    {
        const double radius = 6378137, eccentricitySquared = 0.0066943799901413165;
        var sin = Math.Sin(latitude);
        var n = radius / Math.Sqrt(1 - eccentricitySquared * sin * sin);
        return (n * Math.Cos(latitude) * Math.Cos(longitude), n * Math.Cos(latitude) * Math.Sin(longitude), n * (1 - eccentricitySquared) * sin);
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
