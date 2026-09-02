using System.Windows;
using System.Windows.Threading;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var startupSettings = new GfMusicManagerSettingsStore().Load();
        UiText.SetLanguage(startupSettings.Language);
        GfMusicManagerLog.SetFileLoggingEnabled(startupSettings.EnableFileLogging);
        GfMusicManagerLog.Info($"Application startup. Arguments: {string.Join(" | ", e.Args)}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("Application startup failed", exception);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GfMusicManagerLog.Info($"Application exit. Code: {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        GfMusicManagerLog.Exception("Dispatcher unhandled exception", e.Exception);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            GfMusicManagerLog.Exception("AppDomain unhandled exception", exception);
        }
        else
        {
            GfMusicManagerLog.Error($"AppDomain unhandled exception: {e.ExceptionObject}");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        GfMusicManagerLog.Exception("Unobserved task exception", e.Exception);
    }
}
