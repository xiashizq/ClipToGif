using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipToGif.Controls;
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
        Closing += (_, _) => Persist();
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
            ? "未检测到 FFmpeg"
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
                    Status = gifExists ? "完成" : "文件缺失"
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
            Title = "选择视频",
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.m4v;*.flv;*.ts|所有文件|*.*",
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
                StatusText.Text = $"读取视频信息：{item.DisplayName}";
                var info = await _ffmpeg.GetMediaInfoAsync(path);
                item.Duration = info.Duration;
                item.Width = info.Width;
                item.Height = info.Height;
                item.Fps = info.Fps;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"无法读取视频：{item.DisplayName}\n{ex.Message}", "ClipToGif",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            _videos.Add(item);
            added++;
        }

        Persist();
        StatusText.Text = added > 0 ? $"已链接 {added} 个视频路径" : "没有新的视频可导入";
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
            $"移除视频「{video.DisplayName}」？\n不会删除已生成的 GIF 文件。",
            "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            StatusText.Text = "就绪";
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
        GifCountText.Text = $"{video.Gifs.Count} 条";

        if (video.IsMissing)
        {
            Player.ShowMissing(video.FilePath);
            StatusText.Text = "视频文件已不存在";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            SetWorkspaceEnabled(enabled: false, videoMissing: true);
            return;
        }

        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        StatusText.Text = $"已链接：{video.DisplayName}";
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
        RangeSlider.IsEnabled = enabled;
        ExportPanel.IsEnabled = enabled;
        GenerateButton.IsEnabled = enabled;
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
        if (video.Width > 0)
            WidthBox.Text = video.Width.ToString(CultureInfo.InvariantCulture);
        if (video.Height > 0)
            HeightBox.Text = video.Height.ToString(CultureInfo.InvariantCulture);
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
    /// 将轨道映射到整段视频；默认选区不超过 30 秒。
    /// </summary>
    private void ApplyFullDuration(TimeSpan duration)
    {
        var totalSeconds = duration.TotalSeconds;
        var defaultEnd = Math.Min(30, totalSeconds);
        _suppressRangeEvents = true;
        RangeSlider.Minimum = 0;
        RangeSlider.Maximum = totalSeconds;
        RangeSlider.Start = 0;
        RangeSlider.End = defaultEnd;
        RangeSlider.Position = 0;
        _suppressRangeEvents = false;

        DurationStartLabel.Text = "00:00";
        DurationEndLabel.Text = $"总时长 {FormatTime(totalSeconds)}";
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
        StartLabel.Text = $"起点 {FormatTime(RangeSlider.Start)}";
        EndLabel.Text = $"终点 {FormatTime(RangeSlider.End)}";
        var span = RangeSlider.End - RangeSlider.Start;
        var spanText = span >= 60 ? FormatTime(span) : $"{span:0.0}s";
        SelectionLabel.Text = span >= 30 - 0.05 ? $"选区 {spanText}（最长 30s）" : $"选区 {spanText}";
    }

    private void UpdatePositionLabels(double seconds) =>
        PositionLabel.Text = $"当前位置 {FormatTime(seconds)}";

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
        HeightBox.IsEnabled = KeepAspectCheck.IsChecked != true;
        if (_currentVideo?.Height > 0)
            HeightBox.Text = _currentVideo.Height.ToString(CultureInfo.InvariantCulture);
    }

    private async void GenerateGif_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null)
        {
            MessageBox.Show(this, "请先选择视频。", "ClipToGif", MessageBoxButton.OK, MessageBoxImage.Information);
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
                "未找到 ffmpeg.exe。\n\n请任选其一：\n1) 安装 FFmpeg 并加入 PATH\n2) 将 ffmpeg.exe 放到 tools\\ffmpeg.exe\n3) 设置环境变量 FFMPEG_PATH",
                "缺少 FFmpeg", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshFfmpegStatus();
            return;
        }

        if (!TryReadSettings(out var settings, out var error))
        {
            MessageBox.Show(this, error, "参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var start = TimeSpan.FromSeconds(RangeSlider.Start);
        var end = TimeSpan.FromSeconds(RangeSlider.End);
        if (end - start < TimeSpan.FromMilliseconds(200))
        {
            MessageBox.Show(this, "选取区间太短，请至少选择 0.2 秒。", "ClipToGif",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (end - start > TimeSpan.FromSeconds(30))
        {
            MessageBox.Show(this, "GIF 选区最长 30 秒，请缩短绿色区间后再生成。", "ClipToGif",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(_currentVideo.DisplayName));
        var fileName = $"{safeName}_{stamp}.gif";
        var outputDir = Path.Combine(_store.OutputDirectory, _currentVideo.Id.ToString("N"));
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, fileName);

        var pending = new GifItem
        {
            VideoId = _currentVideo.Id,
            FilePath = outputPath,
            DisplayName = fileName,
            Start = start,
            End = end,
            Width = settings.Width,
            Height = settings.Height,
            Fps = settings.Fps,
            Quality = settings.Quality,
            Status = "生成中"
        };
        _currentVideo.Gifs.Insert(0, pending);
        GifCountText.Text = $"{_currentVideo.Gifs.Count} 条";
        GifList.SelectedItem = pending;

        _convertCts?.Cancel();
        _convertCts = new CancellationTokenSource();
        GenerateButton.IsEnabled = false;
        ProgressBar.Value = 0;
        StatusText.Text = "正在生成 GIF…";

        var progress = new Progress<double>(p =>
        {
            ProgressBar.Value = p;
            StatusText.Text = $"正在生成 GIF… {p:P0}";
        });

        try
        {
            await _ffmpeg.ConvertAsync(new GifConversionRequest
            {
                VideoPath = _currentVideo.FilePath,
                OutputPath = outputPath,
                Start = start,
                End = end,
                Settings = settings,
                Progress = progress,
                CancellationToken = _convertCts.Token
            });

            pending.FileSizeBytes = new FileInfo(outputPath).Length;
            pending.Status = "完成";
            pending.RefreshThumbnail();
            StatusText.Text = $"生成完成：{fileName}";
            ProgressBar.Value = 1;
            Persist();
            GifList.Items.Refresh();
        }
        catch (OperationCanceledException)
        {
            pending.Status = "已取消";
            StatusText.Text = "已取消生成";
        }
        catch (Exception ex)
        {
            pending.Status = "失败";
            StatusText.Text = "生成失败";
            MessageBox.Show(this, ex.Message, "生成失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private bool TryReadSettings(out GifExportSettings settings, out string error)
    {
        settings = new GifExportSettings();
        error = string.Empty;

        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 16)
        {
            error = "宽度请输入 ≥ 16 的整数。";
            return false;
        }

        if (!int.TryParse(HeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height < 0)
        {
            error = "高度请输入 ≥ 0 的整数（0 表示按比例）。";
            return false;
        }

        if (!double.TryParse(FpsBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps is < 1 or > 60)
        {
            error = "帧率请输入 1–60 之间的数字。";
            return false;
        }

        settings.Width = width;
        settings.Height = KeepAspectCheck.IsChecked == true ? 0 : height;
        settings.Fps = fps;
        settings.Quality = (int)QualitySlider.Value;
        settings.KeepAspectRatio = KeepAspectCheck.IsChecked == true || height <= 0;
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
            MessageBox.Show(this, "请先选择有效的 GIF。", "ClipToGif", MessageBoxButton.OK, MessageBoxImage.Information);
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

        var confirm = MessageBox.Show(this, $"删除「{gif.DisplayName}」？", "确认删除",
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
            MessageBox.Show(this, $"删除文件失败：{ex.Message}", "ClipToGif",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 仅在文件删除成功后从列表移除
        _currentVideo.Gifs.Remove(gif);
        GifCountText.Text = $"{_currentVideo.Gifs.Count} 条";
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

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "clip" : name;
    }
}
