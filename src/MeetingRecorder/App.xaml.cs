using System.IO;
using System.Windows;
using System.Windows.Threading;
using MeetingRecorder.Services;
using MeetingRecorder.Views;

namespace MeetingRecorder;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Screens replace one another, and a hidden toast window must not be
        // able to hold the process open, so shutdown is explicit — see
        // ShellWindow.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // The two folders are created independently on purpose. They used to
        // be one call, and a save location pointed somewhere unreachable took
        // Documents\VoxMark\ down with it — leaving plans.json and
        // presets.json with nowhere to be written. Neither failure blocks
        // startup; both are noted for the Settings screen's Log.
        try
        {
            AppPaths.EnsureRoot();
        }
        catch (Exception ex)
        {
            AppPaths.Note("Could not create the app folder \"" + AppPaths.Root + "\".\n" +
                          ex.Message + "\n" + AppPaths.OneDriveHint(AppPaths.Root));
        }

        try
        {
            AppPaths.CreateDirectory(AppPaths.SessionsRoot);
        }
        catch (Exception ex)
        {
            // Deliberately not cleared: an external drive that is merely
            // unplugged today should still be the configured location
            // tomorrow. Settings shows this note next to the Reset button so
            // the operator decides.
            AppPaths.Note("The saved recording location \"" + AppPaths.SessionsRoot +
                          "\" could not be created.\n" + ex.Message + "\n" +
                          AppPaths.OneDriveHint(AppPaths.SessionsRoot) +
                          "\nUse Settings → Save recordings to → Reset to go back to " +
                          "Documents\\VoxMark\\Sessions.");
        }

        new LibraryWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(
            "Something went wrong and VoxMark had to stop.\n\nA log was written next to the app.\n\n" +
            e.Exception.Message,
            "VoxMark", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Environment.Exit(1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) Log(exception);
    }

    private static void Log(Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(directory)) return;

            var text = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                       exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace + "\n";
            if (exception.InnerException is { } inner)
            {
                text += "Inner: " + inner.Message + "\n" + inner.StackTrace + "\n";
            }

            File.AppendAllText(Path.Combine(directory, "voxmark-crash.log"), text + "\n");
        }
        catch (Exception)
        {
            // Nothing useful to do if even the log cannot be written.
        }
    }
}
