using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class TouchdownCardRegressionTests
{
    private const double Epoch = 100;

    public static void Builder()
    {
        var record = LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: 2, reciprocal: false);
        var runways = new[] { record.RunwayGeometry! };
        var samples = Samples(record);
        var contact = samples.FindIndex(sample => sample.OnGround);
        Check(TouchdownNoticeBuilder.TryCreate(1, DateTime.UtcNow, samples.Take(contact + 4).ToList(), contact, runways) == null,
            "the notice waits while the initial load response develops");
        var (notice, delay) = FirstNotice(samples, contact, runways);
        Check(delay >= .7 && delay <= 1.0, $"a settled impact completes the notice about a second after contact ({delay:0.00} s)");
        Near(-150, notice.VerticalSpeedFpm, 1e-9, "vertical speed of the last airborne frame");
        Near(1.32, notice.PeakG!.Value, 1e-9, "peak of the initial load response");
        Check(notice.Runway != null && !notice.SecondaryEnd && notice.RunwayName == "09" && !ReferenceEquals(notice.Runway, runways[0]),
            "the contact selects a detached copy of the containing runway end");
        // The card must agree with the Distance tab of the saved landing.
        var distance = LandingDistanceAnalysis.Create(record, LandingDistanceRegressionTests.Track(record), new[] { 0.0 })!;
        Near(distance.TouchdownMeters, notice.TouchdownMeters, .05, "distance past the threshold matches the Distance tab");
        Near(distance.TouchdownLateralMeters, notice.LateralMeters, .05, "centerline offset matches the Distance tab");
        Near(2, notice.LateralMeters, .1, "positive offset is right of the landing direction");

        var reciprocal = LandingDistanceRegressionTests.Record("27", touchdown: 450, lateral: 2, reciprocal: true);
        var reverse = Samples(reciprocal);
        var (opposite, _) = FirstNotice(reverse, reverse.FindIndex(sample => sample.OnGround), new[] { reciprocal.RunwayGeometry! });
        Check(opposite.SecondaryEnd && opposite.RunwayName == "27", "landing on the reciprocal selects the secondary end");
        Near(450, opposite.TouchdownMeters, .5, "secondary distance is measured from its own threshold");
        Near(2, opposite.LateralMeters, .1, "secondary offset is right of its landing direction");

        var (unknown, _) = FirstNotice(samples, contact, Array.Empty<RunwayFacility>());
        Check(unknown.Runway == null && double.IsNaN(unknown.TouchdownMeters) && double.IsNaN(unknown.LateralMeters) &&
              unknown.PeakG.HasValue && !double.IsNaN(unknown.VerticalSpeedFpm), "without a runway only the runway readings are unavailable");

        // A load that never settles is reported with its peak at the deadline.
        var restless = Samples(record);
        for (var index = contact + 1; index < restless.Count; index++) restless[index].GForce = index % 2 == 0 ? 1.0 : 1.3;
        var (late, wait) = FirstNotice(restless, contact, runways);
        Near(TouchdownNoticeBuilder.MaximumWaitSeconds, wait, .05, "an unsettled impact waits no longer than the deadline");
        Near(1.3, late.PeakG!.Value, 1e-9, "the deadline keeps the peak so far");
    }

    public static void Coordinator()
    {
        var record = LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: 2, reciprocal: false);
        var port = new Port(record.RunwayGeometry!);
        var samples = Samples(record);
        // A bounce from 3.0 to 3.5 s ends a later contact, which never gets its own notice.
        foreach (var sample in samples.Where(sample => sample.SimulationTimeSeconds > Epoch + 3 && sample.SimulationTimeSeconds < Epoch + 3.5))
        { sample.OnGround = false; sample.AboveGroundLevelFeet = 12; }
        var utc = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);

        var notices = new List<TouchdownNoticeEventArgs>();
        var coordinator = Coordinator(port, utc, notices);
        foreach (var sample in samples) coordinator.ProcessSample(sample.CopyForAnalysis());
        Check(notices.Count == 1 && notices[0].EpisodeId == 1 && notices[0].ObservedUtc == utc && notices[0].RunwayName == "09",
            "one notice per landing, for its first contact");

        // Recovery inside the wait keeps the pending contact, and older checkpoints restore without one.
        notices.Clear();
        coordinator = Coordinator(port, utc, notices);
        var contact = samples.FindIndex(sample => sample.OnGround);
        foreach (var sample in samples.Take(contact + 3)) coordinator.ProcessSample(sample.CopyForAnalysis());
        var checkpoint = coordinator.CaptureCheckpoint();
        coordinator = LandingEpisodeCoordinator.RestoreCheckpoint(checkpoint, port, () => 0, Array.Empty<string>, () => utc);
        Check(coordinator.CaptureCheckpoint().SequenceEqual(checkpoint), "the pending notice survives a checkpoint round trip");
        coordinator.TouchdownObserved += (_, notice) => notices.Add(notice);
        foreach (var sample in samples.Skip(contact + 3)) coordinator.ProcessSample(sample.CopyForAnalysis());
        Check(notices.Count == 1, "a restored coordinator completes the pending notice once");

        var legacy = checkpoint.Take(checkpoint.Length - sizeof(int)).ToArray();
        BitConverter.GetBytes(0x3443454c).CopyTo(legacy, 0);
        LandingEpisodeCoordinator.RestoreCheckpoint(legacy, port, () => 0, Array.Empty<string>, () => utc);
        var invalid = (byte[])checkpoint.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(invalid, invalid.Length - sizeof(int));
        try { LandingEpisodeCoordinator.RestoreCheckpoint(invalid, port, () => 0, Array.Empty<string>, () => utc); throw new InvalidOperationException("An out-of-range pending contact was accepted."); }
        catch (InvalidDataException) { }
    }

    public static void Card()
    {
        LocalizationManager.Apply("en");
        var card = TouchdownCard.Create(MainWindow.PreviewTouchdownNotice());
        Check(card.Location == "ZSPD · RWY 17L", "the card names the airport and runway end");
        Check(card.Metrics.Select(metric => metric.Value + " " + metric.Unit).SequenceEqual(new[] { "−132 fpm", "1.24 G", "1.8 m R", "412 m" }),
            "the card shows signed vertical speed, peak G, sided centerline offset and distance");
        Check(card.Metrics.All(metric => metric.Score is double score && score >= 80), "a typical landing grades good on every reading");

        var firm = new TouchdownNoticeEventArgs(1, DateTime.UtcNow, -420, 1.9, 140000, 138, null, false, double.NaN, double.NaN);
        var graded = TouchdownCard.Create(firm);
        Near(LandingScore.Evaluate(LandingScore.VerticalSpeedCurve(LandingScoreCategory.Airliner), 420), graded.Metrics[0].Score!.Value, 1e-9,
            "vertical speed is graded on the Score tab's curve for the category");
        Near(LandingScore.Evaluate(LandingScore.GForceCurve, 1.9), graded.Metrics[1].Score!.Value, 1e-9, "peak G is graded on the Score tab's curve");
        Check(graded.Location.Length == 0 && graded.Metrics.Skip(2).All(metric => metric.Value == "—" && metric.Score == null),
            "runway readings are unavailable without a runway");

        var view = new TouchdownCardView();
        view.SetCard(card);
        var bitmap = RenderAtDesiredSize(view);
        var preview = Environment.GetEnvironmentVariable("MSFSLS_DISTANCE_PREVIEW");
        if (!string.IsNullOrWhiteSpace(preview)) LandingDistanceRegressionTests.Save(bitmap, Path.Combine(preview, "touchdown-card.png"));
        Check(bitmap.PixelWidth < 600 && bitmap.PixelHeight < 160 && view.Location == card.Location, "the card stays compact and shows the location");
        try
        {
            LocalizationManager.Apply("ru");
            var russian = new TouchdownCardView();
            russian.SetCard(TouchdownCard.Create(MainWindow.PreviewTouchdownNotice()));
            bitmap = RenderAtDesiredSize(russian);
            if (!string.IsNullOrWhiteSpace(preview)) LandingDistanceRegressionTests.Save(bitmap, Path.Combine(preview, "touchdown-card-ru.png"));
            Check(bitmap.PixelWidth < 600 && russian.Location == "ZSPD · ВПП 17L", "the Russian card stays compact");
        }
        finally { LocalizationManager.Apply("en"); }

        var now = DateTime.UtcNow;
        Check(MainWindow.IsFreshTouchdownNotice(now.AddSeconds(-3), now) && !MainWindow.IsFreshTouchdownNotice(now.AddMinutes(-2), now),
            "a recovered notice from an earlier session is never shown");
    }

    /// <summary>Renders the card at the size its window takes on, as SizeToContent does.</summary>
    private static System.Windows.Media.Imaging.RenderTargetBitmap RenderAtDesiredSize(TouchdownCardView view)
    {
        view.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = view.DesiredSize;
        return LandingDistanceRegressionTests.Render(view, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    public static void Settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "touchdown-card-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var repository = new ApplicationSettingsRepository(path);
            Check(repository.Load().TouchdownCardEnabled && repository.Load().TouchdownCardPosition == "TopRight", "fresh installs show the card at the top right");
            File.WriteAllText(path, "{\"schemaVersion\":5,\"language\":\"en\",\"integrationApiEnabled\":false}");
            Check(repository.Load().TouchdownCardEnabled && repository.Load().TouchdownCardPosition == "TopRight", "upgrades show the card at the top right");
            foreach (var unknown in new[] { "Sideways", "2", "TopLeft,TopCenter", "topleft", "" })
            {
                File.WriteAllText(path, "{\"schemaVersion\":5,\"language\":\"en\",\"touchdownCardPosition\":\"" + unknown + "\"}");
                Check(repository.Load().TouchdownCardPosition == "TopRight", "an unknown position falls back to the top right: " + unknown);
            }
            var owner = new ApplicationSettingsOwner(repository, new SimulatorAutoStartManager());
            var baseline = owner.Read().Settings;
            var disabled = baseline.Clone(); disabled.TouchdownCardEnabled = false;
            owner.SaveAsync(baseline, disabled).GetAwaiter().GetResult();
            Check(!repository.Load().TouchdownCardEnabled && File.ReadAllText(path).Contains("\"touchdownCardEnabled\":false"),
                "turning the card off persists");
            var moved = owner.Snapshot.Settings; var bottom = moved.Clone(); bottom.TouchdownCardPosition = "BottomCenter";
            owner.SaveAsync(moved, bottom).GetAwaiter().GetResult();
            Check(repository.Load().TouchdownCardPosition == "BottomCenter", "the chosen position persists");
            var stale = baseline.Clone(); stale.Language = "ru";
            owner.SaveAsync(baseline, stale).GetAwaiter().GetResult();
            Check(!repository.Load().TouchdownCardEnabled && repository.Load().TouchdownCardPosition == "BottomCenter",
                "an unrelated stale save keeps the card's preferences");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void Placement()
    {
        // A 720 × 200 window whose card has 28/18/28/34 px of shadow, on a 2560 × 1440 monitor right of the primary one.
        (int X, int Y) At(TouchdownCardPosition position) => TouchdownCardWindow.Place(position, 1920, 0, 4480, 1440, 720, 200, 28, 18, 28, 34);
        var corner = 43;
        Check(At(TouchdownCardPosition.TopRight) == (4480 - corner - 720 + 28, corner - 18), "top right aligns the visible card with the corner gap");
        Check(At(TouchdownCardPosition.TopLeft) == (1920 + corner - 28, corner - 18), "top left aligns the visible card with the corner gap");
        Check(At(TouchdownCardPosition.TopCenter) == (1920 + (2560 - 720) / 2, 115 - 18), "top centre sits 8% below the top edge");
        Check(At(TouchdownCardPosition.BottomCenter) == (1920 + (2560 - 720) / 2, 1440 - 144 - 200 + 34), "bottom centre sits 10% above the bottom edge");
        Check(TouchdownCardPositions.Default == TouchdownCardPosition.TopRight, "the card defaults to the top right");
    }

    private static (TouchdownNoticeEventArgs Notice, double Delay) FirstNotice(List<TelemetrySample> samples, int contact, IReadOnlyList<RunwayFacility> runways)
    {
        for (var end = contact; end < samples.Count; end++)
        {
            var notice = TouchdownNoticeBuilder.TryCreate(1, DateTime.UtcNow, samples.Take(end + 1).ToList(), contact, runways);
            if (notice != null) return (notice, samples[end].SimulationTimeSeconds - samples[contact].SimulationTimeSeconds);
        }
        throw new InvalidOperationException("No touchdown notice was produced.");
    }

    /// <summary>Live frames along a synthetic landing: −2.5 ft/s descent, then a 1.32 G load that settles by 0.3 s.</summary>
    private static List<TelemetrySample> Samples(LandingRecord record) => record.Series.Select(point => new TelemetrySample
    {
        SimulationTimeSeconds = Epoch + point.TimeSeconds, HostElapsedSeconds = Epoch + point.TimeSeconds, SimulationDeltaSeconds = .1,
        MotionSimulation = true, OnGround = point.OnGround, LatitudeDegrees = point.LatitudeDegrees!.Value, LongitudeDegrees = point.LongitudeDegrees!.Value,
        AboveGroundLevelFeet = point.AglFeet, PlaneAltitudeFeet = point.PlaneAltitudeFeet!.Value, GroundAltitudeFeet = point.GroundAltitudeFeet!.Value,
        HeadingTrueDegrees = point.HeadingTrueDegrees!.Value, GroundSpeedKnots = point.GroundSpeedKnots,
        VelocityWorldYFps = point.TimeSeconds < 0 ? -2.5 : 0, VerticalSpeedFps = point.TimeSeconds < 0 ? -2.5 : 0,
        GForce = Math.Abs(point.TimeSeconds) < 1e-9 ? 1.05 : Math.Abs(point.TimeSeconds - .1) < 1e-9 ? 1.32 : Math.Abs(point.TimeSeconds - .2) < 1e-9 ? 1.1 : 1,
        TotalWeightPounds = 140000, IndicatedAirspeedKnots = 140,
    }).ToList();

    private static LandingEpisodeCoordinator Coordinator(Port port, DateTime utc, List<TouchdownNoticeEventArgs> notices)
    {
        var coordinator = new LandingEpisodeCoordinator(port, () => 0, Array.Empty<string>, () => utc, Guid.NewGuid());
        coordinator.TouchdownObserved += (_, notice) => notices.Add(notice);
        return coordinator;
    }

    private sealed class Port : IEpisodeFacilityPort
    {
        public Port(RunwayFacility runway) { ApproachRunways = new[] { runway }; }
        public long EpisodeId { get; private set; }
        public bool CanQuery => false;
        public double DecisionTime => 0;
        public Task<IReadOnlyList<RunwayFacility>>? RunwayTask => null;
        public Task<EpisodeFacilitySnapshot>? SnapshotTask => null;
        public IReadOnlyList<AirportFacility> CachedAirports => Array.Empty<AirportFacility>();
        public IReadOnlyList<RunwayFacility> ApproachRunways { get; }
        public void SetDecisionContext(TelemetryDecisionJournal? journal, double time) { }
        public void BeginEpisode(long episodeId) => EpisodeId = episodeId;
        public void RequestPreliminary(TelemetrySample trigger, IReadOnlyList<TelemetrySample> samples) { }
        public void RequestFinal(IReadOnlyList<TelemetrySample> samples) { }
        public void CompleteWithoutRefresh(string reason) { }
        public void DiscardEpisode(long episodeId) { if (episodeId == EpisodeId) EpisodeId = 0; }
        public EpisodeFacilitySnapshot CurrentSnapshot() => new EpisodeFacilitySnapshot(EpisodeId, Array.Empty<RunwayFacility>(),
            Array.Empty<AirportFacility>(), Array.Empty<TelemetrySample>(), Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    { if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}"); }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
