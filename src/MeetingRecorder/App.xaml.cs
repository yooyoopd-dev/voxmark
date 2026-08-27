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

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception)
        {
            // A missing Documents folder is reported by the first save
            // instead of blocking startup.
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
