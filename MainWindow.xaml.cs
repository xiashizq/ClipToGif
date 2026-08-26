using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipToGif.Controls;
using ClipToGif.Localization;
using ClipToGif.Models;
using ClipToGif.Services;
using Microsoft.Win32;

namespace ClipToGif;

public partial class MainWindow : Window
{
    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".ts"
    ];

    private readonly ObservableCollection<VideoItem> _videos = [];
    private readonly FfmpegGifService _ffmpeg = new();
    private readonly ProjectStore _store = new();

    private VideoItem? _currentVideo;
    private CancellationTokenSource? _convertCts;
    private bool _suppressRangeEvents;
    private bool _isGenerating;
    private bool _applyingScale;
    private bool _suppressScaleEvents;

    public ObservableCollection<VideoItem> Videos => _videos;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var startDesc = DependencyPropertyDescriptor.FromProperty(
            TimeRangeSlider.StartProperty, typeof(TimeRangeSlider));
        var endDesc = DependencyPropertyDescriptor.FromProperty(
            TimeRangeSlider.EndProperty, typeof(TimeRangeSlider));
        startDesc?.AddValueChanged(RangeSlider, (_, _) => OnRangeChanged());
        endDesc?.AddValueChanged(RangeSlider, (_, _) => OnRangeChanged());
        RangeSlider.SeekRequested += OnPlayheadSeekRequested;

        Loaded += async (_, _) => await OnLoadedAsync();
        Activated += (_, _) => RefreshCurrentVideoExists();
        Closing += OnWindowClosing;
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
        UpdateLanguageSwitch();
        PopulateCompressionBox();
        PopulateScaleBox();
    }

    private async Task OnLoadedAsync()
    {
        RefreshFfmpegStatus();
        LoadLibrary();
        OnRangeChanged();

        if (_videos.Count > 0)
            VideoList.SelectedIndex = 0;
        else
            SetWorkspaceEnabled(enabled: false, videoMissing: false);

        await Task.CompletedTask;
    }

    private void RefreshFfmpegStatus()
    {
        var path = FfmpegLocator.Find();
        FfmpegStatusText.Text = path is null
            ? Loc.Get("FfmpegNotFound")
            : $"FFmpeg: {Path.GetFileName(Path.GetDirectoryName(path))}\\{Path.GetFileName(path)}";
        FfmpegStatusText.Foreground = path is null
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private void LoadLibrary()
    {
        var data = _store.Load();
        _videos.Clear();
        foreach (var record in data.Videos)
        {
            // 只保存路径链接；即使文件已不存在也保留条目，便于提示
            var video = new VideoItem
            {
                Id = record.Id,
                FilePath = record.FilePath,
                DisplayName = string.IsNullOrWhiteSpace(record.DisplayName)
                    ? Path.GetFileName(record.FilePath)
                    : record.DisplayName,
                Duration = TimeSpan.FromSeconds(record.DurationSeconds),
                Width = record.Width,
                Height = record.Height,
                Fps = record.Fps
            };
            video.RefreshExists();

            foreach (var g in record.Gifs)
            {
                var gifExists = File.Exists(g.FilePath);
                video.Gifs.Add(new GifItem
                {
                    Id = g.Id,
                    VideoId = g.VideoId,
                    FilePath = g.FilePath,
                    DisplayName = g.DisplayName,
                    Start = TimeSpan.FromSeconds(g.StartSeconds),
                    End = TimeSpan.FromSeconds(g.EndSeconds),
                    Width = g.Width,
                    Height = g.Height,
                    Fps = g.Fps,
                    Quality = g.Quality,
                    CreatedAt = g.CreatedAt,
                    FileSizeBytes = g.FileSizeBytes > 0
                        ? g.FileSizeBytes
                        : gifExists ? new FileInfo(g.FilePath).Length : 0,
                    StatusKey = gifExists ? "StatusDone" : "StatusMissing"
                });
            }

            _videos.Add(video);
        }
    }

    private void Persist() => _store.Save(_videos);

    private void AddVideos_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("OpenVideoTitle"),
            Filter = $"{Loc.Get("OpenVideoFilter")}|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.m4v;*.flv;*.ts|{Loc.Get("AllFilesFilter")}|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
            _ = AddVideosAsync(dialog.FileNames);
    }

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;
        _ = AddVideosAsync(files);
    }

    private async Task AddVideosAsync(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsVideoFile(path) || !File.Exists(path))
                continue;
            if (_videos.Any(v => string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var item = new VideoItem
            {
                FilePath = path, // 仅链接路径，不复制/移动文件
                DisplayName = Path.GetFileName(path)
            };
            item.RefreshExists();

            try
            {
                StatusText.Text = Loc.Format("ReadingVideoInfo", item.DisplayName);
                var info = await _ffmpeg.GetMediaInfoAsync(path);
                item.Duration = info.Duration;
                item.Width = info.Width;
                item.Height = info.Height;
                item.Fps = info.Fps;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{Loc.Format("CannotReadVideo", item.DisplayName)}\n{ex.Message}", "ClipToGif",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            _videos.Add(item);
            added++;
        }

        Persist();
        StatusText.Text = added > 0 ? Loc.Format("LinkedVideos", added) : Loc.Get("NoNewVideos");
        if (added > 0)
            VideoList.SelectedItem = _videos[^1];
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void RemoveVideo_OnClick(object sender, RoutedEventArgs e)
    {
        if (VideoList.SelectedItem is not VideoItem video)
            return;

        var confirm = MessageBox.Show(this,
            Loc.Format("ConfirmRemoveVideo", video.DisplayName, Environment.NewLine),
            Loc.Get("ConfirmRemoveTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        var index = _videos.IndexOf(video);
        _videos.Remove(video);
        if (_currentVideo?.Id == video.Id)
        {
            StopPlayback();
            Player.ShowEmpty();
            _currentVideo = null;
            GifList.ItemsSource = null;
            SetWorkspaceEnabled(enabled: false, videoMissing: false);
            StatusText.Text = Loc.Get("Ready");
        }

        Persist();
        if (_videos.Count > 0)
            VideoList.SelectedIndex = Math.Clamp(index, 0, _videos.Count - 1);
        else
            SetWorkspaceEnabled(enabled: false, videoMissing: false);
    }

    private void VideoList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoList.SelectedItem is not VideoItem video)
            return;
        LoadVideo(video);
    }

    private void RefreshCurrentVideoExists()
    {
        if (_currentVideo is null)
            return;

        var wasMissing = _currentVideo.IsMissing;
        _currentVideo.RefreshExists();
        if (wasMissing != _currentVideo.IsMissing || _currentVideo.IsMissing)
            LoadVideo(_currentVideo);
    }

    private void LoadVideo(VideoItem video)
    {
        StopPlayback();
        _currentVideo = video;
        video.RefreshExists();

        GifList.ItemsSource = video.Gifs;
        UpdateGifCount(video.Gifs.Count);

        if (video.IsMissing)
        {
            Player.ShowMissing(video.FilePath);
            StatusText.Text = Loc.Get("VideoMissing");
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            SetWorkspaceEnabled(enabled: false, videoMissing: true);
            return;
        }

        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        StatusText.Text = Loc.Format("LinkedVideo", video.DisplayName);
        SetWorkspaceEnabled(enabled: true, videoMissing: false);
        Player.Open(video.FilePath);

        if (video.Duration.TotalSeconds > 0)
            ApplyFullDuration(video.Duration);

        ApplyGifDefaultsFromVideo(video);

        if (video.Width <= 0 || video.Height <= 0 || video.Fps <= 0)
            _ = EnsureMediaInfoAsync(video);
    }

    /// <summary>
    /// 源文件缺失时禁用页面操作按钮与参数；保留「移除」以便删除失效链接，列表仍可切换其他视频。
    /// </summary>
    private void SetWorkspaceEnabled(bool enabled, bool videoMissing)
    {
        // 缺失时除「移除」外全部禁用；无选中时仍可导入
        ImportButton.IsEnabled = !videoMissing;
        RemoveButton.IsEnabled = _currentVideo is not null || VideoList.SelectedItem is not null;
        Player.SetChromeEnabled(enabled && !videoMissing);
        RangeSlider.IsEnabled = enabled && !_isGenerating;
        ExportPanel.IsEnabled = enabled || _isGenerating;
        GenerateButton.IsEnabled = enabled && !_isGenerating;
        if (CancelGenerateButton is not null)
            CancelGenerateButton.IsEnabled = _isGenerating;
        OpenGifButton.IsEnabled = enabled;
        OpenGifFolderButton.IsEnabled = enabled;
        DeleteGifButton.IsEnabled = enabled;
        GifList.IsEnabled = enabled;
        AllowDrop = !videoMissing;

        if (videoMissing)
            ImportButton.IsEnabled = false;
    }

    private async Task EnsureMediaInfoAsync(VideoItem video)
    {
        video.RefreshExists();
        if (video.IsMissing)
            return;

        try
        {
            var info = await _ffmpeg.GetMediaInfoAsync(video.FilePath);
            if (_currentVideo?.Id != video.Id)
                return;

            if (info.Duration > TimeSpan.Zero)
                video.Duration = info.Duration;
            if (info.Width > 0) video.Width = info.Width;
            if (info.Height > 0) video.Height = info.Height;
            if (info.Fps > 0) video.Fps = info.Fps;

            if (video.Duration.TotalSeconds > 0)
                ApplyFullDuration(video.Duration);
            ApplyGifDefaultsFromVideo(video);
            Persist();
        }
        catch
        {
            // 忽略，保留现有默认值
        }
    }

    private void ApplyGifDefaultsFromVideo(VideoItem video)
    {
        if (!ApplySelectedScale())
        {
            if (video.Width > 0)
                WidthBox.Text = video.Width.ToString(CultureInfo.InvariantCulture);
            if (KeepAspectCheck.IsChecked == true)
                UpdateHeightFromAspect();
            else if (video.Height > 0)
                HeightBox.Text = video.Height.ToString(CultureInfo.InvariantCulture);
        }

        if (video.Fps > 0)
        {
            var fpsText = video.Fps % 1 == 0
                ? ((int)video.Fps).ToString(CultureInfo.InvariantCulture)
                : video.Fps.ToString("0.###", CultureInfo.InvariantCulture);
            FpsBox.Text = fpsText;
        }
    }

    private void Player_OnMediaOpened(object? sender, EventArgs e)
    {
        if (_currentVideo is null || Player.DurationSeconds <= 0)
            return;

        var duration = TimeSpan.FromSeconds(Player.DurationSeconds);
        _currentVideo.Duration = duration;
        ApplyFullDuration(duration);
        Persist();
    }

    private void Player_OnPositionChanged(object? sender, double seconds)
    {
        if (RangeSlider.IsDraggingPlayhead)
            return;
        RangeSlider.Position = seconds;
        UpdatePositionLabels(seconds);
        UpdatePlayerTimeText(seconds);
    }

    private void Player_OnSeekToStartRequested(object? sender, EventArgs e)
    {
        if (_currentVideo is null) return;
        SeekTo(RangeSlider.Start);
    }

    private void UpdatePlayerTimeText(double seconds)
    {
        if (PlayerTimeText is null) return;
        var total = Player.DurationSeconds > 0 ? Player.DurationSeconds : RangeSlider.Maximum;
        PlayerTimeText.Text = $"{PreviewPlayer.FormatTime(seconds)} / {PreviewPlayer.FormatTime(total)}";
    }

    /// <summary>
    /// 将轨道映射到整段视频；默认选中全长。
    /// </summary>
    private void ApplyFullDuration(TimeSpan duration)
    {
        var totalSeconds = duration.TotalSeconds;
        _suppressRangeEvents = true;
        RangeSlider.Minimum = 0;
        RangeSlider.Maximum = totalSeconds;
        RangeSlider.Start = 0;
        RangeSlider.End = totalSeconds;
        RangeSlider.Position = 0;
        _suppressRangeEvents = false;

        DurationStartLabel.Text = "00:00";
        DurationEndLabel.Text = Loc.Format("TotalDuration", FormatTime(totalSeconds));
        OnRangeChanged();
        UpdatePositionLabels(0);
        UpdatePlayerTimeText(0);

        // 布局完成后按全宽重算手柄位置
        RangeSlider.Dispatcher.BeginInvoke(() => RangeSlider.RefreshVisuals(), DispatcherPriority.Loaded);
    }

    private void Player_OnMediaEnded(object? sender, EventArgs e)
    {
        if (RangeSlider.Maximum > 0)
        {
            RangeSlider.Position = RangeSlider.Maximum;
            UpdatePositionLabels(RangeSlider.Maximum);
        }
    }

    private void OnPlayheadSeekRequested(double seconds)
    {
        if (_currentVideo is null || !Player.HasMedia)
            return;

        seconds = Math.Clamp(seconds, 0, Math.Max(0, RangeSlider.Maximum));
        Player.Seek(seconds);
        UpdatePositionLabels(seconds);
    }

    private void SeekTo(double seconds, bool updateSlider = true)
    {
        seconds = Math.Clamp(seconds, 0, Math.Max(0, RangeSlider.Maximum));
        if (updateSlider)
            RangeSlider.Position = seconds;
        OnPlayheadSeekRequested(seconds);
    }

    private void OnRangeChanged()
    {
        if (_suppressRangeEvents) return;
        StartLabel.Text = Loc.Format("RangeStart", FormatTime(RangeSlider.Start));
        EndLabel.Text = Loc.Format("RangeEnd", FormatTime(RangeSlider.End));
        var span = RangeSlider.End - RangeSlider.Start;
        var spanText = span >= 60 ? FormatTime(span) : $"{span:0.0}s";
        SelectionLabel.Text = Loc.Format("Selection", spanText);
    }

    private void UpdatePositionLabels(double seconds) =>
        PositionLabel.Text = Loc.Format("CurrentPosition", FormatTime(seconds));

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void QualitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityValueText is not null)
            QualityValueText.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private void KeepAspectCheck_OnChanged(object sender, RoutedEventArgs e)
    {
        if (HeightBox is null || KeepAspectCheck is null) return;
        var keep = KeepAspectCheck.IsChecked == true;
        HeightBox.IsReadOnly = keep;
        HeightBox.IsTabStop = !keep;
        if (keep)
            UpdateHeightFromAspect();
    }

    private void WidthBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHeightFromAspect();
        if (!_applyingScale)
            SyncScaleFromWidth();
    }

    private void PopulateScaleBox()
    {
        if (ScaleBox is null) return;
        var selected = ScaleBox.SelectedValue as string ?? "100";
        _suppressScaleEvents = true;
        ScaleBox.ItemsSource = ScaleChoice.CreateAll();
        ScaleBox.SelectedValue = selected;
        if (ScaleBox.SelectedItem is null)
            ScaleBox.SelectedValue = "100";
        _suppressScaleEvents = false;
    }

    private void ScaleBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressScaleEvents) return;
        ApplySelectedScale();
    }

    private bool ApplySelectedScale()
    {
        if (ScaleBox?.SelectedItem is not ScaleChoice choice || choice.Factor <= 0)
            return false;
        return ApplyScale(choice.Factor);
    }

    private bool ApplyScale(double factor)
    {
        var srcW = _currentVideo?.Width ?? 0;
        var srcH = _currentVideo?.Height ?? 0;
        if (srcW <= 0 || WidthBox is null || HeightBox is null)
            return false;

        var width = Math.Clamp((int)Math.Round(srcW * factor), 16, 4096);
        _applyingScale = true;
        try
        {
            WidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
            if (KeepAspectCheck.IsChecked == true)
                UpdateHeightFromAspect();
            else if (srcH > 0)
                HeightBox.Text = Math.Clamp((int)Math.Round(srcH * factor), 1, 4096)
                    .ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _applyingScale = false;
        }

        return true;
    }

    private void SyncScaleFromWidth()
    {
        if (ScaleBox is null) return;
        var srcW = _currentVideo?.Width ?? 0;
        if (srcW <= 0) return;
        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 1)
            return;

        var ratio = width / (double)srcW;
        var match = ScaleChoice.CreateAll()
            .FirstOrDefault(c => c.Factor > 0 && Math.Abs(c.Factor - ratio) < 0.02);

        _suppressScaleEvents = true;
        ScaleBox.SelectedValue = match?.Id ?? "custom";
        _suppressScaleEvents = false;
    }

    private void UpdateHeightFromAspect()
    {
        if (KeepAspectCheck?.IsChecked != true || HeightBox is null || WidthBox is null)
            return;

        var srcW = _currentVideo?.Width ?? 0;
        var srcH = _currentVideo?.Height ?? 0;
        if (srcW <= 0 || srcH <= 0)
            return;

        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 1)
            return;

        var height = Math.Max(1, (int)Math.Round(width * (double)srcH / srcW));
        var text = height.ToString(CultureInfo.InvariantCulture);
        if (HeightBox.Text != text)
            HeightBox.Text = text;
    }

    private void PopulateCompressionBox()
    {
        if (CompressionBox is null) return;
        var selected = CompressionBox.SelectedValue is GifCompressionMode mode
            ? mode
            : GifCompressionMode.None;
        CompressionBox.ItemsSource = CompressionChoice.CreateAll();
        CompressionBox.SelectedValue = selected;
        UpdateCompressionTooltip();
        UpdateQualityEnabled();
    }

    private void CompressionBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCompressionTooltip();
        UpdateQualityEnabled();
    }

    private void UpdateCompressionTooltip()
    {
        if (CompressionBox?.SelectedItem is CompressionChoice choice)
            CompressionBox.ToolTip = choice.Description;
    }

    private void UpdateQualityEnabled()
    {
        if (QualitySlider is null) return;
        var lossless = CompressionBox.SelectedValue is GifCompressionMode mode &&
                       mode is GifCompressionMode.LosslessTransdiff
                           or GifCompressionMode.LosslessRectangle
                           or GifCompressionMode.LosslessPaletteDiff;
        QualitySlider.IsEnabled = !lossless;
    }

    private async void GenerateGif_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null)
        {
            MessageBox.Show(this, Loc.Get("SelectVideoFirst"), "ClipToGif", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _currentVideo.RefreshExists();
        if (_currentVideo.IsMissing)
        {
            LoadVideo(_currentVideo);
            return;
        }

        if (FfmpegLocator.Find() is null)
        {
            MessageBox.Show(this,
                Loc.Format("FfmpegMissingBody", Environment.NewLine),
                Loc.Get("FfmpegMissingTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshFfmpegStatus();
            return;
        }

        if (!TryReadSettings(out var settings, out var error))
        {
            MessageBox.Show(this, error, Loc.Get("InvalidParams"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var start = TimeSpan.FromSeconds(RangeSlider.Start);
        var end = TimeSpan.FromSeconds(RangeSlider.End);
        if (end - start < TimeSpan.FromMilliseconds(200))
        {
            MessageBox.Show(this, Loc.Get("RangeTooShort"), "ClipToGif",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fileName = CreateGifFileName();
        var outputDir = Path.Combine(_store.OutputDirectory, _currentVideo.Id.ToString("N"));
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, fileName);

        var owner = _currentVideo;
        var pending = new GifItem
        {
            VideoId = owner.Id,
            FilePath = outputPath,
            DisplayName = fileName,
            Start = start,
            End = end,
            Width = settings.Width,
            Height = settings.Height,
            Fps = settings.Fps,
            Quality = settings.Quality,
            StatusKey = "StatusGenerating"
        };
        owner.Gifs.Insert(0, pending);
        UpdateGifCount(owner.Gifs.Count);
        GifList.SelectedItem = pending;

        _convertCts?.Cancel();
        _convertCts = new CancellationTokenSource();
        var cts = _convertCts;
        SetGeneratingUi(generating: true);
        ProgressBar.Value = 0;
        StatusText.Text = Loc.Get("GeneratingGif");

        var progress = new Progress<double>(p =>
        {
            if (cts.IsCancellationRequested) return;
            ProgressBar.Value = p;
            StatusText.Text = Loc.Format("GeneratingGifProgress", p);
        });

        try
        {
            await _ffmpeg.ConvertAsync(new GifConversionRequest
            {
                VideoPath = owner.FilePath,
                OutputPath = outputPath,
                Start = start,
                End = end,
                Settings = settings,
                Progress = progress,
                CancellationToken = _convertCts.Token
            });

            pending.FileSizeBytes = new FileInfo(outputPath).Length;
            pending.StatusKey = "StatusDone";
            pending.RefreshThumbnail();
            StatusText.Text = Loc.Format("GenerateComplete", fileName);
            ProgressBar.Value = 1;
            Persist();
            GifList.Items.Refresh();
        }
        catch (OperationCanceledException)
        {
            DiscardPendingGif(owner, pending, outputPath);
            StatusText.Text = Loc.Get("GenerateCanceled");
            ProgressBar.Value = 0;
        }
        catch (Exception ex)
        {
            pending.StatusKey = "StatusFailed";
            StatusText.Text = Loc.Get("GenerateFailed");
            MessageBox.Show(this, ex.Message, Loc.Get("GenerateFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetGeneratingUi(generating: false);
        }
    }

    private void CancelGenerate_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isGenerating)
            return;

        CancelGenerateButton.IsEnabled = false;
        StatusText.Text = Loc.Get("CancellingGenerate");
        _convertCts?.Cancel();
    }

    private void SetGeneratingUi(bool generating)
    {
        _isGenerating = generating;
        GenerateButton.Visibility = generating ? Visibility.Collapsed : Visibility.Visible;
        CancelGenerateButton.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        CancelGenerateButton.IsEnabled = generating;

        if (_currentVideo is not null)
            SetWorkspaceEnabled(!_currentVideo.IsMissing, _currentVideo.IsMissing);
        else
            SetWorkspaceEnabled(enabled: false, videoMissing: false);
    }

    private void DiscardPendingGif(VideoItem owner, GifItem pending, string outputPath)
    {
        pending.ReleaseThumbnail();
        try
        {
            DeleteFileWithRetry(outputPath);
        }
        catch
        {
            // 取消后尽量清理不完整文件，失败则忽略
        }

        owner.Gifs.Remove(pending);
        if (ReferenceEquals(_currentVideo, owner))
            UpdateGifCount(owner.Gifs.Count);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _convertCts?.Cancel();
        Persist();
    }

    private bool TryReadSettings(out GifExportSettings settings, out string error)
    {
        settings = new GifExportSettings();
        error = string.Empty;

        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 16)
        {
            error = Loc.Get("WidthInvalid");
            return false;
        }

        if (!int.TryParse(HeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height < 0)
        {
            error = Loc.Get("HeightInvalid");
            return false;
        }

        if (!double.TryParse(FpsBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps is < 1 or > 60)
        {
            error = Loc.Get("FpsInvalid");
            return false;
        }

        settings.Width = width;
        settings.Height = KeepAspectCheck.IsChecked == true && height <= 0 ? 0 : height;
        settings.Fps = fps;
        settings.Quality = (int)QualitySlider.Value;
        settings.KeepAspectRatio = KeepAspectCheck.IsChecked == true || height <= 0;
        settings.Compression = CompressionBox.SelectedValue is GifCompressionMode mode
            ? mode
            : GifCompressionMode.None;
        return true;
    }

    private void GifList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // reserved for future preview
    }

    private void OpenGif_OnClick(object sender, RoutedEventArgs e)
    {
        if (GifList.SelectedItem is not GifItem gif || !File.Exists(gif.FilePath))
        {
            MessageBox.Show(this, Loc.Get("SelectValidGif"), "ClipToGif", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(gif.FilePath) { UseShellExecute = true });
    }

    private void OpenGifFolder_OnClick(object sender, RoutedEventArgs e)
    {
        string? dir = null;
        if (GifList.SelectedItem is GifItem gif)
            dir = Path.GetDirectoryName(gif.FilePath);
        dir ??= _currentVideo is null
            ? _store.OutputDirectory
            : Path.Combine(_store.OutputDirectory, _currentVideo.Id.ToString("N"));

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void DeleteGif_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null || GifList.SelectedItem is not GifItem gif)
            return;

        if (_isGenerating && gif.StatusKey == "StatusGenerating")
        {
            CancelGenerate_OnClick(sender, e);
            return;
        }

        var confirm = MessageBox.Show(this, Loc.Format("ConfirmDeleteGif", gif.DisplayName), Loc.Get("ConfirmDeleteTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        var path = gif.FilePath;
        gif.ReleaseThumbnail();

        try
        {
            DeleteFileWithRetry(path);
        }
        catch (Exception ex)
        {
            // 删除失败：保留列表项并恢复缩略图
            gif.RefreshThumbnail();
            MessageBox.Show(this, Loc.Format("DeleteFailed", ex.Message), "ClipToGif",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 仅在文件删除成功后从列表移除
        _currentVideo.Gifs.Remove(gif);
        UpdateGifCount(_currentVideo.Gifs.Count);
        Persist();
    }

    private static void DeleteFileWithRetry(string path, int retries = 8)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        for (var i = 0; i < retries; i++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            catch (IOException) when (i < retries - 1)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(40 * (i + 1));
            }
        }
    }

    private void StopPlayback()
    {
        try { Player.Pause(); } catch { /* ignore */ }
    }

    private void LangZh_OnClick(object sender, RoutedEventArgs e) => SwitchLanguage(Loc.Chinese);

    private void LangEn_OnClick(object sender, RoutedEventArgs e) => SwitchLanguage(Loc.English);

    private void SwitchLanguage(string code)
    {
        if (string.Equals(code, Loc.Current, StringComparison.OrdinalIgnoreCase))
            return;
        Loc.SetLanguage(code);
    }

    private void UpdateLanguageSwitch()
    {
        var zh = string.Equals(Loc.Current, Loc.Chinese, StringComparison.OrdinalIgnoreCase);
        LangZhButton.Style = (Style)FindResource(zh ? "LanguageChipActive" : "LanguageChip");
        LangEnButton.Style = (Style)FindResource(zh ? "LanguageChip" : "LanguageChipActive");
    }

    private void OnLanguageChanged()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateLanguageSwitch();
            PopulateCompressionBox();
            PopulateScaleBox();

            RefreshFfmpegStatus();
            UpdateGifCount(_currentVideo?.Gifs.Count ?? 0);
            OnRangeChanged();
            UpdatePositionLabels(RangeSlider.Position);
            if (RangeSlider.Maximum > 1)
                DurationEndLabel.Text = Loc.Format("TotalDuration", FormatTime(RangeSlider.Maximum));

            foreach (var video in _videos)
            {
                video.NotifyLocalized();
                foreach (var gif in video.Gifs)
                    gif.NotifyLocalized();
            }

            Player.ApplyLanguage();

            if (_isGenerating)
                StatusText.Text = Loc.Get("GeneratingGif");
            else if (_currentVideo is null)
                StatusText.Text = Loc.Get("Ready");
            else if (_currentVideo.IsMissing)
                StatusText.Text = Loc.Get("VideoMissing");
            else
                StatusText.Text = Loc.Format("LinkedVideo", _currentVideo.DisplayName);
        });
    }

    private void UpdateGifCount(int count) =>
        GifCountText.Text = Loc.Format("GifCount", count);

    private static string CreateGifFileName()
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var suffix = Random.Shared.Next(0x10000).ToString("x4", CultureInfo.InvariantCulture);
        return $"gif{stamp}{suffix}.gif";
    }
}
