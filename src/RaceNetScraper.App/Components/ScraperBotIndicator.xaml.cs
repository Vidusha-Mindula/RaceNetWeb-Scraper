using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RaceNetScraper.App.Components;

public partial class ScraperBotIndicator : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ScraperBotIndicator),
        new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Width of the track the mascot walks back and forth across — bound to the
    /// ActualWidth of whatever container hosts it in MainWindow, since the header's available
    /// width depends on the window's own size and can't be known from XAML alone.</summary>
    public static readonly DependencyProperty TrackWidthProperty = DependencyProperty.Register(
        nameof(TrackWidth), typeof(double), typeof(ScraperBotIndicator),
        new PropertyMetadata(0.0, OnTrackWidthChanged));

    public double TrackWidth
    {
        get => (double)GetValue(TrackWidthProperty);
        set => SetValue(TrackWidthProperty, value);
    }

    // Kept as two separate storyboards deliberately: _limbStoryboard (bob/arms/head) has nothing
    // to do with the header's width and is started exactly once, so a window resize can never cut
    // an arm swing off mid-motion. Only _runStoryboard depends on measured layout and gets rebuilt
    // when that changes.
    private Storyboard? _limbStoryboard;
    private Storyboard? _runStoryboard;
    private double _lastRunTrackWidth = -1;

    public ScraperBotIndicator()
    {
        InitializeComponent();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ScraperBotIndicator)d).UpdateActiveAnimationState((bool)e.NewValue);
    }

    private static void OnTrackWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ScraperBotIndicator)d).RebuildRunStoryboardIfChanged();
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateActiveAnimationState(IsActive);
        StartLimbStoryboardOnce();
        RebuildRunStoryboardIfChanged();
    }

    private void UpdateActiveAnimationState(bool active)
    {
        var storyboard = (Storyboard)Resources["ActiveStoryboard"];
        if (active) storyboard.Begin(this, true);
        else storyboard.Stop(this);
    }

    /// <summary>Bob + arm swing + head tilt — an always-on "walking" animation with nothing that
    /// depends on measured layout, so it starts once and is never rebuilt or restarted again.</summary>
    private void StartLimbStoryboardOnce()
    {
        if (_limbStoryboard is not null) return;

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        AddAnimation(sb, "BobTransform", TranslateTransform.YProperty, 0, -6, 0.5, autoReverse: true);
        AddAnimation(sb, "HeadTiltTransform", RotateTransform.AngleProperty, -8, 8, 1.6, autoReverse: true);

        // Rests at -25/25 (see the static RotateTransform.Angle set in XAML) — swings well past
        // that resting point in both directions for a clearly visible wave, alternating arms.
        var armLeft = new DoubleAnimation(-55, 5, TimeSpan.FromSeconds(0.55)) { AutoReverse = true };
        Storyboard.SetTargetName(armLeft, "ArmLeft");
        Storyboard.SetTargetProperty(armLeft, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        sb.Children.Add(armLeft);

        var armRight = new DoubleAnimation(55, -5, TimeSpan.FromSeconds(0.55))
        {
            AutoReverse = true,
            BeginTime = TimeSpan.FromSeconds(0.275)
        };
        Storyboard.SetTargetName(armRight, "ArmRight");
        Storyboard.SetTargetProperty(armRight, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        sb.Children.Add(armRight);

        _limbStoryboard = sb;
        _limbStoryboard.Begin(this, true);
    }

    /// <summary>Side-to-side run across <see cref="TrackWidth"/> — rebuilt whenever that measured
    /// width actually changes (e.g. the window is resized), since the run distance has to match
    /// it. Skips rebuilding on no-op/sub-pixel changes so it doesn't restart on every layout pass.</summary>
    private void RebuildRunStoryboardIfChanged()
    {
        if (!IsLoaded) return;
        if (Math.Abs(TrackWidth - _lastRunTrackWidth) < 1.0) return;
        _lastRunTrackWidth = TrackWidth;

        _runStoryboard?.Stop(this);

        var maxOffset = Math.Max(0, TrackWidth - ActualWidth);
        // Roughly constant walking speed regardless of how wide the header currently is, with a
        // sensible floor so a very narrow window doesn't make it flicker back and forth instantly.
        var runSeconds = Math.Max(2.5, maxOffset / 55.0);

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddAnimation(sb, "RunTransform", TranslateTransform.XProperty, 0, maxOffset, runSeconds, autoReverse: true);

        _runStoryboard = sb;
        _runStoryboard.Begin(this, true);
    }

    private static void AddAnimation(
        Storyboard sb, string targetName, DependencyProperty property, double from, double to, double seconds, bool autoReverse)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = autoReverse };
        Storyboard.SetTargetName(anim, targetName);
        Storyboard.SetTargetProperty(anim, new PropertyPath(property));
        sb.Children.Add(anim);
    }
}
