using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

/// <summary>Total touchdown rating beside one bar per criterion, each with its rule.</summary>
public sealed class LandingScoreView : FrameworkElement
{
    private const double SummaryLeft = 24, SummaryWidth = 280, RowsLeft = 336, RightPad = 16, HeaderHeight = 22;
    private static readonly Brush GridBrush = Brush("#201E1C");
    private static readonly Brush MutedBrush = Brush("#8A837B");
    private static readonly Brush SubtleBrush = Brush("#6B655F");
    private static readonly Brush TextBrush = Brush("#F5F3F0");
    private static readonly Brush AmberBrush = Brush("#D9C46A");
    private static readonly Brush TrackBrush = Brush("#1F1D1B");
    // One color per grade, from Excellent to Bad, with a faint tint for badges.
    private static readonly string[] GradeNames = { "Excellent", "Good", "Fair", "Poor", "Bad" };
    private static readonly string[] GradeColors = { "7FD9A4", "B9D872", "E0C25E", "F09A5A", "FF7B6B" };
    private static readonly Brush[] GradeBrushes = GradeColors.Select(color => Brush("#" + color)).ToArray();
    private static readonly Brush[] GradeTints = GradeColors.Select(color => Brush("#26" + color)).ToArray();
    private static readonly Pen GridPen = Pen(GridBrush, 1);
    private static readonly Pen TickPen = Pen(Brush("#34302D"), 1);

    private LandingRecord? _record;
    private LandingScore? _score;
    private LandingDistanceAnalysis? _distance;
    private DrawingGroup? _staticDrawing;
    private Size _drawingSize;
    private string? _drawingLanguage;
    private double _drawingDpi;

    internal LandingScore? Score => _score;

    /// <summary>A later contact of a bounced landing: recorded as a bounce and not scored.</summary>
    internal bool IsBounceContact => _record is { ContactNumber: > 1 };

    public void SetRecord(LandingRecord? record, IEnumerable<LandingRecord>? contacts = null)
    {
        _record = record;
        _score = null;
        _distance = null;
        if (record != null && !IsBounceContact)
        {
            var prepared = ReplayPresentationData.Prepare(record, contacts ?? Array.Empty<LandingRecord>());
            _distance = LandingDistanceAnalysis.Create(record, prepared.Track, prepared.ContactTimes);
            _score = LandingScore.Create(record, prepared.Track, _distance,
                LandingReplayContacts.ForEpisode(record, contacts ?? Array.Empty<LandingRecord>()));
        }
        Invalidate();
    }

    public void RefreshLocalization() => Invalidate();

    private void Invalidate() { _staticDrawing = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var language = LocalizationManager.EffectiveLanguage;
        if (_staticDrawing == null || _drawingSize != RenderSize || _drawingLanguage != language || _drawingDpi != dpi)
        {
            _drawingSize = RenderSize; _drawingLanguage = language; _drawingDpi = dpi;
            _staticDrawing = new DrawingGroup();
            using (var drawing = _staticDrawing.Open()) BuildStaticDrawing(drawing);
            _staticDrawing.Freeze();
        }
        context.DrawDrawing(_staticDrawing);
    }

    private void BuildStaticDrawing(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var record = _record;
        var score = _score;
        if (record != null && IsBounceContact)
        {
            var message = Formatted(Format("Score.BounceContactFormat", record.ContactNumber, record.ContactCount), MutedBrush, 12);
            message.MaxTextWidth = Math.Max(1, ActualWidth - 2 * SummaryLeft);
            context.DrawText(message, new Point(SummaryLeft, 18));
            return;
        }
        if (record == null || score == null) return;
        if (ActualWidth < 640 || ActualHeight < 220 || score.Total is not double total)
        {
            context.DrawText(Formatted(LocalizationManager.Text("Chart.NoData"), MutedBrush, 12), new Point(96, 18));
            return;
        }
        DrawSummary(context, record, score, total);
        DrawRows(context, record, score);
    }

