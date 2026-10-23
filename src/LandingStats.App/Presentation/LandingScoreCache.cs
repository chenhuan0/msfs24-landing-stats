using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;

namespace LandingStats.App.Presentation;

/// <summary>
/// History-list totals persisted in the landings folder. They derive from the landing
/// files and the scoring rules, so they stay outside the record format: an entry is reused
/// while its rules version and input stamp match, and is otherwise recomputed.
/// </summary>
internal static class LandingScoreCache
{
    internal const string FileName = "landing-scores.json";
    private const long MaximumBytes = 16 * 1024 * 1024;
    private static readonly DataContractJsonSerializer RunwaySerializer = new DataContractJsonSerializer(typeof(RunwayFacility));

    [DataContract]
    internal sealed class Entry
    {
        public Entry(string id, string stamp, double? total) { Id = id; Stamp = stamp; Total = total; }

        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Stamp { get; set; }
        /// <summary>Null when the first contact has nothing to score.</summary>
        [DataMember(Order = 3)] public double? Total { get; set; }
    }

    [DataContract]
    private sealed class Payload
    {
        [DataMember(Order = 1)] public int RulesVersion { get; set; }
        [DataMember(Order = 2)] public List<Entry>? Entries { get; set; }
    }

    /// <summary>
    /// Beside the landing index. The repository only enumerates record files there and cloud
    /// backup uploads records by identity, so neither picks the cache up.
    /// </summary>
    public static string PathFor(string landingsRoot) => Path.Combine(landingsRoot, FileName);

    /// <summary>Entries of the current rules version; empty when the file is missing, unreadable or older.</summary>
    public static Dictionary<string, Entry> Load(string path)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(path)) return entries;
        try
        {
            var payload = DurableFile.ReadJson<Payload>(path, MaximumBytes);
            if (payload.RulesVersion != LandingScore.RulesVersion) return entries;
            foreach (var entry in payload.Entries ?? new List<Entry>())
                if (!string.IsNullOrEmpty(entry?.Id) && !string.IsNullOrEmpty(entry!.Stamp)) entries[entry.Id] = entry;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                          exception is SerializationException || exception is InvalidDataException)
        {
            // A damaged cache is rebuilt from the landing files.
            entries.Clear();
        }
        return entries;
    }

    public static void Save(string path, IEnumerable<Entry> entries) =>
        DurableFile.WriteJson(path, new Payload
        {
            RulesVersion = LandingScore.RulesVersion,
            Entries = entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList(),
        });

    /// <summary>
    /// Identifies the scoring inputs that can change after a landing is stored: scenery
    /// metadata resolved later and the contacts of its episode.
    /// </summary>
    public static string Stamp(LandingRecord summary, IEnumerable<LandingRecord> episode)
    {
        var text = new StringBuilder()
            .Append(summary.ContactCount).Append('|').Append(summary.Airport).Append('|').Append(summary.Runway).Append('|')
            .Append(string.Join(",", episode.Select(contact => contact.Id))).Append('|');
        if (summary.RunwayGeometry != null)
        {
            using var stream = new MemoryStream();
            RunwaySerializer.WriteObject(stream, summary.RunwayGeometry);
            text.Append(Encoding.UTF8.GetString(stream.ToArray()));
        }
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
