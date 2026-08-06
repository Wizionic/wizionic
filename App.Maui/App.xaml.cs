namespace App.Maui;

public partial class MauiShell : Application
{
	public MauiShell()
	{
#if WINDOWS
		var userDataFolder = Path.Combine(FileSystem.AppDataDirectory, "WebView2");
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
#endif
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage()) { Title = "Wizionic" };
	}
}