    private void DrawSummary(DrawingContext context, LandingRecord record, LandingScore score, double total)
    {
        var brush = ScoreBrush(total);
        var y = 4.0;
        context.DrawText(Formatted(Text("Score.Title"), MutedBrush, 10, FontWeights.SemiBold), new Point(SummaryLeft, y));
        y += 16;
        var number = Formatted(Math.Round(total).ToString("0", CultureInfo.CurrentCulture), brush, 64, FontWeights.SemiBold);
        context.DrawText(number, new Point(SummaryLeft - 3, y));
        var outOf = Formatted(Text("Score.OutOf"), MutedBrush, 14);
        context.DrawText(outOf, new Point(SummaryLeft + number.Width + 4, y + number.Baseline - outOf.Baseline));
        y += number.Height;
        var grade = Formatted(Text("Score.Grade." + Grade(total)), brush, 16, FontWeights.SemiBold);
        context.DrawText(grade, new Point(SummaryLeft, y));
        y += grade.Height + 12;

        var category = Text("Score.Category." + score.Category);
        var basis = score.CategoryFromWeight
            ? Format("Score.CategoryWeightFormat", category, record.WeightPounds * 0.45359237)
            : Format("Score.CategorySpeedFormat", category, record.AirspeedKnots);
        y = DrawWrapped(context, basis, MutedBrush, 11, y) + 6;
        var available = score.Components.Count(component => component.Score.HasValue);
        y = DrawWrapped(context, Format("Score.CriteriaFormat", available, score.Components.Count, score.AvailableWeight),
            available == score.Components.Count ? MutedBrush : AmberBrush, 11, y) + 6;
        if (score.BounceDeduction > 0)
            y = DrawWrapped(context, Format("Score.BounceDeductedFormat", score.BounceCount, score.BounceDeduction), AmberBrush, 11, y) + 6;
        if (score.Capped && score.LimitingScore is double limitingScore && score.Weakest is { } weakest)
            y = DrawWrapped(context, Format("Score.CappedFormat",
                Text("Score.Criterion." + (score.LimitedByBounce ? "Bounce" : weakest.Criterion.ToString())), limitingScore,
                LandingScore.CapFor(limitingScore), score.UncappedTotal!.Value, LandingScore.CapBelowScore), AmberBrush, 11, y) + 6;
        if (record.ContactCount > 1)
            y = DrawWrapped(context, Format("Score.ContactFormat", record.ContactCount), AmberBrush, 11, y) + 6;
        DrawWrapped(context, Text("Score.Basis"), SubtleBrush, 10, y + 6);
        context.DrawLine(GridPen, new Point(RowsLeft - 18, 4), new Point(RowsLeft - 18, ActualHeight - 8));
    }

