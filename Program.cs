using MagniReader.Infrastructure;
using Velopack;

namespace MagniReader;

/// <summary>
/// Custom entry point. Velopack must run before anything else: when the installer,
/// an update or the uninstaller launches the app with one of its hook arguments,
/// <see cref="VelopackApp.Run"/> handles it and exits the process.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            // Don't leave a "Launch at startup" entry pointing at a deleted exe.
            .OnBeforeUninstallFastCallback(_ => StartupManager.Disable())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
