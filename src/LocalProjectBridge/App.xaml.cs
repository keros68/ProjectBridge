using System.Windows;

namespace LocalProjectBridge;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(initiallyOwned: false, @"Local\ProjectBridge.SingleInstance");
        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }
        if (!_ownsInstanceMutex)
        {
            System.Windows.MessageBox.Show("ProjectBridge 已经在运行，请从任务栏通知区域打开现有窗口。",
                "ProjectBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var startHidden = e.Args.Any(argument => string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase));
        _window = new MainWindow(startHidden);
        _window.Show();
        if (startHidden) _window.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