    private void DrawRows(DrawingContext context, LandingRecord record, LandingScore score)
    {
        var right = ActualWidth - RightPad;
        var width = right - RowsLeft;
        var nameWidth = Math.Min(260, width * .3);
        var valueLeft = RowsLeft + nameWidth + 12;
        var valueWidth = 120.0;
        var pointsWidth = 84.0;
        var barLeft = valueLeft + valueWidth + 8;
        var barRight = right - pointsWidth - 16;
        context.DrawText(Formatted(Text("Score.HeaderCriterion"), SubtleBrush, 9, FontWeights.SemiBold), new Point(RowsLeft, 4));
        context.DrawText(Formatted(Text("Score.HeaderMeasured"), SubtleBrush, 9, FontWeights.SemiBold), new Point(valueLeft, 4));
        context.DrawText(Formatted(Text("Score.HeaderScore"), SubtleBrush, 9, FontWeights.SemiBold), new Point(barLeft, 4));
        var pointsHeader = Formatted(Text("Score.HeaderPoints"), SubtleBrush, 9, FontWeights.SemiBold);
        context.DrawText(pointsHeader, new Point(right - pointsHeader.Width, 4));

        var rows = score.Components.Select(component => (
            Name: Text("Score.Criterion." + component.Criterion), Rule: Rule(component), Measured: Measured(component, record, score),
            component.Score,
            Points: component.Points is double earned
                ? Format("Score.PointsFormat", earned, component.Weight)
                : Format("Score.PointsUnavailableFormat", component.Weight),
            Bar: true)).ToList();
        // The bounce has no weight: it deducts from the total and takes part in the cap.
        var curve = LandingScore.BounceScoreCurve;
        rows.Add((Text("Score.Criterion.Bounce"),
            Format("Score.Rule.BounceFormat", curve[0].Score, curve[1].Score, curve[1].X, curve[2].Score, curve[2].X),
            score.BounceCount == 0 ? Text("Score.BounceNone") : Format("Score.BounceMeasuredFormat", string.Join(" / ",
                score.BounceHeightsFeet.Select(height => height is double feet ? feet.ToString("0.0#", CultureInfo.CurrentCulture) : Text("Score.NotAvailable")))),
            score.BounceScore, Format("Score.DeductionFormat", score.BounceDeduction), true));

        var rowHeight = Math.Max(40, Math.Min(66, (ActualHeight - HeaderHeight - 6) / rows.Count));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var top = HeaderHeight + index * rowHeight;
            context.DrawLine(GridPen, new Point(RowsLeft, top), new Point(right, top));
            var middle = top + rowHeight / 2;
            var name = Trimmed(Formatted(row.Name, TextBrush, 12, FontWeights.SemiBold), nameWidth);
            var rule = Trimmed(Formatted(row.Rule, SubtleBrush, 10), nameWidth);
            var block = name.Height + 2 + rule.Height;
            context.DrawText(name, new Point(RowsLeft, middle - block / 2));
            context.DrawText(rule, new Point(RowsLeft, middle - block / 2 + name.Height + 2));

            var brush = row.Score is double value ? ScoreBrush(value) : SubtleBrush;
            var valueBrush = row.Score.HasValue || !row.Bar ? TextBrush : SubtleBrush;
            var measured = Trimmed(FormattedMono(row.Measured, valueBrush, 14), valueWidth);
            context.DrawText(measured, new Point(valueLeft, middle - measured.Height / 2));

            if (row.Bar && barRight - barLeft > 40)
            {
                var bar = new Rect(barLeft, middle - 4, barRight - barLeft, 8);
                context.DrawRoundedRectangle(TrackBrush, null, bar, 4, 4);
                foreach (var tick in new[] { .5, .75 })
                    context.DrawLine(TickPen, new Point(bar.Left + bar.Width * tick, bar.Top - 3), new Point(bar.Left + bar.Width * tick, bar.Bottom + 3));
                if (row.Score is double scored && scored > 0)
                    context.DrawRoundedRectangle(brush, null, new Rect(bar.Left, bar.Top, Math.Max(8, bar.Width * scored / 100), bar.Height), 4, 4);
                var label = FormattedMono(row.Score is double shown ? shown.ToString("0", CultureInfo.CurrentCulture) : Text("Score.NotAvailable"), brush, 10);
                context.DrawText(label, new Point(bar.Right + 6, middle - label.Height / 2));
            }

            var points = FormattedMono(row.Points, valueBrush, 13);
            context.DrawText(points, new Point(right - points.Width, middle - points.Height / 2));
        }
        var bottom = HeaderHeight + rows.Count * rowHeight;
        context.DrawLine(GridPen, new Point(RowsLeft, bottom), new Point(right, bottom));
    }

    private string Measured(LandingScoreComponent component, LandingRecord record, LandingScore score)
    {
        if (component.Value is not double value) return "—";
        return component.Criterion switch
        {
            LandingScoreCriterion.VerticalSpeed => (-record.InertialFpm).ToString("+0;-0;0", CultureInfo.CurrentCulture) + " fpm",
            LandingScoreCriterion.GForce => value.ToString("0.00", CultureInfo.CurrentCulture) + " G",
            LandingScoreCriterion.TouchdownZone => value.ToString("N0", CultureInfo.CurrentCulture) + " " + Text("Replay.Meters"),
            LandingScoreCriterion.Centerline => Sided(value, _distance?.TouchdownLateralMeters ?? 0, Text("Replay.Meters"), .05),
            LandingScoreCriterion.Crab => Sided(value, score.CrabSignedDegrees ?? 0, "°", .05),
            // MSFS reports a lowered left wing as positive bank.
            LandingScoreCriterion.Bank => Sided(value, -record.BankDegrees, "°", .05),
            _ => value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°",
        };
    }

    private static string Sided(double magnitude, double signed, string unit, double tolerance) =>
        magnitude.ToString("0.0", CultureInfo.CurrentCulture) + (unit == "°" ? unit : " " + unit) +
        (magnitude < tolerance ? string.Empty : " " + Text(signed > 0 ? "Replay.RightShort" : "Replay.LeftShort"));

    private string Rule(LandingScoreComponent component)
    {
        var curve = component.Curve;
        if (curve.Count == 0)
            return Text(component.Criterion == LandingScoreCriterion.TouchdownZone || component.Criterion == LandingScoreCriterion.Centerline
                ? "Score.NeedsRunway" : "Score.NotAvailable");
        if (component.Score == null)
            return Text(component.Criterion == LandingScoreCriterion.Crab ? "Score.NeedsTrack" :
                component.Criterion == LandingScoreCriterion.GForce ? "Score.NeedsImpact" : "Score.NotAvailable");
        return component.Criterion switch
        {
            LandingScoreCriterion.VerticalSpeed => Format("Score.Rule.VerticalSpeedFormat", curve[1].X, curve[2].X, curve[3].X, curve[4].X),
            LandingScoreCriterion.GForce => Format("Score.Rule.GForceFormat", curve[1].X, curve[2].X, curve[3].X, curve[4].X),
            LandingScoreCriterion.TouchdownZone => Format("Score.Rule.TouchdownZoneFormat", curve[1].X, curve[2].X, curve[3].X, curve[4].X),
            LandingScoreCriterion.Centerline => Format("Score.Rule.CenterlineFormat", curve[1].X, curve[2].X, curve[3].X),
            LandingScoreCriterion.Crab => Format("Score.Rule.AngleFormat", curve[1].X, curve[2].X, curve[3].X, curve[4].X),
            LandingScoreCriterion.Bank => Format("Score.Rule.AngleFormat", curve[1].X, curve[2].X, curve[3].X, curve[4].X),
            _ => Format("Score.Rule.PitchFormat", curve[3].X, curve[4].X, curve[2].X, curve[5].X),
        };
    }

    internal static string Grade(double total) => GradeNames[GradeIndex(total)];

    internal static Brush ScoreBrush(double score) => GradeBrushes[GradeIndex(score)];

    internal static Brush ScoreTintBrush(double score) => GradeTints[GradeIndex(score)];

    private static int GradeIndex(double score) => score >= 90 ? 0 : score >= 80 ? 1 : score >= 65 ? 2 : score >= 50 ? 3 : 4;

    internal static Brush BounceBrush => AmberBrush;

    internal static Brush NeutralBrush => SubtleBrush;

    internal static Brush NeutralTintBrush => TrackBrush;

    private double DrawWrapped(DrawingContext context, string text, Brush brush, double size, double y)
    {
        var formatted = Formatted(text, brush, size);
        formatted.MaxTextWidth = SummaryWidth;
        context.DrawText(formatted, new Point(SummaryLeft, y));
        return y + formatted.Height;
    }

    private static string Text(string key) => LocalizationManager.Text(key);

    private static string Format(string key, params object[] arguments) => LocalizationManager.Format(key, arguments);

    private static FormattedText Trimmed(FormattedText text, double width)
    {
        text.MaxTextWidth = Math.Max(1, width);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        return text;
    }

    private FormattedText Formatted(string text, Brush brush, double size, FontWeight? weight = null) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private FormattedText FormattedMono(string text, Brush brush, double size) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Cascadia Mono"),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
