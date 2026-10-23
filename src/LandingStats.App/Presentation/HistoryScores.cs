using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.Presentation;

/// <summary>History-list totals of first contacts, published on the dispatcher.</summary>
public sealed class HistoryScoreStore : INotifyPropertyChanged
{
    private readonly Dictionary<string, double?> _totals = new Dictionary<string, double?>(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Changes on every publication so list bindings read their totals again.</summary>
    public int Version { get; private set; }

    /// <summary>False until the first contact has been scored; the total is null when it has nothing to score.</summary>
    internal bool TryGetTotal(string id, out double? total) => _totals.TryGetValue(id, out total);

    internal void Publish(IEnumerable<KeyValuePair<string, double?>> totals)
    {
        foreach (var total in totals) _totals[total.Key] = total.Value;
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }
}

/// <summary>
/// The history-list score badge. Values are the row's <see cref="LandingRecord"/> and the
/// <see cref="HistoryScoreStore"/> (and its version, to refresh); the parameter selects
/// Text, Foreground, Background or ToolTip. Later contacts are bounces and show their number.
/// </summary>
public sealed class HistoryScoreConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not LandingRecord record || values[1] is not HistoryScoreStore store)
            return DependencyProperty.UnsetValue;
        var bounce = record.ContactNumber > 1;
        double? total = null;
        var scored = !bounce && store.TryGetTotal(record.Id, out total);
        return parameter as string switch
        {
            "Text" => bounce ? LocalizationManager.Format("History.BounceBadgeFormat", record.ContactNumber) :
                !scored ? string.Empty : total is double value ? Math.Round(value).ToString("0", culture) : "—",
            "Foreground" => total is double foreground ? LandingScoreView.ScoreBrush(foreground) : LandingScoreView.NeutralBrush,
            "Background" => total is double background ? LandingScoreView.ScoreTintBrush(background) : LandingScoreView.NeutralTintBrush,
            "ToolTip" => bounce ? LocalizationManager.Format("Score.BounceContactFormat", record.ContactNumber, record.ContactCount) :
                !scored ? LocalizationManager.Text("History.ScorePending") : total is double shown
                    ? LocalizationManager.Format("History.ScoreToolTipFormat", LocalizationManager.Text("Score.Grade." + LandingScoreView.Grade(shown)), shown)
                    : LocalizationManager.Text("History.ScoreUnavailable"),
            _ => DependencyProperty.UnsetValue,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
