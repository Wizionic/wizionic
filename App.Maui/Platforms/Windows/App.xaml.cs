using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace App.Maui.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class WinUIApp : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public WinUIApp()
	{
		this.InitializeComponent();
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			if (e.ExceptionObject is Exception ex)
				TryWriteStartupCrash(ex);
		};
		TaskScheduler.UnobservedTaskException += (_, e) => TryWriteStartupCrash(e.Exception);
	}

	protected override MauiApp CreateMauiApp()
	{
		try
		{
			return MauiProgram.CreateMauiApp();
		}
		catch (Exception ex)
		{
			TryWriteStartupCrash(ex);
			throw;
		}
	}

	private static void TryWriteStartupCrash(Exception ex)
	{
		try
		{
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Wizionic", "userdata");
			Directory.CreateDirectory(dir);
			File.AppendAllText(
				Path.Combine(dir, "startup-crash.log"),
				$"{DateTimeOffset.Now:u}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
		}
		catch
		{
			// ignore
		}
	}
}

