using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MeetingRecorder;

public partial class App : Application
{
    public App()
    {
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        MessageBox.Show($"예기치 않은 오류가 발생했습니다.\n로그 파일이 저장되었습니다.\n\n{e.Exception.Message}", "오류 발생", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Environment.Exit(1);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException(ex);
            MessageBox.Show($"치명적인 오류가 발생했습니다.\n로그 파일이 저장되었습니다.\n\n{ex.Message}", "오류 발생", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LogException(Exception ex)
    {
        try
        {
            var exePath = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(exePath)) return;
            
            var logPath = Path.Combine(exePath, "crash_log.txt");
            var content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n";
            
            if (ex.InnerException != null)
            {
                content += $"Inner Exception: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n";
            }
            content += "\n";

            File.AppendAllText(logPath, content);
        }
        catch 
        { 
            // 로그 작성 중 오류 발생 시 무시
        }
    }
}
