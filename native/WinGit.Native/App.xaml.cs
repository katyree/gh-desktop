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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        CommandLineArguments = Environment.GetCommandLineArgs();
        var gitRuntime = await NativeGitRuntime.ValidateAsync();
        mainWindow = gitRuntime.IsValid
            ? new MainWindow(gitRuntime.GitExecutablePath)
            : new NativeGitRecoveryWindow(
                gitRuntime.GitExecutablePath,
                gitRuntime.FailureReason);
        mainWindow.Activate();
    }
}
