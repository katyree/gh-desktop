using Microsoft.UI.Xaml;

namespace WinGit.Native;

public partial class App : Application
{
    private Window? mainWindow;

    public static string[] CommandLineArguments { get; private set; } = [];

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CommandLineArguments = Environment.GetCommandLineArgs();
        mainWindow = new MainWindow();
        mainWindow.Activate();
    }
}
