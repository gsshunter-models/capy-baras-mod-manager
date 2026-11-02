using System.Windows;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace MXSimModManager.Desktop;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private bool reportingError;
    protected override void OnStartup(StartupEventArgs e)
    {
        var stateIndex = Array.IndexOf(e.Args, "--state");
        var identity = stateIndex >= 0 && stateIndex + 1 < e.Args.Length ? e.Args[stateIndex + 1] : Environment.UserName;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..20];
        instance = new Mutex(true, "Local\\MXSimModManager-" + key, out var created);
        if (!created) { MessageBox.Show(Brand.Name + " is already open.", Brand.Name); Shutdown(); return; }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (reportingError) { Shutdown(1); return; }
            reportingError = true;
            try { MessageBox.Show(args.Exception.Message, Brand.Name, MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { reportingError = false; }
        };
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
