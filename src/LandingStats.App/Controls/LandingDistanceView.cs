using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

/// <summary>
/// Runway plan and approach profile on one shared distance axis. The landing
/// direction runs left to right; the plan's lateral scale is exaggerated.
/// </summary>
public sealed class LandingDistanceView : FrameworkElement
{
    // The plot shares the 96 px label column of the main chart, lanes and timeline.
    private const double MetricsLeft = 24, PlotLeft = 96, PlotRight = 16, MetricsHeight = 60, TitleHeight = 20, SectionGap = 14, AxisHeight = 24, BaselineMargin = 22;
    private static readonly Brush GridBrush = Brush("#201E1C");
    private static readonly Brush MutedBrush = Brush("#8A837B");
    private static readonly Brush SubtleBrush = Brush("#6B655F");
    private static readonly Brush TextBrush = Brush("#F5F3F0");
    private static readonly Brush AccentBrush = Brush("#FF7A45");
    private static readonly Brush BlueBrush = Brush("#5FA8F5");
    private static readonly Brush AmberBrush = Brush("#D9C46A");
    private static readonly Brush GreenBrush = Brush("#8FD6A8");
    private static readonly Brush DangerBrush = Brush("#FF8A6A");
    private static readonly Brush TooltipBrush = Brush("#1F1D1B");
    private static readonly Brush FlareBrush = Brush("#D5A08050");
    private static readonly Brush BackgroundBrush = Brush("#0D0C0B");
    private static readonly Brush PavementBrush = Brush("#1C1A18");
    private static readonly Brush DisplacedBrush = Brush("#121110");
    private static readonly Brush PaintBrush = Brush("#CFC9C2");
    private static readonly Brush PianoBrush = Brush("#99CFC9C2");
    private static readonly Brush TdzFillBrush = Brush("#1F5FA8F5");
    private static readonly Brush TargetFillBrush = Brush("#3A8FD6A8");
    private static readonly Brush TdzColumnBrush = Brush("#0E5FA8F5");
    private static readonly Brush TargetColumnBrush = Brush("#1A8FD6A8");
    private static readonly Pen GridPen = Pen(GridBrush, 1);
    private static readonly Pen EdgePen = Pen(Brush("#34302D"), 1);
    private static readonly Pen CenterlinePen = DashedPen(Brush("#4A4540"), 1, 6, 6);
    private static readonly Pen ChevronPen = Pen(SubtleBrush, 1);
    private static readonly Pen RunwaySurfacePen = Pen(Brush("#4A4540"), 2);
    private static readonly Pen ApproachBaselinePen = DashedPen(Brush("#33302C"), 1, 2, 4);
    private static readonly Pen FlightPen = Pen(AccentBrush, 2);
    private static readonly Pen GroundPen = Pen(BlueBrush, 2);
    private static readonly Pen ProjectionPen = DashedPen(BlueBrush, 1.3, 3, 4);
    private static readonly Pen ProjectionMarkerPen = Pen(BlueBrush, 1.5);
    private static readonly Pen GlidePathPen = DashedPen(AmberBrush, 1.3, 5, 4);
    private static readonly Pen ReferencePen = DashedPen(Brush("#4A4540"), 1, 2, 3);
    private static readonly Pen TouchdownPen = DashedPen(AccentBrush, 1, 4, 3);
    private static readonly Pen TdzEdgePen = DashedPen(BlueBrush, 1, 3, 3);
    private static readonly Pen TargetEdgePen = DashedPen(GreenBrush, 1, 3, 3);
    private static readonly Pen FlarePen = DashedPen(FlareBrush, 1.1, 2, 4);
    private static readonly Pen HoverPen = Pen(Brush("#73F5F3F0"), 1);
    private static readonly Pen MarkerOutlinePen = Pen(BackgroundBrush, 1.5);
    private static readonly Pen ContactPen = Pen(AccentBrush, 1.5);
    private static readonly Pen BracketPen = Pen(MutedBrush, 1);
    private static readonly Pen ThresholdPen = Pen(PaintBrush, 2);

    private struct Layout
    {
        public bool Valid;
        public Rect Metrics, Plan, Profile;
        public double MinAlong, MaxAlong, LateralRange, MaxHeightFeet, BaseY;
        public double X(double along) => Plan.Left + (along - MinAlong) / (MaxAlong - MinAlong) * Plan.Width;
        public double Along(double x) => MinAlong + (x - Plan.Left) / Plan.Width * (MaxAlong - MinAlong);
        public double PlanY(double lateral) => Plan.Top + Plan.Height / 2 + lateral / LateralRange * Plan.Height / 2;
        public double ProfileY(double heightFeet) => BaseY - heightFeet / MaxHeightFeet * (BaseY - Profile.Top);
    }

