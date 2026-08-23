using System.IO;
using System.Windows;
using System.Windows.Threading;
using Unosquare.FFME;

namespace ClipToGif;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;

        try
        {
            Library.FFmpegDirectory = ResolveFfmpegDirectory();
            Library.LoadFFmpeg();
        }
        catch (Exception ex)
        {
            ShowFatal($"无法加载 FFmpeg：{ex.Message}");
            Shutdown(-1);
        }
    }

    private static string ResolveFfmpegDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ffmpeg")),
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "avcodec-61.dll")))
                return dir;
        }

        return candidates[0];
    }

    private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception.GetBaseException().Message);
        e.Handled = true;
        Shutdown(-1);
    }

    private static void CurrentDomain_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var message = e.ExceptionObject is Exception ex ? ex.GetBaseException().Message : e.ExceptionObject?.ToString();
        ShowFatal(message ?? "发生未知错误。");
    }

    private static void ShowFatal(string message)
    {
        MessageBox.Show(message, "ClipToGif 无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
