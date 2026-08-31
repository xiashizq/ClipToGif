using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClipToGif.Models;

namespace ClipToGif.Controls;

public partial class CropOverlay : UserControl
{
    private const double HandleSize = 10;

    private int _videoWidth;
    private int _videoHeight;
    private VideoCrop _crop = VideoCrop.Full(0, 0);

    private enum DragKind { None, Draw, Move, Resize }

    private DragKind _drag;
    private string _resizeHandle = "";
    private Point _dragStart;
    private VideoCrop _dragOrigin = VideoCrop.Full(0, 0);

    public event EventHandler? CropChanged;

    public CropOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RefreshLayout();
        IsVisibleChanged += (_, _) => RefreshLayout();
    }

    public int VideoWidth => _videoWidth;
    public int VideoHeight => _videoHeight;

    public bool HasCrop => !_crop.IsFullFrame(_videoWidth, _videoHeight);

    public VideoCrop? GetCrop() =>
        _crop.IsFullFrame(_videoWidth, _videoHeight) ? null : _crop;

    public void SetVideoSize(int width, int height)
    {
        var changed = _videoWidth != width || _videoHeight != height;
        _videoWidth = Math.Max(0, width);
        _videoHeight = Math.Max(0, height);
        if (changed || _crop.Width <= 0 || _crop.Height <= 0)
            _crop = VideoCrop.Full(_videoWidth, _videoHeight);
        else
            _crop = VideoCrop.Normalize(_crop.X, _crop.Y, _crop.Width, _crop.Height, _videoWidth, _videoHeight);

        Visibility = _videoWidth > 0 && _videoHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshLayout();
        RaiseCropChanged();
    }

    public void ResetCrop()
    {
        _crop = VideoCrop.Full(_videoWidth, _videoHeight);
        RefreshLayout();
        RaiseCropChanged();
    }

    private void RaiseCropChanged() => CropChanged?.Invoke(this, EventArgs.Empty);

    private void RefreshLayout()
    {
        if (!IsVisible || HostCanvas is null || ContentCanvas is null)
            return;

        var content = GetContentRect();
        Canvas.SetLeft(ContentCanvas, content.X);
        Canvas.SetTop(ContentCanvas, content.Y);
        ContentCanvas.Width = Math.Max(0, content.Width);
        ContentCanvas.Height = Math.Max(0, content.Height);
        UpdateCropVisuals();
    }

    private Rect GetContentRect()
    {
        var viewW = ActualWidth;
        var viewH = ActualHeight;
        if (viewW <= 1 || viewH <= 1 || _videoWidth <= 0 || _videoHeight <= 0)
            return new Rect(0, 0, Math.Max(0, viewW), Math.Max(0, viewH));

        var viewAspect = viewW / viewH;
        var videoAspect = _videoWidth / (double)_videoHeight;
        if (viewAspect > videoAspect)
        {
            var w = viewH * videoAspect;
            return new Rect((viewW - w) / 2, 0, w, viewH);
        }

        var h = viewW / videoAspect;
        return new Rect(0, (viewH - h) / 2, viewW, h);
    }

    private void UpdateCropVisuals()
    {
        var cw = ContentCanvas.Width;
        var ch = ContentCanvas.Height;
        if (cw <= 1 || ch <= 1 || _videoWidth <= 0 || _videoHeight <= 0)
            return;

        var rect = SourceToCanvas(_crop);
        Canvas.SetLeft(CropFrame, rect.X);
        Canvas.SetTop(CropFrame, rect.Y);
        CropFrame.Width = Math.Max(0, rect.Width);
        CropFrame.Height = Math.Max(0, rect.Height);

        var hasCrop = HasCrop || _drag == DragKind.Draw;
        DimPath.Visibility = hasCrop ? Visibility.Visible : Visibility.Collapsed;
        CropFrame.BorderBrush = hasCrop
            ? new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF))
            : new SolidColorBrush(Color.FromArgb(0x66, 0x2D, 0xD4, 0xBF));
        CropFrame.Background = hasCrop
            ? new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
            : Brushes.Transparent;
        CropFrame.Cursor = hasCrop ? Cursors.SizeAll : Cursors.Cross;

        if (hasCrop)
        {
            var geometry = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, cw, ch)),
                new RectangleGeometry(rect));
            DimPath.Data = geometry;
        }
        else
        {
            DimPath.Data = null;
        }

        PlaceHandle(HandleNW, rect.Left, rect.Top);
        PlaceHandle(HandleN, rect.Left + rect.Width / 2, rect.Top);
        PlaceHandle(HandleNE, rect.Right, rect.Top);
        PlaceHandle(HandleE, rect.Right, rect.Top + rect.Height / 2);
        PlaceHandle(HandleSE, rect.Right, rect.Bottom);
        PlaceHandle(HandleS, rect.Left + rect.Width / 2, rect.Bottom);
        PlaceHandle(HandleSW, rect.Left, rect.Bottom);
        PlaceHandle(HandleW, rect.Left, rect.Top + rect.Height / 2);

        var showHandles = hasCrop;
        HandleNW.Visibility = HandleN.Visibility = HandleNE.Visibility =
            HandleE.Visibility = HandleSE.Visibility = HandleS.Visibility =
            HandleSW.Visibility = HandleW.Visibility =
            showHandles ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlaceHandle(Thumb handle, double cx, double cy)
    {
        Canvas.SetLeft(handle, cx - HandleSize / 2);
        Canvas.SetTop(handle, cy - HandleSize / 2);
    }

    private Rect SourceToCanvas(VideoCrop crop)
    {
        var sx = ContentCanvas.Width / _videoWidth;
        var sy = ContentCanvas.Height / _videoHeight;
        return new Rect(crop.X * sx, crop.Y * sy, crop.Width * sx, crop.Height * sy);
    }

    private VideoCrop CanvasToSource(Rect canvas, bool normalize = true)
    {
        var sx = _videoWidth / ContentCanvas.Width;
        var sy = _videoHeight / ContentCanvas.Height;
        var x = (int)Math.Round(canvas.X * sx);
        var y = (int)Math.Round(canvas.Y * sy);
        var w = (int)Math.Round(canvas.Width * sx);
        var h = (int)Math.Round(canvas.Height * sy);
        return normalize
            ? VideoCrop.Normalize(x, y, w, h, _videoWidth, _videoHeight)
            : VideoCrop.Clamp(x, y, w, h, _videoWidth, _videoHeight);
    }

    private void Content_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb)
            return;

        _dragStart = e.GetPosition(ContentCanvas);
        _dragOrigin = _crop;
        _drag = DragKind.Draw;
        ContentCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Frame_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasCrop)
            return;

        _dragStart = e.GetPosition(ContentCanvas);
        _dragOrigin = _crop;
        _drag = DragKind.Move;
        ContentCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Content_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragKind.None || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(ContentCanvas);
        if (_drag == DragKind.Draw)
            ApplyDraw(pos);
        else if (_drag == DragKind.Move)
            ApplyMove(pos);

        UpdateCropVisuals();
    }

    private void Content_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        EndDrag();

    private void Content_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_drag is DragKind.Draw or DragKind.Move && e.LeftButton != MouseButtonState.Pressed)
            EndDrag();
    }

    private void EndDrag()
    {
        if (_drag == DragKind.None)
            return;

        if (_drag == DragKind.Draw)
        {
            var pos = Mouse.GetPosition(ContentCanvas);
            if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4)
                _crop = _dragOrigin;
        }

        _drag = DragKind.None;
        _resizeHandle = "";
        ContentCanvas.ReleaseMouseCapture();
        _crop = VideoCrop.Normalize(_crop.X, _crop.Y, _crop.Width, _crop.Height, _videoWidth, _videoHeight);
        UpdateCropVisuals();
        RaiseCropChanged();
    }

    private void ApplyDraw(Point pos)
    {
        var x = Math.Min(_dragStart.X, pos.X);
        var y = Math.Min(_dragStart.Y, pos.Y);
        var w = Math.Abs(pos.X - _dragStart.X);
        var h = Math.Abs(pos.Y - _dragStart.Y);
        w = Math.Max(w, 4);
        h = Math.Max(h, 4);
        _crop = CanvasToSource(new Rect(x, y, w, h), normalize: false);
    }

    private void ApplyMove(Point pos)
    {
        var sx = _videoWidth / ContentCanvas.Width;
        var sy = _videoHeight / ContentCanvas.Height;
        var dx = (int)Math.Round((pos.X - _dragStart.X) * sx);
        var dy = (int)Math.Round((pos.Y - _dragStart.Y) * sy);
        var maxX = Math.Max(0, _videoWidth - _dragOrigin.Width);
        var maxY = Math.Max(0, _videoHeight - _dragOrigin.Height);
        _crop = VideoCrop.Clamp(
            Math.Clamp(_dragOrigin.X + dx, 0, maxX),
            Math.Clamp(_dragOrigin.Y + dy, 0, maxY),
            _dragOrigin.Width,
            _dragOrigin.Height,
            _videoWidth,
            _videoHeight);
    }

    private void Handle_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        _drag = DragKind.Resize;
        _resizeHandle = (sender as FrameworkElement)?.Tag as string ?? "";
        _dragOrigin = _crop;
        e.Handled = true;
    }

    private void Handle_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_drag != DragKind.Resize || ContentCanvas.Width <= 1 || ContentCanvas.Height <= 1)
            return;

        var sx = _videoWidth / ContentCanvas.Width;
        var sy = _videoHeight / ContentCanvas.Height;
        var dx = (int)Math.Round(e.HorizontalChange * sx);
        var dy = (int)Math.Round(e.VerticalChange * sy);

        var x = _dragOrigin.X;
        var y = _dragOrigin.Y;
        var w = _dragOrigin.Width;
        var h = _dragOrigin.Height;
        var handle = _resizeHandle;

        if (handle.Contains('W', StringComparison.Ordinal))
        {
            var nextX = Math.Clamp(x + dx, 0, x + w - VideoCrop.MinSize);
            w += x - nextX;
            x = nextX;
        }

        if (handle.Contains('E', StringComparison.Ordinal))
            w = Math.Clamp(w + dx, VideoCrop.MinSize, _videoWidth - x);

        if (handle.Contains('N', StringComparison.Ordinal))
        {
            var nextY = Math.Clamp(y + dy, 0, y + h - VideoCrop.MinSize);
            h += y - nextY;
            y = nextY;
        }

        if (handle.Contains('S', StringComparison.Ordinal))
            h = Math.Clamp(h + dy, VideoCrop.MinSize, _videoHeight - y);

        _crop = VideoCrop.Clamp(x, y, w, h, _videoWidth, _videoHeight);
        _dragOrigin = _crop;
        UpdateCropVisuals();
    }

    private void Handle_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _drag = DragKind.None;
        _resizeHandle = "";
        _crop = VideoCrop.Normalize(_crop.X, _crop.Y, _crop.Width, _crop.Height, _videoWidth, _videoHeight);
        UpdateCropVisuals();
        RaiseCropChanged();
    }
}