    private LandingRecord? _record;
    private LandingDistanceAnalysis? _analysis;
    private double? _hoverTime;
    private Layout _layout;
    private DrawingGroup? _staticDrawing;
    private Size _drawingSize;
    private string? _drawingLanguage;
    private double _drawingDpi;

    public event EventHandler<LandingChartHoverEventArgs>? HoverTimeChanged;

    internal LandingDistanceAnalysis? Analysis => _analysis;

    public void SetRecord(LandingRecord? record, IEnumerable<LandingRecord>? contacts = null)
    {
        _record = record;
        _analysis = null;
        if (record != null)
        {
            // Shares the replay track and contact clocks prepared off the dispatcher.
            var prepared = ReplayPresentationData.Prepare(record, contacts ?? Array.Empty<LandingRecord>());
            _analysis = LandingDistanceAnalysis.Create(record, prepared.Track, prepared.ContactTimes);
        }
        _hoverTime = null;
        Invalidate();
    }

    public void SetHoverTime(double? timeSeconds)
    {
        if (_hoverTime == timeSeconds) return;
        _hoverTime = timeSeconds;
        InvalidateVisual();
    }

    public void RefreshLocalization() => Invalidate();

    private void Invalidate() { _staticDrawing = null; InvalidateVisual(); }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        var analysis = _analysis;
        if (analysis == null || !_layout.Valid) return;
        var position = eventArgs.GetPosition(this);
        if (position.X < _layout.Plan.Left || position.X > _layout.Plan.Right ||
            position.Y < _layout.Plan.Top || position.Y > _layout.Profile.Bottom)
        {
            ClearHover();
            return;
        }
        var along = _layout.Along(position.X);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < analysis.Samples.Count; index++)
        {
            var sample = analysis.Samples[index];
            if (sample.AlongMeters < _layout.MinAlong || sample.AlongMeters > _layout.MaxAlong) continue;
            var distance = Math.Abs(sample.AlongMeters - along);
            if (distance < bestDistance) { bestDistance = distance; best = index; }
        }
        if (best < 0) { ClearHover(); return; }
        var time = analysis.Samples[best].TimeSeconds;
        if (_hoverTime == time) return;
        _hoverTime = time;
        InvalidateVisual();
        HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(time));
    }

    protected override void OnMouseLeave(MouseEventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        ClearHover();
    }

    private void ClearHover()
    {
        if (!_hoverTime.HasValue) return;
        _hoverTime = null;
        InvalidateVisual();
        HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(null));
    }

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
        if (_analysis != null && _layout.Valid && _hoverTime.HasValue) DrawHover(context, _analysis, _hoverTime.Value);
    }

    private void BuildStaticDrawing(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _layout = default;
        if (_record == null) return;
        var analysis = _analysis;
        if (analysis == null)
        {
            var message = Formatted(LocalizationManager.Text("Distance.Unavailable"), MutedBrush, 12);
            message.MaxTextWidth = Math.Max(120, Math.Min(560, ActualWidth - PlotLeft - PlotRight));
            context.DrawText(message, new Point(PlotLeft, 18));
            return;
        }
        _layout = BuildLayout(analysis);
        if (!_layout.Valid)
        {
            context.DrawText(Formatted(LocalizationManager.Text("Chart.NoData"), MutedBrush, 12), new Point(PlotLeft, 18));
            return;
        }
        DrawMetrics(context, analysis);
        DrawDistanceGrid(context, analysis);
        DrawPlan(context, analysis);
        DrawProfile(context, analysis);
    }

    private Layout BuildLayout(LandingDistanceAnalysis analysis)
    {
        var layout = new Layout();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 420 || height < 260) return layout;
        var plotWidth = width - PlotLeft - PlotRight;
        layout.Metrics = new Rect(MetricsLeft, 0, width - MetricsLeft - PlotRight, MetricsHeight);
        var planTop = MetricsHeight + TitleHeight;
        var available = height - planTop - TitleHeight - SectionGap - AxisHeight;
        var planHeight = Math.Max(56, Math.Min(190, available * 0.4));
        layout.Plan = new Rect(PlotLeft, planTop, plotWidth, planHeight);
        var profileTop = layout.Plan.Bottom + SectionGap + TitleHeight;
        layout.Profile = new Rect(PlotLeft, profileTop, plotWidth, Math.Max(60, height - AxisHeight - profileTop));
        layout.BaseY = layout.Profile.Bottom - BaselineMargin;

        var runwayEnd = analysis.AvailableLengthMeters;
        layout.MinAlong = Math.Min(-700, -analysis.ThresholdOffsetMeters - 80);
        var furthest = analysis.Samples.Count == 0 ? runwayEnd : analysis.Samples.Max(sample => sample.AlongMeters);
        if (analysis.ExitSpeedMeters is double exitSpeed) furthest = Math.Max(furthest, exitSpeed);
        layout.MaxAlong = Math.Max(runwayEnd + 60, Math.Min(furthest + 40, runwayEnd + 600));

        // Exaggerate across-runway distances; exclude taxi movement after the exit speed.
        var half = analysis.RunwayWidthMeters / 2;
        var lateral = analysis.Samples
            .Where(sample => sample.AlongMeters >= -analysis.ThresholdOffsetMeters && sample.AlongMeters <= runwayEnd &&
                (sample.OnGround ? sample.GroundSpeedKnots > LandingDistanceAnalysis.ExitSpeedKnots : sample.HeightFeet < 100))
            .Select(sample => Math.Abs(sample.LateralMeters)).DefaultIfEmpty(0).Max();
        layout.LateralRange = Math.Max(half * 2.2, Math.Min(lateral * 1.25, half * 6));

        var observed = analysis.Samples
            .Where(sample => sample.AlongMeters >= layout.MinAlong && sample.TimeSeconds <= 0)
            .Select(sample => sample.HeightFeet).DefaultIfEmpty(0).Max();
        var glide = analysis.GlidePathHeightFeet(layout.MinAlong);
        var maximum = Math.Max(60, Math.Min(400, Math.Max(observed, glide) * 1.08));
        var step = HeightStep(maximum);
        layout.MaxHeightFeet = Math.Ceiling(maximum / step) * step;
        layout.Valid = layout.Profile.Height >= 60 && layout.MaxAlong > layout.MinAlong;
        return layout;
    }

    private void DrawMetrics(DrawingContext context, LandingDistanceAnalysis analysis)
    {
        var zoneBrush = ZoneBrush(analysis.Zone);
        var touchdown = analysis.TouchdownMeters;
        string touchdownZone; Brush touchdownZoneBrush;
        if (touchdown < 0) { touchdownZone = Format("Distance.TdzShortFormat", -touchdown); touchdownZoneBrush = DangerBrush; }
        else if (touchdown <= analysis.TouchdownZoneEndMeters) { touchdownZone = Format("Distance.TdzLeftFormat", analysis.TouchdownZoneEndMeters - touchdown); touchdownZoneBrush = TextBrush; }
        else { touchdownZone = Format("Distance.TdzBeyondFormat", touchdown - analysis.TouchdownZoneEndMeters); touchdownZoneBrush = DangerBrush; }
        var cells = new (string Label, string Value, Brush Brush, string Detail)[]
        {
            (Text("Distance.Touchdown"), Meters(touchdown), zoneBrush,
                Format(touchdown >= 0 ? "Distance.PastThresholdFormat" : "Distance.BeforeThresholdFormat", Math.Abs(touchdown) / 0.3048)),
            (Text("Distance.AimPoint"), SignedMeters(analysis.AimDeviationMeters), zoneBrush,
                Format("Distance.Aim" + analysis.AimSource + "Format", analysis.AimingPointMeters)),
            (Text("Distance.TargetZone"), Text("Distance.Zone." + analysis.Zone), zoneBrush,
                Format("Distance.TargetRangeFormat", analysis.TargetZoneStartMeters, analysis.TargetZoneEndMeters)),
            (Text("Distance.TouchdownZone"), touchdownZone, touchdownZoneBrush,
                Format("Distance.TdzRangeFormat", analysis.TouchdownZoneEndMeters)),
            (Text("Distance.ThresholdHeight"),
                analysis.ThresholdCrossingHeightFeet is double crossing ? Feet(crossing) : "—", TextBrush,
                analysis.AirDistanceMeters is double air ? Format("Distance.AirDistanceFormat", air) : Text("Distance.AirDistanceUnavailable")),
            (Text("Replay.Centerline"), Lateral(analysis.TouchdownLateralMeters), analysis.OffRunwayEdge ? DangerBrush : TextBrush,
                Format("Distance.RunwayWidthFormat", analysis.RunwayWidthMeters)),
            (Text("Distance.Remaining"), Meters(analysis.RunwayRemainingMeters),
                analysis.ExitSpeedRemainingMeters < 0 ? DangerBrush : TextBrush,
                analysis.ExitSpeedRemainingMeters is double exit
                    ? Format(analysis.ExitSpeedProjected ? "Distance.ExitProjectedFormat" : "Distance.ExitRemainingFormat", exit)
                    : Text("Distance.ExitNotReached")),
        };
        var metrics = _layout.Metrics;
        var cellWidth = metrics.Width / cells.Length;
        for (var index = 0; index < cells.Length; index++)
        {
            var left = metrics.Left + index * cellWidth;
            var textWidth = Math.Max(20, cellWidth - 14);
            if (index > 0) context.DrawLine(GridPen, new Point(left - 8, metrics.Top + 6), new Point(left - 8, metrics.Bottom - 8));
            context.DrawText(Trimmed(Formatted(cells[index].Label, MutedBrush, 10, FontWeights.SemiBold), textWidth), new Point(left, metrics.Top + 4));
            context.DrawText(Trimmed(FormattedMono(cells[index].Value, cells[index].Brush, 16), textWidth), new Point(left, metrics.Top + 18));
            context.DrawText(Trimmed(Formatted(cells[index].Detail, SubtleBrush, 10), textWidth), new Point(left, metrics.Top + 40));
        }
        context.DrawLine(GridPen, new Point(metrics.Left, metrics.Bottom - .5), new Point(metrics.Right, metrics.Bottom - .5));
    }

    private void DrawDistanceGrid(DrawingContext context, LandingDistanceAnalysis analysis)
    {
        var layout = _layout;
        var title = Format("Distance.PlanTitleFormat", analysis.RunwayName.Length == 0 ? "—" : analysis.RunwayName,
            analysis.AirportIdent, analysis.RunwayLengthMeters, analysis.RunwayWidthMeters);
        if (!analysis.ThresholdOffsetKnown) title += " · " + Text("Distance.DisplacedUnknown");
        context.DrawText(Trimmed(Formatted(title, MutedBrush, 10, FontWeights.SemiBold), layout.Plan.Width * .7), new Point(layout.Plan.Left, layout.Plan.Top - 17));
        var exaggerated = Formatted(Text("Distance.LateralExaggerated"), SubtleBrush, 9);
        context.DrawText(exaggerated, new Point(layout.Plan.Right - exaggerated.Width, layout.Plan.Top - 15));
        context.DrawText(Trimmed(Formatted(Text("Distance.ProfileTitle"), MutedBrush, 10, FontWeights.SemiBold), layout.Profile.Width * .7),
            new Point(layout.Profile.Left, layout.Profile.Top - 17));

        var step = DistanceStep(layout.MaxAlong - layout.MinAlong);
        for (var tick = Math.Ceiling(layout.MinAlong / step) * step; tick <= layout.MaxAlong; tick += step)
        {
            var x = layout.X(tick);
            context.DrawLine(GridPen, new Point(x, layout.Plan.Top), new Point(x, layout.Plan.Bottom));
            context.DrawLine(GridPen, new Point(x, layout.Profile.Top), new Point(x, layout.Profile.Bottom));
            var label = Math.Abs(tick) < .5 ? Text("Distance.Threshold") : tick.ToString("N0", CultureInfo.CurrentCulture);
            var text = FormattedMono(label, Math.Abs(tick) < .5 ? TextBrush : MutedBrush, 10);
            context.DrawText(text, new Point(x - text.Width / 2, layout.Profile.Bottom + 6));
        }
        var unit = FormattedMono(Text("Replay.Meters"), MutedBrush, 10);
        context.DrawText(unit, new Point(layout.Profile.Right + 2, layout.Profile.Bottom + 6));
    }

    private void DrawPlan(DrawingContext context, LandingDistanceAnalysis analysis)
    {
        var layout = _layout;
        var plan = layout.Plan;
        var half = analysis.RunwayWidthMeters / 2;
        var start = layout.X(-analysis.ThresholdOffsetMeters);
        var threshold = layout.X(0);
        var end = layout.X(analysis.AvailableLengthMeters);
        var top = layout.PlanY(-half);
        var bottom = layout.PlanY(half);
        var height = bottom - top;

        context.PushClip(new RectangleGeometry(plan));
        context.DrawRectangle(PavementBrush, EdgePen, new Rect(new Point(start, top), new Point(end, bottom)));
        if (threshold - start > 1)
        {
            context.DrawRectangle(DisplacedBrush, null, new Rect(new Point(start, top), new Point(threshold, bottom)));
            for (var index = 1; index <= 3; index++)
            {
                var x = start + (threshold - start) * index / 4;
                var size = Math.Min(height * .25, 6);
                context.DrawLine(ChevronPen, new Point(x - size, layout.PlanY(0) - size), new Point(x, layout.PlanY(0)));
                context.DrawLine(ChevronPen, new Point(x - size, layout.PlanY(0) + size), new Point(x, layout.PlanY(0)));
            }
        }
        context.DrawRectangle(TdzFillBrush, null, new Rect(new Point(threshold, top), new Point(layout.X(analysis.TouchdownZoneEndMeters), bottom)));
        context.DrawRectangle(TargetFillBrush, null, new Rect(new Point(layout.X(analysis.TargetZoneStartMeters), top),
            new Point(Math.Max(layout.X(analysis.TargetZoneStartMeters) + 1, layout.X(analysis.TargetZoneEndMeters)), bottom)));
        context.DrawLine(TdzEdgePen, new Point(layout.X(analysis.TouchdownZoneEndMeters), top), new Point(layout.X(analysis.TouchdownZoneEndMeters), bottom));
        context.DrawLine(TargetEdgePen, new Point(layout.X(analysis.TargetZoneStartMeters), top), new Point(layout.X(analysis.TargetZoneStartMeters), bottom));
        context.DrawLine(TargetEdgePen, new Point(layout.X(analysis.TargetZoneEndMeters), top), new Point(layout.X(analysis.TargetZoneEndMeters), bottom));

        // Schematic threshold piano keys and aiming-point bars.
        const int keys = 8;
        var key = analysis.RunwayWidthMeters / (2 * keys + 1);
        for (var index = 0; index < keys; index++)
        {
            var from = -half + (2 * index + 1) * key;
            context.DrawRectangle(PianoBrush, null, new Rect(new Point(layout.X(6), layout.PlanY(from)), new Point(Math.Max(layout.X(36), layout.X(6) + 1), layout.PlanY(from + key))));
        }
        context.DrawLine(ThresholdPen, new Point(threshold, top), new Point(threshold, bottom));
        var aimFrom = layout.X(analysis.AimingPointMeters);
        var aimTo = Math.Max(aimFrom + 2, layout.X(analysis.AimingPointMeters + 45));
        foreach (var side in new[] { -1, 1 })
            context.DrawRectangle(PaintBrush, null, new Rect(new Point(aimFrom, layout.PlanY(side * half * .14)), new Point(aimTo, layout.PlanY(side * half * .5))));
        context.DrawLine(CenterlinePen, new Point(layout.X(60), layout.PlanY(0)), new Point(layout.X(analysis.AvailableLengthMeters - 60), layout.PlanY(0)));
        DrawTrack(context, analysis, sample => layout.PlanY(sample.LateralMeters));
        context.Pop();

        // Reference labels: zone limits above the plan, paint labels at the runway edge.
        DrawLabel(context, Text("Distance.Threshold"), threshold + 3, plan.Top + 2, TextBrush);
        DrawLabelLeft(context, Text("Distance.Tdz"), layout.X(analysis.TouchdownZoneEndMeters) - 3, plan.Top + 2, BlueBrush);
        DrawLabelLeft(context, Text("Distance.End"), end - 3, plan.Top + 2, MutedBrush);
        var aimLabel = Formatted(Text("Distance.Aim"), AmberBrush, 9, FontWeights.SemiBold);
        if (top - aimLabel.Height - 1 > plan.Top + 14) context.DrawText(aimLabel, new Point(aimFrom, top - aimLabel.Height - 1));
        var targetLabel = Formatted(Text("Distance.Target"), GreenBrush, 9, FontWeights.SemiBold);
        var targetRight = layout.X(analysis.TargetZoneEndMeters) - 3;
        if (targetRight - targetLabel.Width > aimFrom + aimLabel.Width + 6 && top - targetLabel.Height - 1 > plan.Top + 14)
            context.DrawText(targetLabel, new Point(targetRight - targetLabel.Width, top - targetLabel.Height - 1));

        var touchdownX = layout.X(analysis.TouchdownMeters);
        context.DrawLine(TouchdownPen, new Point(touchdownX, plan.Top), new Point(touchdownX, plan.Bottom));
        context.DrawLine(TouchdownPen, new Point(touchdownX, layout.Profile.Top), new Point(touchdownX, layout.BaseY));

        // Remaining runway from the analysed contact to the far end.
        var bracketY = bottom + 10;
        var remaining = Formatted(Format("Distance.RemainingBracketFormat", analysis.RunwayRemainingMeters), MutedBrush, 9);
        if (bracketY + remaining.Height < plan.Bottom && end - touchdownX > remaining.Width + 16)
        {
            context.DrawLine(BracketPen, new Point(touchdownX, bracketY), new Point(end, bracketY));
            context.DrawLine(BracketPen, new Point(touchdownX, bracketY - 3), new Point(touchdownX, bracketY + 3));
            context.DrawLine(BracketPen, new Point(end, bracketY - 3), new Point(end, bracketY + 3));
            var middle = (touchdownX + end) / 2 - remaining.Width / 2;
            context.DrawRectangle(BackgroundBrush, null, new Rect(middle - 4, bracketY - remaining.Height / 2, remaining.Width + 8, remaining.Height));
            context.DrawText(remaining, new Point(middle, bracketY - remaining.Height / 2));
        }

        if (analysis.ExitSpeedMeters is double exit)
        {
            var x = layout.X(exit);
            var y = Math.Max(plan.Top + 4, Math.Min(plan.Bottom - 4, layout.PlanY(analysis.ExitSpeedLateralMeters)));
            if (analysis.ProjectionStartMeters is double projectionStart)
                context.DrawLine(ProjectionPen, new Point(layout.X(projectionStart), y), new Point(x, y));
            var diamond = new StreamGeometry();
            using (var geometry = diamond.Open())
            {
                geometry.BeginFigure(new Point(x, y - 4.5), true, true);
                geometry.LineTo(new Point(x + 4.5, y), true, false);
                geometry.LineTo(new Point(x, y + 4.5), true, false);
                geometry.LineTo(new Point(x - 4.5, y), true, false);
            }
            diamond.Freeze();
            if (analysis.ExitSpeedProjected) context.DrawGeometry(BackgroundBrush, ProjectionMarkerPen, diamond);
            else context.DrawGeometry(BlueBrush, MarkerOutlinePen, diamond);
            var label = Formatted(Text(analysis.ExitSpeedProjected ? "Distance.ExitProjected" : "Distance.Exit"), BlueBrush, 9, FontWeights.SemiBold);
            context.DrawText(label, new Point(x - label.Width / 2, Math.Min(plan.Bottom - label.Height, y + 6)));
        }

        foreach (var contact in analysis.Contacts.OrderBy(contact => contact.Selected))
        {
            var x = layout.X(contact.AlongMeters);
            var y = Math.Max(plan.Top + 5, Math.Min(plan.Bottom - 5, layout.PlanY(contact.LateralMeters)));
            if (contact.Selected) context.DrawEllipse(AccentBrush, MarkerOutlinePen, new Point(x, y), 5, 5);
            else context.DrawEllipse(BackgroundBrush, ContactPen, new Point(x, y), 3.5, 3.5);
            if (contact.Number is int number && analysis.Contacts.Count > 1)
            {
                var text = FormattedMono(number.ToString(CultureInfo.CurrentCulture), contact.Selected ? AccentBrush : MutedBrush, 9);
                context.DrawText(text, new Point(x - text.Width / 2, y - 7 - text.Height));
            }
        }
        var touchdownLabel = FormattedMono(Format("Distance.TouchdownLabelFormat", analysis.TouchdownMeters), AccentBrush, 10);
        var touchdownY = Math.Max(plan.Top + 2, Math.Min(plan.Bottom - touchdownLabel.Height, layout.PlanY(analysis.TouchdownLateralMeters) + 7));
        var labelLeft = touchdownX + 8 + touchdownLabel.Width > plan.Right ? touchdownX - 8 - touchdownLabel.Width : touchdownX + 8;
        context.DrawText(touchdownLabel, new Point(labelLeft, touchdownY));
    }

    private void DrawProfile(DrawingContext context, LandingDistanceAnalysis analysis)
    {
        var layout = _layout;
        var profile = layout.Profile;
        var step = HeightStep(layout.MaxHeightFeet);
        for (var value = 0.0; value <= layout.MaxHeightFeet + .1; value += step)
        {
            var y = layout.ProfileY(value);
            context.DrawLine(GridPen, new Point(profile.Left, y), new Point(profile.Right, y));
            var label = FormattedMono(value.ToString("N0", CultureInfo.CurrentCulture), MutedBrush, 10);
            context.DrawText(label, new Point(profile.Left - 6 - label.Width, y - label.Height / 2));
        }
        var unit = FormattedMono(Text("Distance.Feet"), MutedBrush, 10);
        context.DrawText(unit, new Point(profile.Left - 6 - unit.Width, profile.Top - 17));

        context.PushClip(new RectangleGeometry(profile));
        var tdzLeft = layout.X(0);
        context.DrawRectangle(TdzColumnBrush, null, new Rect(new Point(tdzLeft, profile.Top), new Point(layout.X(analysis.TouchdownZoneEndMeters), layout.BaseY)));
        context.DrawRectangle(TargetColumnBrush, null, new Rect(new Point(layout.X(analysis.TargetZoneStartMeters), profile.Top),
            new Point(Math.Max(layout.X(analysis.TargetZoneStartMeters) + 1, layout.X(analysis.TargetZoneEndMeters)), layout.BaseY)));
        var runwayStart = layout.X(-analysis.ThresholdOffsetMeters);
        context.DrawLine(ApproachBaselinePen, new Point(profile.Left, layout.BaseY), new Point(runwayStart, layout.BaseY));
        context.DrawLine(RunwaySurfacePen, new Point(runwayStart, layout.BaseY), new Point(layout.X(analysis.AvailableLengthMeters), layout.BaseY));
        context.DrawLine(ThresholdPen, new Point(tdzLeft, layout.BaseY - 4), new Point(tdzLeft, layout.BaseY + 4));
        var referenceY = layout.ProfileY(LandingDistanceAnalysis.ReferenceHeightFeet);
        context.DrawLine(ReferencePen, new Point(profile.Left, referenceY), new Point(tdzLeft, referenceY));
        context.DrawLine(GlidePathPen, new Point(layout.X(layout.MinAlong), layout.ProfileY(analysis.GlidePathHeightFeet(layout.MinAlong))),
            new Point(layout.X(analysis.AimingPointMeters), layout.BaseY));
        if (analysis.FlareStartMeters is double flare)
        {
            var x = layout.X(flare);
            context.DrawLine(FlarePen, new Point(x, profile.Top), new Point(x, layout.BaseY));
            context.DrawText(Formatted(LocalizationManager.Text("Chart.Flare"), FlareBrush, 9), new Point(x + 4, profile.Top + 3));
        }
        // On the ground the reference point drops as the nose lowers; keep the roll on the surface.
        DrawTrack(context, analysis, sample => layout.ProfileY(sample.OnGround ? 0 : Math.Max(0, sample.HeightFeet)));
        context.Pop();

        var reference = Formatted(Format("Distance.ReferenceHeightFormat", LandingDistanceAnalysis.ReferenceHeightFeet), SubtleBrush, 9);
        context.DrawText(reference, new Point(profile.Left + 4, referenceY - reference.Height - 1));
        if (analysis.ThresholdCrossingHeightFeet is double crossing)
        {
            var y = layout.ProfileY(crossing);
            if (y >= profile.Top && y <= layout.BaseY)
            {
                context.DrawEllipse(TextBrush, MarkerOutlinePen, new Point(tdzLeft, y), 3.5, 3.5);
                var label = FormattedMono(Format("Distance.TchFormat", crossing), TextBrush, 10);
                var origin = new Point(tdzLeft - 10 - label.Width, Math.Max(profile.Top, y - label.Height / 2));
                context.DrawRoundedRectangle(TooltipBrush, null, new Rect(origin.X - 4, origin.Y - 1, label.Width + 8, label.Height + 2), 3, 3);
                context.DrawText(label, origin);
            }
        }
        var touchdownX = layout.X(analysis.TouchdownMeters);
        if (analysis.ReferenceHeightCrossingMeters is double from && analysis.AirDistanceMeters is double air)
        {
            var x = layout.X(from);
            context.DrawEllipse(SubtleBrush, null, new Point(x, referenceY), 2.5, 2.5);
            var bracketY = layout.BaseY + 11;
            var text = Formatted(Format("Distance.AirBracketFormat", air), MutedBrush, 9);
            if (touchdownX - x > 8)
            {
                context.DrawLine(BracketPen, new Point(x, bracketY), new Point(touchdownX, bracketY));
                context.DrawLine(BracketPen, new Point(x, bracketY - 3), new Point(x, bracketY + 3));
                context.DrawLine(BracketPen, new Point(touchdownX, bracketY - 3), new Point(touchdownX, bracketY + 3));
                var left = touchdownX - x > text.Width + 12 ? (x + touchdownX) / 2 - text.Width / 2 : x - text.Width - 6;
                context.DrawRectangle(BackgroundBrush, null, new Rect(left - 4, bracketY - text.Height / 2, text.Width + 8, text.Height));
                context.DrawText(text, new Point(left, bracketY - text.Height / 2));
            }
        }
        context.DrawEllipse(AccentBrush, MarkerOutlinePen, new Point(touchdownX, layout.BaseY), 5, 5);
    }

    private void DrawTrack(DrawingContext context, LandingDistanceAnalysis analysis, Func<LandingDistanceSample, double> y)
    {
        var flight = new StreamGeometry();
        var ground = new StreamGeometry();
        using (var flightContext = flight.Open())
        using (var groundContext = ground.Open())
        {
            LandingDistanceSample? previous = null;
            foreach (var sample in analysis.Samples)
            {
                if (previous is LandingDistanceSample before &&
                    sample.TimeSeconds - before.TimeSeconds <= LandingReplayTrack.MaximumInterpolationGapSeconds &&
                    (before.AlongMeters >= _layout.MinAlong || sample.AlongMeters >= _layout.MinAlong))
                {
                    // Colour each segment by its destination sample's on-ground state.
                    var target = sample.OnGround ? groundContext : flightContext;
                    target.BeginFigure(new Point(_layout.X(before.AlongMeters), y(before)), false, false);
                    target.LineTo(new Point(_layout.X(sample.AlongMeters), y(sample)), true, false);
                }
                previous = sample;
            }
        }
        flight.Freeze(); ground.Freeze();
        context.DrawGeometry(null, GroundPen, ground);
        context.DrawGeometry(null, FlightPen, flight);
    }

    private void DrawHover(DrawingContext context, LandingDistanceAnalysis analysis, double time)
    {
        var samples = analysis.Samples;
        if (samples.Count == 0) return;
        var sample = samples[RecordedTimeSearch.Nearest(samples.Count, index => samples[index].TimeSeconds, time)];
        if (sample.AlongMeters < _layout.MinAlong || sample.AlongMeters > _layout.MaxAlong) return;
        var plan = _layout.Plan;
        var profile = _layout.Profile;
        var x = _layout.X(sample.AlongMeters);
        context.DrawLine(HoverPen, new Point(x, plan.Top), new Point(x, plan.Bottom));
        context.DrawLine(HoverPen, new Point(x, profile.Top), new Point(x, profile.Bottom));
        var planY = _layout.PlanY(sample.LateralMeters);
        if (planY >= plan.Top && planY <= plan.Bottom) context.DrawEllipse(TextBrush, MarkerOutlinePen, new Point(x, planY), 3.5, 3.5);
        var profileY = _layout.ProfileY(sample.OnGround ? 0 : Math.Max(0, sample.HeightFeet));
        if (profileY >= profile.Top) context.DrawEllipse(TextBrush, MarkerOutlinePen, new Point(x, profileY), 3.5, 3.5);

        var parts = new List<string> { Meters(sample.AlongMeters), Lateral(sample.LateralMeters) };
        if (!sample.OnGround) parts.Add(Feet(Math.Max(0, sample.HeightFeet)));
        if (LandingReplayTrack.Finite(sample.GroundSpeedKnots)) parts.Add($"{sample.GroundSpeedKnots:0} {Text("Replay.Knots")}");
        var text = FormattedMono(string.Join(" · ", parts), TextBrush, 10);
        var width = text.Width + 12;
        var height = text.Height + 4;
        var left = Math.Max(profile.Left, Math.Min(profile.Right - width, x - width / 2));
        var top = profile.Top + 4;
        context.DrawRoundedRectangle(TooltipBrush, null, new Rect(left, top, width, height), 4, 4);
        context.DrawText(text, new Point(left + 6, top + 2));
    }

    private static Brush ZoneBrush(LandingDistanceZone zone) => zone switch
    {
        LandingDistanceZone.Target => GreenBrush,
        LandingDistanceZone.ShortOfTarget or LandingDistanceZone.LongInTouchdownZone => AmberBrush,
        _ => DangerBrush,
    };

    private static double DistanceStep(double span)
    {
        foreach (var step in new[] { 100.0, 200, 250, 500, 1000, 2000 })
            if (span / step <= 12) return step;
        return 5000;
    }

    private static double HeightStep(double maximum) => maximum <= 120 ? 20 : maximum <= 250 ? 50 : 100;

    private void DrawLabel(DrawingContext context, string text, double x, double y, Brush brush) =>
        context.DrawText(Formatted(text, brush, 9, FontWeights.SemiBold), new Point(x, y));

    private void DrawLabelLeft(DrawingContext context, string text, double right, double y, Brush brush)
    {
        var formatted = Formatted(text, brush, 9, FontWeights.SemiBold);
        context.DrawText(formatted, new Point(right - formatted.Width, y));
    }

    private static string Text(string key) => LocalizationManager.Text(key);

    private static string Format(string key, params object[] arguments) => LocalizationManager.Format(key, arguments);

    private static string Meters(double value) =>
        value.ToString("N0", CultureInfo.CurrentCulture) + " " + Text("Replay.Meters");

    private static string SignedMeters(double value) =>
        value.ToString("+#,##0;-#,##0;0", CultureInfo.CurrentCulture) + " " + Text("Replay.Meters");

    private static string Feet(double value) =>
        value.ToString("N0", CultureInfo.CurrentCulture) + " " + Text("Distance.Feet");

    private static string Lateral(double value) => Math.Abs(value) < .05
        ? Text("Replay.OnCenterline")
        : Math.Abs(value).ToString("0.0", CultureInfo.CurrentCulture) + " " + Text("Replay.Meters") + " " +
          Text(value > 0 ? "Replay.RightShort" : "Replay.LeftShort");

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

    private static Pen DashedPen(Brush brush, double thickness, double dash, double gap)
    {
        var pen = new Pen(brush, thickness) { DashStyle = new DashStyle(new[] { dash, gap }, 0) };
        pen.Freeze();
        return pen;
    }
}
