using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Discoteka.Desktop.Controls;

/// <summary>
/// Displays text that scrolls horizontally like a marquee when it overflows the container.
/// Pauses at each end before scrolling back.
/// </summary>
public class MarqueeTextBlock : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, string>(nameof(Text), string.Empty);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private enum MarqueeState { InitialPause, Scrolling, EndPause }

    private readonly TextBlock _inner;
    private readonly Canvas _clip;
    private DispatcherTimer? _timer;
    private MarqueeState _state = MarqueeState.InitialPause;
    private DateTime _pauseEnd;
    private DateTime _lastTick;
    private double _scrollX;

    private const double PixelsPerSecond = 32;
    private const double InitialPauseSeconds = 1.5;
    private const double EndPauseSeconds = 1.5;

    public MarqueeTextBlock()
    {
        _inner = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        _clip = new Canvas { ClipToBounds = true };
        _clip.Children.Add(_inner);
        Content = _clip;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            OnTextChanged((string)(change.NewValue ?? string.Empty));
    }

    private void OnTextChanged(string text)
    {
        _inner.Text = text;
        ResetScroll();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _clip.Width = finalSize.Width;
        _clip.Height = finalSize.Height;
        _inner.Height = finalSize.Height;
        var result = base.ArrangeOverride(finalSize);
        UpdateScrollState();
        return result;
    }

    private void ResetScroll()
    {
        _scrollX = 0;
        Canvas.SetLeft(_inner, 0);
        _state = MarqueeState.InitialPause;
        _pauseEnd = DateTime.UtcNow.AddSeconds(InitialPauseSeconds);
        _lastTick = DateTime.UtcNow;
        UpdateScrollState();
    }

    private void UpdateScrollState()
    {
        _inner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = _inner.DesiredSize.Width;
        var containerWidth = Bounds.Width;
        var needsScroll = textWidth > containerWidth && containerWidth > 0;

        if (needsScroll && _timer == null)
        {
            _lastTick = DateTime.UtcNow;
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        else if (!needsScroll && _timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
            _scrollX = 0;
            Canvas.SetLeft(_inner, 0);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        switch (_state)
        {
            case MarqueeState.InitialPause:
                _lastTick = now;
                if (now >= _pauseEnd)
                    _state = MarqueeState.Scrolling;
                break;

            case MarqueeState.Scrolling:
                var elapsed = (now - _lastTick).TotalSeconds;
                _lastTick = now;

                _inner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var textWidth = _inner.DesiredSize.Width;
                var containerWidth = Bounds.Width;
                var maxScroll = textWidth - containerWidth + 8;

                _scrollX = Math.Min(_scrollX + PixelsPerSecond * elapsed, maxScroll);
                Canvas.SetLeft(_inner, -_scrollX);

                if (_scrollX >= maxScroll)
                {
                    _state = MarqueeState.EndPause;
                    _pauseEnd = now.AddSeconds(EndPauseSeconds);
                }
                break;

            case MarqueeState.EndPause:
                _lastTick = now;
                if (now >= _pauseEnd)
                {
                    _scrollX = 0;
                    Canvas.SetLeft(_inner, 0);
                    _state = MarqueeState.InitialPause;
                    _pauseEnd = now.AddSeconds(InitialPauseSeconds);
                }
                break;
        }
    }
}
