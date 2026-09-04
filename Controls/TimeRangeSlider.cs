using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClipToGif.Controls;

/// <summary>
/// 双滑块时间区间选择器 + 可拖动播放光标。
/// </summary>
public class TimeRangeSlider : Control
{
    private Canvas? _trackCanvas;
    private Canvas? _rulerCanvas;
    private Border? _selection;
    private Border? _trackHit;
    private Thumb? _startThumb;
    private Thumb? _endThumb;
    private Thumb? _playheadThumb;
    private Border? _rangeHit;
    private ScrollBar? _horizontalScroll;
    private bool _draggingRange;
    private double _dragOffset;
    private double _viewStart;
    private double? _pendingZoomAnchor;
    private bool _updatingScrollBar;

    static TimeRangeSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(typeof(TimeRangeSlider)));
    }

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(0d, OnRangeBoundsChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(100d, OnRangeBoundsChanged));

    public static readonly DependencyProperty StartProperty =
        DependencyProperty.Register(nameof(Start), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnStartEndChanged));

    public static readonly DependencyProperty EndProperty =
        DependencyProperty.Register(nameof(End), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnStartEndChanged));

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public static readonly DependencyProperty MinimumRangeProperty =
        DependencyProperty.Register(nameof(MinimumRange), typeof(double), typeof(TimeRangeSlider),
            new PropertyMetadata(0.2d));

    public static readonly DependencyProperty MaximumRangeProperty =
        DependencyProperty.Register(nameof(MaximumRange), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(0d, OnRangeBoundsChanged));

    public static readonly DependencyProperty ZoomLevelProperty =
        DependencyProperty.Register(nameof(ZoomLevel), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(
                1d,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnZoomLevelChanged,
                CoerceZoomLevel));

    public static readonly RoutedEvent PositionSeekedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(PositionSeeked),
            RoutingStrategy.Bubble,
            typeof(RoutedPropertyChangedEventHandler<double>),
            typeof(TimeRangeSlider));

    public event RoutedPropertyChangedEventHandler<double> PositionSeeked
    {
        add => AddHandler(PositionSeekedEvent, value);
        remove => RemoveHandler(PositionSeekedEvent, value);
    }

    /// <summary>拖动/点击光标时请求跳转（秒）。</summary>
    public event Action<double>? SeekRequested;

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Start
    {
        get => (double)GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public double End
    {
        get => (double)GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double MinimumRange
    {
        get => (double)GetValue(MinimumRangeProperty);
        set => SetValue(MinimumRangeProperty, value);
    }

    /// <summary>选区最长秒数；0 表示不限制。</summary>
    public double MaximumRange
    {
        get => (double)GetValue(MaximumRangeProperty);
        set => SetValue(MaximumRangeProperty, value);
    }

    /// <summary>时间轴缩放倍数。1 表示适合全长，最大 32 倍。</summary>
    public double ZoomLevel
    {
        get => (double)GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    public bool IsDraggingPlayhead { get; private set; }

    public double VisibleStart => _viewStart;

    public double VisibleEnd => Math.Min(Maximum, _viewStart + VisibleSpan);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        Unhook();

        _trackCanvas = GetTemplateChild("PART_TrackCanvas") as Canvas;
        _rulerCanvas = GetTemplateChild("PART_RulerCanvas") as Canvas;
        _selection = GetTemplateChild("PART_Selection") as Border;
        _trackHit = GetTemplateChild("PART_TrackHit") as Border;
        _startThumb = GetTemplateChild("PART_StartThumb") as Thumb;
        _endThumb = GetTemplateChild("PART_EndThumb") as Thumb;
        _playheadThumb = GetTemplateChild("PART_PlayheadThumb") as Thumb;
        _rangeHit = GetTemplateChild("PART_RangeHit") as Border;
        _horizontalScroll = GetTemplateChild("PART_HorizontalScroll") as ScrollBar;

        if (_startThumb is not null) _startThumb.DragDelta += StartThumb_OnDragDelta;
        if (_endThumb is not null) _endThumb.DragDelta += EndThumb_OnDragDelta;

        if (_playheadThumb is not null)
        {
            _playheadThumb.DragStarted += Playhead_OnDragStarted;
            _playheadThumb.DragDelta += Playhead_OnDragDelta;
            _playheadThumb.DragCompleted += Playhead_OnDragCompleted;
        }

        if (_rangeHit is not null)
        {
            _rangeHit.MouseLeftButtonDown += RangeHit_OnMouseLeftButtonDown;
            _rangeHit.MouseMove += RangeHit_OnMouseMove;
            _rangeHit.MouseLeftButtonUp += RangeHit_OnMouseLeftButtonUp;
        }

        if (_trackHit is not null)
            _trackHit.MouseLeftButtonDown += TrackHit_OnMouseLeftButtonDown;

        if (_horizontalScroll is not null)
            _horizontalScroll.ValueChanged += HorizontalScroll_OnValueChanged;

        SizeChanged += TimeRangeSlider_OnSizeChanged;
        PreviewMouseWheel += TimeRangeSlider_OnPreviewMouseWheel;
        UpdateVisuals();
    }

    private void Unhook()
    {
        if (_startThumb is not null) _startThumb.DragDelta -= StartThumb_OnDragDelta;
        if (_endThumb is not null) _endThumb.DragDelta -= EndThumb_OnDragDelta;
        if (_playheadThumb is not null)
        {
            _playheadThumb.DragStarted -= Playhead_OnDragStarted;
            _playheadThumb.DragDelta -= Playhead_OnDragDelta;
            _playheadThumb.DragCompleted -= Playhead_OnDragCompleted;
        }

        if (_rangeHit is not null)
        {
            _rangeHit.MouseLeftButtonDown -= RangeHit_OnMouseLeftButtonDown;
            _rangeHit.MouseMove -= RangeHit_OnMouseMove;
            _rangeHit.MouseLeftButtonUp -= RangeHit_OnMouseLeftButtonUp;
        }

        if (_trackHit is not null)
            _trackHit.MouseLeftButtonDown -= TrackHit_OnMouseLeftButtonDown;

        if (_horizontalScroll is not null)
            _horizontalScroll.ValueChanged -= HorizontalScroll_OnValueChanged;

        SizeChanged -= TimeRangeSlider_OnSizeChanged;
        PreviewMouseWheel -= TimeRangeSlider_OnPreviewMouseWheel;
    }

    public void RefreshVisuals() => UpdateVisuals();

    /// <summary>围绕指定时间点缩放，使该时间点在屏幕上的位置保持不变。</summary>
    public void ZoomAt(double zoomLevel, double anchorSeconds)
    {
        _pendingZoomAnchor = Math.Clamp(anchorSeconds, Minimum, Maximum);
        SetCurrentValue(ZoomLevelProperty, zoomLevel);
        _pendingZoomAnchor = null;
    }

    public void FitToDuration() => ZoomAt(1, Minimum);

    public void EnsureVisible(double seconds)
    {
        if (ZoomLevel <= 1 || seconds >= VisibleStart && seconds <= VisibleEnd)
            return;

        _viewStart = ClampViewStart(seconds - VisibleSpan / 2);
        UpdateVisuals();
    }

    private static void OnRangeBoundsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimeRangeSlider slider)
        {
            slider.CoerceStartEnd();
            slider._viewStart = slider.ClampViewStart(slider._viewStart);
            slider.UpdateVisuals();
        }
    }

    private static void OnStartEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimeRangeSlider slider)
        {
            slider.CoerceStartEnd();
            slider.UpdateVisuals();
        }
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimeRangeSlider slider)
            slider.UpdatePlayhead();
    }

    private static object CoerceZoomLevel(DependencyObject d, object value) =>
        Math.Clamp((double)value, 1d, 32d);

    private static void OnZoomLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TimeRangeSlider slider)
            return;

        var oldZoom = Math.Max(1, (double)e.OldValue);
        var newZoom = Math.Max(1, (double)e.NewValue);
        var fullSpan = slider.FullSpan;
        var oldSpan = fullSpan / oldZoom;
        var newSpan = fullSpan / newZoom;
        var anchor = slider._pendingZoomAnchor ??
                     (slider.Position >= slider._viewStart &&
                      slider.Position <= slider._viewStart + oldSpan
                         ? slider.Position
                         : slider._viewStart + oldSpan / 2);
        var ratio = oldSpan <= 0 ? 0.5 : (anchor - slider._viewStart) / oldSpan;

        slider._viewStart = newZoom <= 1
            ? slider.Minimum
            : slider.ClampViewStart(anchor - ratio * newSpan);
        slider.UpdateVisuals();
    }

    private void TimeRangeSlider_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateVisuals();

    private void TimeRangeSlider_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_trackCanvas is null || !IsEnabled)
            return;

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            var x = e.GetPosition(_trackCanvas).X;
            var anchor = PixelToValueAbsolute(x);
            var factor = e.Delta > 0 ? 1.25 : 0.8;
            ZoomAt(ZoomLevel * factor, anchor);
        }
        else if (ZoomLevel > 1)
        {
            var direction = e.Delta > 0 ? -1 : 1;
            _viewStart = ClampViewStart(_viewStart + direction * VisibleSpan * 0.12);
            UpdateVisuals();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void HorizontalScroll_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingScrollBar)
            return;

        _viewStart = ClampViewStart(e.NewValue);
        UpdateVisuals(updateScrollBar: false);
    }

    private void CoerceStartEnd()
    {
        var min = Minimum;
        var max = Math.Max(min + MinimumRange, Maximum);
        var start = Math.Clamp(Start, min, max);
        var end = Math.Clamp(End, min, max);
        if (end - start < MinimumRange)
        {
            if (Equals(Start, start))
                end = Math.Min(max, start + MinimumRange);
            else
                start = Math.Max(min, end - MinimumRange);
        }

        if (MaximumRange > 0 && end - start > MaximumRange)
        {
            if (Equals(Start, start))
                end = start + MaximumRange;
            else
                start = end - MaximumRange;
        }

        if (!NearlyEqual(Start, start)) SetCurrentValue(StartProperty, start);
        if (!NearlyEqual(End, end)) SetCurrentValue(EndProperty, end);
    }

    private void StartThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var delta = PixelToValue(e.HorizontalChange);
        var minStart = MaximumRange > 0 ? Math.Max(Minimum, End - MaximumRange) : Minimum;
        var next = Math.Clamp(Start + delta, minStart, End - MinimumRange);
        SetCurrentValue(StartProperty, next);
        UpdateVisuals();
    }

    private void EndThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var delta = PixelToValue(e.HorizontalChange);
        var maxEnd = MaximumRange > 0 ? Math.Min(Maximum, Start + MaximumRange) : Maximum;
        var next = Math.Clamp(End + delta, Start + MinimumRange, maxEnd);
        SetCurrentValue(EndProperty, next);
        UpdateVisuals();
    }

    private void Playhead_OnDragStarted(object sender, DragStartedEventArgs e) =>
        IsDraggingPlayhead = true;

    private void Playhead_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var next = Math.Clamp(Position + PixelToValue(e.HorizontalChange), Minimum, Maximum);
        SetPositionAndSeek(next);
    }

    private void Playhead_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        IsDraggingPlayhead = false;
        RaiseSeek(Position);
    }

    private void TrackHit_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_trackCanvas is null) return;
        // 避免抢走起止手柄 / 播放头的拖动
        if (e.OriginalSource is DependencyObject src &&
            (Equals(src, _startThumb) || Equals(src, _endThumb) || Equals(src, _playheadThumb) ||
             IsDescendantOf(src, _startThumb) || IsDescendantOf(src, _endThumb) || IsDescendantOf(src, _playheadThumb)))
            return;

        var x = e.GetPosition(_trackCanvas).X;
        var next = Math.Clamp(PixelToValueAbsolute(x), Minimum, Maximum);
        IsDraggingPlayhead = true;
        SetPositionAndSeek(next);
        IsDraggingPlayhead = false;
        RaiseSeek(next);
        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject? ancestor)
    {
        if (ancestor is null || node is null) return false;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void RangeHit_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_rangeHit is null || _trackCanvas is null) return;

        _draggingRange = true;
        _rangeHit.CaptureMouse();
        var x = e.GetPosition(_trackCanvas).X;
        _dragOffset = x - ValueToPixel(Start);
        e.Handled = true;
    }

    private void RangeHit_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingRange || _trackCanvas is null) return;
        var x = e.GetPosition(_trackCanvas).X - _dragOffset;
        var span = End - Start;
        var nextStart = Math.Clamp(PixelToValueAbsolute(x), Minimum, Maximum - span);
        SetCurrentValue(StartProperty, nextStart);
        SetCurrentValue(EndProperty, nextStart + span);
        UpdateVisuals();
    }

    private void RangeHit_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingRange = false;
        _rangeHit?.ReleaseMouseCapture();
    }

    private void SetPositionAndSeek(double seconds)
    {
        SetCurrentValue(PositionProperty, seconds);
        UpdatePlayhead();
        RaiseSeek(seconds);
    }

    private void RaiseSeek(double seconds)
    {
        SeekRequested?.Invoke(seconds);
        RaiseEvent(new RoutedPropertyChangedEventArgs<double>(seconds, seconds, PositionSeekedEvent));
    }

    private void UpdateVisuals(bool updateScrollBar = true)
    {
        if (_trackCanvas is null || ActualWidth <= 0) return;

        var left = ValueToPixel(Start);
        var right = ValueToPixel(End);
        var width = Math.Max(0, right - left);

        if (_selection is not null)
        {
            Canvas.SetLeft(_selection, left);
            _selection.Width = width;
        }

        if (_rangeHit is not null)
        {
            Canvas.SetLeft(_rangeHit, left);
            _rangeHit.Width = width;
        }

        if (_startThumb is not null)
        {
            Canvas.SetLeft(_startThumb, left - _startThumb.Width / 2);
            _startThumb.Visibility = IsValueVisible(Start) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_endThumb is not null)
        {
            Canvas.SetLeft(_endThumb, right - _endThumb.Width / 2);
            _endThumb.Visibility = IsValueVisible(End) ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdatePlayhead();
        DrawRuler();
        if (updateScrollBar)
            UpdateScrollBar();
    }

    private void UpdatePlayhead()
    {
        if (_playheadThumb is null || ActualWidth <= 0) return;
        var x = ValueToPixel(Math.Clamp(Position, Minimum, Maximum));
        Canvas.SetLeft(_playheadThumb, x - _playheadThumb.Width / 2);
        _playheadThumb.Visibility = IsValueVisible(Position) ? Visibility.Visible : Visibility.Collapsed;
    }

    private double FullSpan => Math.Max(0.0001, Maximum - Minimum);

    private double VisibleSpan => FullSpan / Math.Max(1, ZoomLevel);

    private double TrackWidth => Math.Max(1, (_trackCanvas?.ActualWidth ?? ActualWidth) - 16);

    private double ValueToPixel(double value)
    {
        return 8 + (value - _viewStart) / VisibleSpan * TrackWidth;
    }

    private double PixelToValue(double deltaPx)
    {
        return deltaPx / TrackWidth * VisibleSpan;
    }

    private double PixelToValueAbsolute(double px)
    {
        return _viewStart + (px - 8) / TrackWidth * VisibleSpan;
    }

    private bool IsValueVisible(double value) =>
        value >= VisibleStart - 0.0001 && value <= VisibleEnd + 0.0001;

    private double ClampViewStart(double value)
    {
        if (ZoomLevel <= 1)
            return Minimum;
        return Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum - VisibleSpan));
    }

    private void UpdateScrollBar()
    {
        if (_horizontalScroll is null)
            return;

        _updatingScrollBar = true;
        try
        {
            _horizontalScroll.Minimum = Minimum;
            _horizontalScroll.Maximum = Math.Max(Minimum, Maximum - VisibleSpan);
            _horizontalScroll.ViewportSize = VisibleSpan;
            _horizontalScroll.SmallChange = VisibleSpan * 0.05;
            _horizontalScroll.LargeChange = VisibleSpan * 0.8;
            _horizontalScroll.Value = ClampViewStart(_viewStart);
            _horizontalScroll.IsEnabled = ZoomLevel > 1.001;
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }

    private void DrawRuler()
    {
        if (_rulerCanvas is null || _rulerCanvas.ActualWidth <= 0)
            return;

        _rulerCanvas.Children.Clear();
        var majorStep = ChooseRulerStep(VisibleSpan, TrackWidth);
        var minorStep = majorStep / 5;
        var minorPixels = minorStep / VisibleSpan * TrackWidth;
        var firstMinor = Math.Ceiling(VisibleStart / minorStep) * minorStep;

        for (var value = firstMinor; value <= VisibleEnd + minorStep * 0.1; value += minorStep)
        {
            var x = ValueToPixel(value);
            var majorIndex = Math.Round(value / majorStep);
            var isMajor = Math.Abs(value - majorIndex * majorStep) < minorStep * 0.05;
            if (!isMajor && minorPixels < 7)
                continue;

            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = isMajor ? 11 : 17,
                Y2 = 25,
                Stroke = new SolidColorBrush(isMajor
                    ? Color.FromRgb(148, 163, 184)
                    : Color.FromRgb(71, 85, 105)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            _rulerCanvas.Children.Add(line);

            if (!isMajor)
                continue;

            var label = new TextBlock
            {
                Text = FormatRulerTime(value, majorStep),
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 10,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, x + 4);
            Canvas.SetTop(label, -1);
            _rulerCanvas.Children.Add(label);
        }
    }

    private static double ChooseRulerStep(double visibleSpan, double width)
    {
        double[] steps =
        [
            0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30,
            60, 120, 300, 600, 900, 1800, 3600
        ];
        var desired = visibleSpan / Math.Max(1, width / 90);
        return steps.FirstOrDefault(step => step >= desired, steps[^1]);
    }

    private static string FormatRulerTime(double seconds, double step)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (step < 1)
            return time.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.0001;

    public string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
}
