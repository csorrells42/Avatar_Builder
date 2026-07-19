using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AvatarBuilder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += AppDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;
        base.OnStartup(e);
        MainWindow = new MainWindow(AvatarBuilderStartupOptions.Parse(e.Args));
        MainWindow.Show();
    }

    private static void AppDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupLog(e.Exception);
        MessageBox.Show(
            $"Avatar Builder hit a startup error. Details were saved to:{Environment.NewLine}{GetLogPath()}",
            "Avatar Builder",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown();
    }

    private static void CurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteStartupLog(exception);
        }
    }

    private static void WriteStartupLog(Exception exception)
    {
        try
        {
            File.AppendAllText(
                GetLogPath(),
                $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string GetLogPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "AvatarBuilder-startup.log");
    }
}
