using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipToGif.Localization;
using Unosquare.FFME;

namespace ClipToGif;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Loc.Initialize();

        try
        {
            Library.FFmpegDirectory = ResolveFfmpegDirectory();
            Library.LoadFFmpeg();
        }
        catch (Exception ex)
        {
            ShowFatal(Loc.Format("CannotLoadFfmpeg", ex.Message));
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
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
        ShowFatal(message ?? Loc.Get("UnknownError"));
    }

    private static void ShowFatal(string message)
    {
        MessageBox.Show(message, Loc.Get("StartupFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
