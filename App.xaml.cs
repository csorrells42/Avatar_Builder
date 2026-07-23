using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AvatarBuilder;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.DispatcherUnhandledException += AppDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;
		base.OnStartup(e);
		base.MainWindow = new MainWindow(AvatarBuilderStartupOptions.Parse(e.Args));
		base.MainWindow.Show();
	}

	private static void AppDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		WriteStartupLog(e.Exception);
		MessageBox.Show("Avatar Builder hit a startup error. Details were saved to:" + Environment.NewLine + GetLogPath(), "Avatar Builder", MessageBoxButton.OK, MessageBoxImage.Hand);
		e.Handled = true;
		Application.Current.Shutdown();
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
			File.AppendAllText(GetLogPath(), $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
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
