using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Presentation;

namespace LandingStats.App;

// History-list scores come from the score cache. Landings it does not cover are
// scored off the dispatcher, newest first, and the cache is saved back. The pass
// holds the repository gate for one landing at a time and stays out of the
// detail queue, so it never waits behind or delays a selection's read.
public partial class MainWindow
{
    private const int HistoryScoreBatchSize = 25;
    private static readonly TimeSpan HistoryScoreBatchInterval = TimeSpan.FromMilliseconds(500);
    private Dictionary<string, LandingScoreCache.Entry>? _historyScoreEntries;
    private IReadOnlyList<LandingRecord>? _scoredLandings;
    private bool _historyScoringRunning;

    private HistoryScoreStore HistoryScores => (HistoryScoreStore)Resources["HistoryScores"];

    /// <summary>Brings the list scores up to date after the history changes; a no-op while a pass runs or nothing changed.</summary>
    private async void RefreshHistoryScores()
    {
        if (_historyScoringRunning || _isClosed || _historyStopped || _historyLoading || _historyLoadError != null ||
            ReferenceEquals(_scoredLandings, _landings)) return;
        _historyScoringRunning = true;
        try
        {
            while (!_isClosed && !_historyStopped && !ReferenceEquals(_scoredLandings, _landings))
            {
                var landings = _landings;
                var summaries = landings.Where(record => !_locallyRemovedHistoryIds.Contains(record.Id)).ToArray();
                var known = _historyScoreEntries == null ? null
                    : new Dictionary<string, LandingScoreCache.Entry>(_historyScoreEntries, StringComparer.Ordinal);
                var path = LandingScoreCache.PathFor(_repository.RootPath);
                var token = _lifetimeCancellation.Token;
                var scoring = Task.Run(() => ScoreHistory(path, summaries, known, PublishHistoryScores, token), token);
                await TrackHistoryOperation(scoring);
                _historyScoreEntries = scoring.Result;
                _scoredLandings = landings;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Unscored rows keep an empty badge; the next history change retries.
            Debug.WriteLine(exception);
        }
        finally
        {
            _historyScoringRunning = false;
        }
    }

    private void PublishHistoryScores(IReadOnlyList<KeyValuePair<string, double?>> totals) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_isClosed) HistoryScores.Publish(totals); }));

    /// <summary>
    /// Off the dispatcher: publishes cached totals whose stamp still matches, scores the other
    /// first contacts newest first and saves the cache, also when shutdown cancels the pass.
    /// </summary>
    private Dictionary<string, LandingScoreCache.Entry> ScoreHistory(string path, IReadOnlyList<LandingRecord> summaries,
        Dictionary<string, LandingScoreCache.Entry>? known, Action<IReadOnlyList<KeyValuePair<string, double?>>> publish,
        CancellationToken token)
    {
        var entries = known ?? LandingScoreCache.Load(path);
        var firstContacts = summaries.Where(record => record.ContactNumber == 1).ToArray();
        var stamps = firstContacts.ToDictionary(record => record.Id,
            record => LandingScoreCache.Stamp(record, LandingReplayContacts.ForEpisode(record, summaries)), StringComparer.Ordinal);
        var deleted = entries.Keys.Where(id => !stamps.ContainsKey(id)).ToArray();
        foreach (var id in deleted) entries.Remove(id);
        var changed = deleted.Length > 0;
        publish(entries.Where(entry => entry.Value.Stamp == stamps[entry.Key])
            .Select(entry => new KeyValuePair<string, double?>(entry.Key, entry.Value.Total)).ToArray());

        var batch = new List<KeyValuePair<string, double?>>();
        var sincePublish = Stopwatch.StartNew();
        try
        {
            foreach (var summary in firstContacts.Where(record => !entries.TryGetValue(record.Id, out var entry) || entry.Stamp != stamps[record.Id]))
            {
                token.ThrowIfCancellationRequested();
                var total = ScoreStoredLanding(summary, summaries);
                entries[summary.Id] = new LandingScoreCache.Entry(summary.Id, stamps[summary.Id], total);
                changed = true;
                batch.Add(new KeyValuePair<string, double?>(summary.Id, total));
                if (batch.Count < HistoryScoreBatchSize && sincePublish.Elapsed < HistoryScoreBatchInterval) continue;
                publish(batch.ToArray());
                batch.Clear();
                sincePublish.Restart();
            }
        }
        finally
        {
            if (batch.Count > 0) publish(batch.ToArray());
            // Keep the progress of a cancelled pass: the next start resumes from it.
            if (changed)
            {
                try { LandingScoreCache.Save(path, entries.Values); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                { Debug.WriteLine(exception); }
            }
        }
        return entries;
    }

    /// <summary>Reads a first contact and its later contacts and scores it as the Score tab does.</summary>
    private double? ScoreStoredLanding(LandingRecord summary, IReadOnlyList<LandingRecord> summaries)
    {
        var episode = LandingReplayContacts.ForEpisode(summary, summaries);
        var details = new List<LandingRecord>();
        lock (_landingRepositoryGate)
            foreach (var contact in episode)
                if (_repository.LoadDetail(contact) is { IsSummaryOnly: false } detail) details.Add(detail);
        // Scenery resolved after a file was written lives in its summary.
        foreach (var detail in details)
            if (episode.First(contact => contact.Id == detail.Id).RunwayGeometry is { } runway) detail.RunwayGeometry = runway;
        var first = details.FirstOrDefault(detail => detail.Id == summary.Id);
        return first == null ? null : LandingScore.ForFirstContact(first, details)?.Total;
    }
}
