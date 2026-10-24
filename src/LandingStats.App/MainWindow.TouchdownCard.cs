using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Telemetry;

namespace LandingStats.App;

public partial class MainWindow
{
    /// <summary>An older notice is a recovered journal effect, not a landing that has just happened.</summary>
    internal static readonly TimeSpan TouchdownCardFreshness = TimeSpan.FromSeconds(10);
    private bool _changingTouchdownCardSelection;
    private bool _changingTouchdownCardPosition;
    private Guid _touchdownCardEpisode;
    private TouchdownCardWindow? _touchdownCardWindow;

    private void PresentTouchdownNotice(RuntimeTouchdownNotice? notice)
    {
        if (notice == null || notice.Episode == _touchdownCardEpisode) return;
        // Consume the notice even when hidden, so enabling the card later never replays it.
        _touchdownCardEpisode = notice.Episode;
        if (!_settings.TouchdownCardEnabled || !IsFreshTouchdownNotice(notice.Readings.ObservedUtc, DateTime.UtcNow)) return;
        ShowTouchdownCard(TouchdownCard.Create(notice.Readings));
    }

    internal static bool IsFreshTouchdownNotice(DateTime observedUtc, DateTime nowUtc) =>
        nowUtc - observedUtc <= TouchdownCardFreshness && observedUtc - nowUtc <= TouchdownCardFreshness;

    private void ShowTouchdownCard(TouchdownCard card)
    {
        if (_isClosed) return;
        (_touchdownCardWindow ??= new TouchdownCardWindow()).Present(card, TouchdownCardPositions.Parse(_settings.TouchdownCardPosition));
    }

    private void CloseTouchdownCard()
    {
        _touchdownCardWindow?.Close();
        _touchdownCardWindow = null;
    }

    private void OnTouchdownCardPreviewClick(object sender, RoutedEventArgs args) =>
        ShowTouchdownCard(TouchdownCard.Create(PreviewTouchdownNotice()));

    /// <summary>A typical airliner landing on a 3,800 m runway, for previewing the card.</summary>
    internal static TouchdownNoticeEventArgs PreviewTouchdownNotice()
    {
        var runway = new RunwayFacility
        {
            AirportIdent = "ZSPD", LatitudeDegrees = 31.1434, LongitudeDegrees = 121.8052, HeadingTrueDegrees = 168,
            LengthMeters = 3800, WidthMeters = 60, PrimaryName = "17L", SecondaryName = "35R",
            PrimaryThresholdOffsetMeters = 0, SecondaryThresholdOffsetMeters = 0,
        };
        return new TouchdownNoticeEventArgs(0, DateTime.UtcNow, -132, 1.24, 140000, 138, runway, false, 412, 1.8);
    }

    private RadioButton[] TouchdownCardPositionChoices => new[]
    {
        SettingsTouchdownCardTopLeft, SettingsTouchdownCardTopCenter, SettingsTouchdownCardTopRight, SettingsTouchdownCardBottomCenter,
    };

    private void RefreshTouchdownCardSettingsPresentation()
    {
        _changingTouchdownCardSelection = true;
        try { SettingsTouchdownCardCheckBox.IsChecked = _settings.TouchdownCardEnabled; }
        finally { _changingTouchdownCardSelection = false; }
        var position = TouchdownCardPositions.Parse(_settings.TouchdownCardPosition).ToString();
        _changingTouchdownCardPosition = true;
        try { foreach (var choice in TouchdownCardPositionChoices) choice.IsChecked = Equals(choice.Tag, position); }
        finally { _changingTouchdownCardPosition = false; }
    }

    private async void OnTouchdownCardPositionChanged(object sender, RoutedEventArgs args)
    {
        if (_changingTouchdownCardPosition || !_startupReady || SettingsBusy || _settingsOperations.IsStopping ||
            sender is not RadioButton { IsChecked: true, Tag: string tag }) return;
        var position = TouchdownCardPositions.Parse(tag).ToString();
        if (position == TouchdownCardPositions.Parse(_settings.TouchdownCardPosition).ToString()) return;
        await SaveTouchdownCardSettingAsync(settings => settings.TouchdownCardPosition = position);
    }

    private async void OnTouchdownCardSelectionChanged(object sender, RoutedEventArgs args)
    {
        if (_changingTouchdownCardSelection || !_startupReady || SettingsBusy || _settingsOperations.IsStopping) return;
        var enabled = SettingsTouchdownCardCheckBox.IsChecked == true;
        if (enabled == _settings.TouchdownCardEnabled) return;
        await SaveTouchdownCardSettingAsync(settings => settings.TouchdownCardEnabled = enabled);
    }

    private async Task SaveTouchdownCardSettingAsync(Action<ApplicationSettings> change)
    {
        string? error = null;
        await RunSettingsOperationAsync(async () =>
        {
            var snapshot = _settings.Clone();
            change(snapshot);
            try
            {
                await _services.Settings.SaveAsync(_settings, snapshot);
                _settings.CopyFrom(_services.Settings.Snapshot.Settings);
            }
            catch (Exception exception) { error = exception.Message; }
            return error == null;
        });
        if (_isClosed || error == null) return;
        SettingsErrorText.Text = LocalizationManager.Format("Settings.SaveErrorFormat", error);
        SettingsErrorText.Visibility = Visibility.Visible;
    }
}
