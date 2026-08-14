using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RaceNetScraper.App.Components;

/// <summary>
/// Cycles through the 12 rasterized gallop frames in Assets/Horse while <see cref="IsRunning"/> is
/// true, mimicking the source SVG's own 0.5s-per-cycle SMIL timing (12 frames * ~41.67ms). Frames
/// are loaded once and cached; the timer itself is only running while animating, so this costs
/// nothing while idle.
/// </summary>
public partial class HorseRunIndicator : System.Windows.Controls.UserControl
{
    private const int FrameCount = 12;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(0.5 / FrameCount);

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(HorseRunIndicator),
        new PropertyMetadata(false, OnIsRunningChanged));

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    private readonly BitmapImage[] _frames = new BitmapImage[FrameCount];
    private readonly DispatcherTimer _timer;
    private int _frameIndex;

    public HorseRunIndicator()
    {
        InitializeComponent();

        for (var i = 0; i < FrameCount; i++)
        {
            _frames[i] = new BitmapImage(new Uri($"pack://application:,,,/Assets/Horse/frame_{i:00}.png"));
        }

        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += (_, _) =>
        {
            _frameIndex = (_frameIndex + 1) % FrameCount;
            FrameImage.Source = _frames[_frameIndex];
        };
    }

    private static void OnIsRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (HorseRunIndicator)d;
        if ((bool)e.NewValue)
        {
            control._frameIndex = 0;
            control.FrameImage.Source = control._frames[0];
            control.FrameImage.Visibility = Visibility.Visible;
            control._timer.Start();
        }
        else
        {
            control._timer.Stop();
            control.FrameImage.Visibility = Visibility.Collapsed;
        }
    }
}
