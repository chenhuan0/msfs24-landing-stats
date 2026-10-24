using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

/// <summary>
/// The touchdown card: four readings over grade bars in the Score tab's colors.
/// Its margin leaves room for the drop shadow on a transparent window.
/// </summary>
internal sealed class TouchdownCardView : Border
{
    private static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily TextFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
    private static readonly Brush ValueBrush = Brush("#F5F3F0");
    private static readonly Brush UnitBrush = Brush("#9A948D");
    private static readonly Brush LabelBrush = Brush("#8A837B");
    private static readonly Brush DividerBrush = Brush("#1CFFFFFF");
    private static readonly Brush NeutralBarBrush = Brush("#2E2B28");
    private static readonly Brush AccentBrush = Brush("#FF7A45");
    private static readonly Brush CardBrush = Frozen(new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString("#F21E1C1A"), (Color)ColorConverter.ConvertFromString("#F2121110"), 90));
    private readonly Ellipse _status = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new TextBlock
    {
        FontFamily = TextFont, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = UnitBrush,
        Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _location = new TextBlock
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 9.5, Foreground = LabelBrush,
        Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Grid _metrics = new Grid();

    public TouchdownCardView()
    {
        Margin = new Thickness(22, 14, 22, 28);
        Padding = new Thickness(18, 12, 18, 14);
        CornerRadius = new CornerRadius(13);
        Background = CardBrush;
        BorderBrush = Brush("#2AFFFFFF");
        BorderThickness = new Thickness(1);
        Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = .5, Color = Colors.Black };
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_location, Dock.Right);
        header.Children.Add(_location);
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(_status);
        heading.Children.Add(_title);
        header.Children.Add(heading);
        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_metrics);
        Child = body;
    }

    internal string Location => _location.Text;

    public void SetCard(TouchdownCard card)
    {
        _title.Text = LocalizationManager.Text("TouchdownCard.Title");
        _location.Text = card.Location;
        var scores = card.Metrics.Where(metric => metric.Score.HasValue).Select(metric => metric.Score!.Value).ToArray();
        // The dot summarizes the weakest reading in the same grade colors.
        _status.Fill = scores.Length == 0 ? AccentBrush : LandingScoreView.ScoreBrush(scores.Min());
        _metrics.Children.Clear();
        _metrics.ColumnDefinitions.Clear();
        for (var index = 0; index < card.Metrics.Count; index++)
        {
            if (index > 0)
            {
                _metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var divider = new Rectangle { Width = 1, Fill = DividerBrush, Margin = new Thickness(16, 3, 16, 3) };
                Grid.SetColumn(divider, _metrics.ColumnDefinitions.Count - 1);
                _metrics.Children.Add(divider);
            }
            _metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 84 });
            var cell = CreateMetric(card.Metrics[index]);
            Grid.SetColumn(cell, _metrics.ColumnDefinitions.Count - 1);
            _metrics.Children.Add(cell);
        }
    }

    private static FrameworkElement CreateMetric(TouchdownCardMetric metric)
    {
        var value = new TextBlock { FontFamily = DisplayFont, Foreground = ValueBrush };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        value.Inlines.Add(new Run(metric.Value) { FontSize = 24, FontWeight = FontWeights.SemiBold });
        if (metric.Unit.Length != 0)
            value.Inlines.Add(new Run(" " + metric.Unit) { FontSize = 11, FontWeight = FontWeights.Normal, Foreground = UnitBrush });
        var label = new TextBlock
        {
            Text = metric.Label, FontFamily = TextFont, FontSize = 9, FontWeight = FontWeights.SemiBold,
            Foreground = LabelBrush, Margin = new Thickness(1, 1, 0, 0),
        };
        var bar = new Border
        {
            Height = 2.5, CornerRadius = new CornerRadius(1.25), Margin = new Thickness(1, 9, 0, 0),
            Background = metric.Score is double score ? LandingScoreView.ScoreBrush(score) : NeutralBarBrush,
        };
        var cell = new StackPanel();
        cell.Children.Add(value);
        cell.Children.Add(label);
        cell.Children.Add(bar);
        return cell;
    }

    private static Brush Brush(string color) => Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
