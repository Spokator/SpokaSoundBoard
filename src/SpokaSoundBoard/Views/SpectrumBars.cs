using System.Windows;
using System.Windows.Media;
using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Views;

/// <summary>Lightweight music visualizer: N rounded bars driven by the player's spectrum analyser.</summary>
public sealed class SpectrumBars : FrameworkElement
{
    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register(nameof(Player), typeof(MusicPlayer), typeof(SpectrumBars), new PropertyMetadata(null));

    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(SpectrumBars),
            new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarCountProperty =
        DependencyProperty.Register(nameof(BarCount), typeof(int), typeof(SpectrumBars), new PropertyMetadata(24));

    private float[] _bands = new float[24];
    private bool _subscribed;
    private TimeSpan _lastFrame;

    public SpectrumBars()
    {
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();
        Loaded += (_, _) => UpdateSubscription();
    }

    public MusicPlayer? Player
    {
        get => (MusicPlayer?)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    private void UpdateSubscription()
    {
        if (IsVisible && IsLoaded) Subscribe();
        else Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // ~30 fps is plenty for a visualizer.
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime - _lastFrame < TimeSpan.FromMilliseconds(30)) return;
            _lastFrame = r.RenderingTime;
        }
        if (_bands.Length != BarCount) _bands = new float[Math.Max(1, BarCount)];
        var player = Player;
        if (player is { IsPlaying: true })
        {
            player.Spectrum.GetBands(_bands);
        }
        else
        {
            var any = false;
            for (var i = 0; i < _bands.Length; i++)
            {
                _bands[i] *= 0.85f;
                any |= _bands[i] > 0.01f;
            }
            if (!any) return;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var n = _bands.Length;
        if (n == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var gap = 2.0;
        var width = Math.Max(1, (ActualWidth - gap * (n - 1)) / n);
        var brush = BarBrush;
        for (var i = 0; i < n; i++)
        {
            var h = Math.Max(2, Math.Clamp(_bands[i], 0, 1) * ActualHeight);
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (width + gap), ActualHeight - h, width, h), width / 2, width / 2);
        }
    }
}
