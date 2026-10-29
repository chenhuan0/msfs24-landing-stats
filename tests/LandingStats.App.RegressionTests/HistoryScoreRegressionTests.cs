using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class HistoryScoreRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Cache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-score-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = LandingScoreCache.PathFor(directory);
            Check(Path.GetDirectoryName(path) == directory && Path.GetFileName(path) == LandingScoreCache.FileName, "the cache sits in the landings folder");
            Check(LandingScoreCache.Load(path).Count == 0, "a missing cache is empty");
            LandingScoreCache.Save(path, new[] { new LandingScoreCache.Entry("b", "s2", null), new LandingScoreCache.Entry("a", "s1", 87.5) });
            var loaded = LandingScoreCache.Load(path);
            Check(loaded.Count == 2 && loaded["a"].Total == 87.5 && loaded["a"].Stamp == "s1" && loaded["b"].Total == null, "entries round-trip");
            var current = "\"RulesVersion\":" + LandingScore.RulesVersion;
            Check(File.ReadAllText(path).Contains(current), "the cache records its rules version");
            File.WriteAllText(path, File.ReadAllText(path).Replace(current, "\"RulesVersion\":" + (LandingScore.RulesVersion - 1)));
            Check(LandingScoreCache.Load(path).Count == 0, "a cache from other rules is discarded");
            File.WriteAllText(path, "{ not json");
            Check(LandingScoreCache.Load(path).Count == 0, "a damaged cache is rebuilt");

            var summary = LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false);
            var stamp = LandingScoreCache.Stamp(summary, new[] { summary });
            Check(stamp == LandingScoreCache.Stamp(summary, new[] { summary }), "stamps are stable");
            summary.RunwayGeometry!.WidthMeters = 60;
            Check(stamp != LandingScoreCache.Stamp(summary, new[] { summary }), "resolved runway geometry changes the stamp");
            stamp = LandingScoreCache.Stamp(summary, new[] { summary });
            summary.ContactCount = 2;
            var later = new LandingRecord { ContactNumber = 2, ContactCount = 2 };
            Check(stamp != LandingScoreCache.Stamp(summary, new[] { summary, later }), "a bounce changes the stamp");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    public static void Badge()
    {
        var ideal = LandingScoreRegressionTests.Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        var bounced = LandingScoreRegressionTests.Bounced(2);
        var second = LandingScoreRegressionTests.SecondContact(bounced, heightFeet: 3, gapSeconds: 1, clocked: true);
        second.TimestampUtc = bounced.TimestampUtc;
        foreach (var (record, episode) in new[] { (ideal, new[] { ideal }), (bounced, new[] { bounced, second }) })
        {
            var view = new LandingScoreView();
            view.SetRecord(record, episode);
            Near(view.Score!.Total!.Value, LandingScore.ForFirstContact(record, episode)!.Total!.Value, 1e-9, "the list scores a landing as the Score tab does");
        }
        Check(LandingScore.ForFirstContact(second, new[] { bounced, second }) == null, "later contacts are not scored");

        var grades = new[] { 95.0, 80, 65, 50, 30, 10 }.Select(LandingScoreView.ScoreBrush).ToArray();
        Check(grades.Distinct().Count() == 6, "each grade has its own color");

        var store = new HistoryScoreStore();
        var converter = new HistoryScoreConverter();
        object Badge(LandingRecord record, string part) =>
            converter.Convert(new object[] { record, store, store.Version }, typeof(object), part, CultureInfo.InvariantCulture)!;
        var pending = new LandingRecord();
        Check((string)Badge(pending, "Text") == string.Empty && Badge(pending, "Foreground") == LandingScoreView.NeutralBrush,
            "an unscored landing shows an empty badge");
        var changes = 0;
        store.PropertyChanged += (_, change) => changes += change.PropertyName == nameof(HistoryScoreStore.Version) ? 1 : 0;
        var unscorable = new LandingRecord();
        store.Publish(new[] { new KeyValuePair<string, double?>(pending.Id, 92.4), new KeyValuePair<string, double?>(unscorable.Id, null) });
        Check(changes == 1, "a publication refreshes the bindings once");
        Check((string)Badge(pending, "Text") == "92" && Badge(pending, "Foreground") == LandingScoreView.ScoreBrush(92.4) &&
              Badge(pending, "Background") == LandingScoreView.ScoreTintBrush(92.4), "the badge shows the total in its grade color");
        Check(((string)Badge(pending, "ToolTip")).Contains(LocalizationManager.Text("Score.Grade.Excellent")), "the tooltip names the grade");
        Check((string)Badge(unscorable, "Text") == "—", "a landing without measurements shows a dash");
        var bounce = new LandingRecord { ContactNumber = 2, ContactCount = 3 };
        Check((string)Badge(bounce, "Text") == "#2" && Badge(bounce, "Foreground") == LandingScoreView.NeutralBrush, "a later contact shows its number");
    }

    public static void HistoryList()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-history-scores-" + Guid.NewGuid().ToString("N"));
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var previousContext = SynchronizationContext.Current;
        var previousShutdownMode = Application.Current.ShutdownMode;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        try
        {
            var repository = new LandingRepository(directory);
            var firm = LandingScoreRegressionTests.Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
            firm.InertialFpm = 277;
            firm.TimestampUtc = DateTime.UtcNow.AddMinutes(-2);
            var bounced = LandingScoreRegressionTests.Bounced(2);
            bounced.TimestampUtc = DateTime.UtcNow.AddMinutes(-1);
            var second = LandingScoreRegressionTests.SecondContact(bounced, heightFeet: 3, gapSeconds: 1, clocked: true);
            second.TimestampUtc = bounced.TimestampUtc;
            foreach (var record in new[] { firm, bounced, second })
            {
                // No raw controllers are declared, so the stored series carries none.
                foreach (var point in record.Series)
                {
                    point.RawControllerYAxisPercent = Array.Empty<double>();
                    point.RawControllerYAxisValid = Array.Empty<bool>();
                    point.RawControllerYAxisAgeSeconds = Array.Empty<double>();
                }
                repository.Save(record);
            }
            var details = repository.LoadAll().Select(summary => repository.LoadDetail(summary)!).ToArray();
            var expected = new[] { firm, bounced }.ToDictionary(record => record.Id,
                record => LandingScore.ForFirstContact(details.Single(detail => detail.Id == record.Id), details)!.Total!.Value);

            window = Open(repository, directory);
            var store = (HistoryScoreStore)window.Resources["HistoryScores"];
            PumpUntil(() => expected.Keys.All(id => store.TryGetTotal(id, out _)));
            foreach (var pair in expected)
            {
                store.TryGetTotal(pair.Key, out var total);
                Near(pair.Value, total!.Value, 1e-9, "the background pass publishes each landing's total");
            }
            Check(!store.TryGetTotal(second.Id, out _), "the bounce contact is not scored");
            DrainHistory(window);
            var path = LandingScoreCache.PathFor(directory);
            var saved = LandingScoreCache.Load(path);
            Check(saved.Count == 2 && expected.All(pair => saved[pair.Key].Total == pair.Value), "the totals are saved to the cache");
            Close(window);
            window = null;

            // A planted total proves the next start reads the cache instead of rescoring.
            saved[firm.Id].Total = 42;
            LandingScoreCache.Save(path, saved.Values);
            window = Open(repository, directory);
            store = (HistoryScoreStore)window.Resources["HistoryScores"];
            PumpUntil(() => store.TryGetTotal(firm.Id, out var total) && total == 42);
            DrainHistory(window);
            Check(LandingScoreCache.Load(path)[firm.Id].Total == 42, "a valid cache entry is kept");
        }
        finally
        {
            try
            {
                if (window != null) Close(window);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
                LocalizationManager.Apply("en");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            finally
            {
                Application.Current.ShutdownMode = previousShutdownMode;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    /// <summary>An unshown window on <paramref name="repository"/> with its history loaded.</summary>
    private static MainWindow Open(LandingRepository repository, string directory)
    {
        // Never show it: Loaded starts the simulator, update and cloud owners.
        var window = new MainWindow();
        Set(window, "_repository", repository);
        Set(window, "_airportFacilityRepository", new AirportFacilityRepository(Path.Combine(directory, "airports.gz")));
        var loading = (Task)typeof(MainWindow).GetMethod("LoadHistoryAsync", Private)!.Invoke(window, new object?[] { null })!;
        PumpUntil(() => loading.IsCompleted);
        loading.GetAwaiter().GetResult();
        return window;
    }

    private static void Close(MainWindow window)
    {
        typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, null);
        DrainHistory(window);
        // Avoid invoking unrelated application shutdown in this fixture.
        window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
        window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
        window.Close();
    }

    private static void DrainHistory(MainWindow window)
    {
        var pending = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
        PumpUntil(() => pending.IsCompleted);
        // A completed disk task can have a dispatcher continuation still queued.
        var pulse = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        PumpUntil(() => pulse.Task.IsCompleted);
    }

    private static void Set(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(condition(), "dispatcher operation timed out");
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message}: expected {expected:0.###} ± {tolerance}, actual {actual:0.###}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
