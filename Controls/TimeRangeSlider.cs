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
    private Border? _selection;
    private Border? _trackHit;
    private Thumb? _startThumb;
    private Thumb? _endThumb;
    private Thumb? _playheadThumb;
    private Border? _rangeHit;
    private bool _draggingRange;
    private double _dragOffset;

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

    public bool IsDraggingPlayhead { get; private set; }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        Unhook();

        _trackCanvas = GetTemplateChild("PART_TrackCanvas") as Canvas;
        _selection = GetTemplateChild("PART_Selection") as Border;
        _trackHit = GetTemplateChild("PART_TrackHit") as Border;
        _startThumb = GetTemplateChild("PART_StartThumb") as Thumb;
        _endThumb = GetTemplateChild("PART_EndThumb") as Thumb;
        _playheadThumb = GetTemplateChild("PART_PlayheadThumb") as Thumb;
        _rangeHit = GetTemplateChild("PART_RangeHit") as Border;

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

        SizeChanged += (_, _) => UpdateVisuals();
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
    }

    public void RefreshVisuals() => UpdateVisuals();

    private static void OnRangeBoundsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimeRangeSlider slider)
        {
            slider.CoerceStartEnd();
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

    private void UpdateVisuals()
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
            Canvas.SetLeft(_startThumb, left - _startThumb.Width / 2);

        if (_endThumb is not null)
            Canvas.SetLeft(_endThumb, right - _endThumb.Width / 2);

        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        if (_playheadThumb is null || ActualWidth <= 0) return;
        var x = ValueToPixel(Math.Clamp(Position, Minimum, Maximum));
        Canvas.SetLeft(_playheadThumb, x - _playheadThumb.Width / 2);
    }

    private double TrackWidth => Math.Max(1, ActualWidth - 16);

    private double ValueToPixel(double value)
    {
        var span = Math.Max(0.0001, Maximum - Minimum);
        return 8 + (value - Minimum) / span * TrackWidth;
    }

    private double PixelToValue(double deltaPx)
    {
        var span = Math.Max(0.0001, Maximum - Minimum);
        return deltaPx / TrackWidth * span;
    }

    private double PixelToValueAbsolute(double px)
    {
        var span = Math.Max(0.0001, Maximum - Minimum);
        return Minimum + (px - 8) / TrackWidth * span;
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.0001;

    public string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
}
