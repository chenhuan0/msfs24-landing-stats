using System;
using System.Collections.Generic;
using System.Globalization;
using LandingStats.App.Settings;
using LandingStats.App.Telemetry;

namespace LandingStats.App.Presentation;

/// <summary>Where the touchdown card appears on the simulator's monitor.</summary>
internal enum TouchdownCardPosition { TopRight, TopCenter, TopLeft, BottomCenter }

internal static class TouchdownCardPositions
{
    public const TouchdownCardPosition Default = TouchdownCardPosition.TopRight;

    /// <summary>The stored name, or the default for a missing, numeric or unknown value.</summary>
    public static TouchdownCardPosition Parse(string? name)
    {
        foreach (TouchdownCardPosition position in Enum.GetValues(typeof(TouchdownCardPosition)))
            if (string.Equals(position.ToString(), name, StringComparison.Ordinal)) return position;
        return Default;
    }
}

internal sealed class TouchdownCardMetric
{
    public TouchdownCardMetric(string label, string value, string unit, double? score)
    { Label = label; Value = value; Unit = unit; Score = score; }
    public string Label { get; }
    public string Value { get; }
    public string Unit { get; }
    /// <summary>0–100 on the Score tab's curve for this criterion, or null when unavailable.</summary>
    public double? Score { get; }
}

/// <summary>
/// The four headline readings of a touchdown notice, graded on the same curves
/// as the Score tab so that the card's colors match the saved landing.
/// </summary>
internal sealed class TouchdownCard
{
    private TouchdownCard(string location, IReadOnlyList<TouchdownCardMetric> metrics) { Location = location; Metrics = metrics; }

    /// <summary>Airport and runway end, or empty when no runway contains the contact.</summary>
    public string Location { get; }
    public IReadOnlyList<TouchdownCardMetric> Metrics { get; }

    public static TouchdownCard Create(TouchdownNoticeEventArgs readings)
    {
        var category = LandingScore.CategoryFor(readings.WeightPounds, readings.AirspeedKnots, out _);
        var culture = CultureInfo.CurrentCulture;
        var meters = LocalizationManager.Text("Replay.Meters");
        var metrics = new List<TouchdownCardMetric>(4);

        var fpm = readings.VerticalSpeedFpm;
        metrics.Add(Finite(fpm)
            ? new TouchdownCardMetric(Text("TouchdownCard.VerticalSpeed"), fpm.ToString("+0;−0;0", culture), "fpm",
                LandingScore.Evaluate(LandingScore.VerticalSpeedCurve(category), Math.Abs(fpm)))
            : Unavailable("TouchdownCard.VerticalSpeed"));

        metrics.Add(readings.PeakG is double peak
            ? new TouchdownCardMetric(Text("TouchdownCard.GForce"), peak.ToString("0.00", culture), "G",
                LandingScore.Evaluate(LandingScore.GForceCurve, peak))
            : Unavailable("TouchdownCard.GForce"));

        var distance = readings.Runway == null ? null
            : LandingDistanceAnalysis.ForTouchdown(readings.Runway, readings.SecondaryEnd, readings.TouchdownMeters, readings.LateralMeters);
        if (distance != null)
        {
            var lateral = Math.Abs(distance.TouchdownLateralMeters);
            var side = lateral < .05 ? string.Empty
                : " " + LocalizationManager.Text(distance.TouchdownLateralMeters > 0 ? "Replay.RightShort" : "Replay.LeftShort");
            metrics.Add(new TouchdownCardMetric(Text("TouchdownCard.Centerline"), lateral.ToString("0.0", culture), meters + side,
                distance.OffRunwayEdge ? 0 : LandingScore.Evaluate(LandingScore.CenterlineCurve(distance.RunwayWidthMeters, category), lateral)));
            var along = distance.TouchdownMeters;
            metrics.Add(new TouchdownCardMetric(Text("TouchdownCard.FromThreshold"), along.ToString("#,0;−#,0;0", culture), meters,
                along < 0 ? 0 : LandingScore.Evaluate(LandingScore.TouchdownZoneCurve(distance), along)));
        }
        else
        {
            metrics.Add(Unavailable("TouchdownCard.Centerline"));
            metrics.Add(Unavailable("TouchdownCard.FromThreshold"));
        }

        var location = distance == null ? string.Empty
            : string.IsNullOrEmpty(distance.AirportIdent) ? LocalizationManager.Format("TouchdownCard.RunwayFormat", distance.RunwayName)
            : LocalizationManager.Format("TouchdownCard.AirportRunwayFormat", distance.AirportIdent, distance.RunwayName);
        return new TouchdownCard(location, metrics);
    }

    private static TouchdownCardMetric Unavailable(string labelKey) => new TouchdownCardMetric(Text(labelKey), "—", string.Empty, null);

    private static string Text(string key) => LocalizationManager.Text(key);

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
