using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using WindowMover.App.ViewModels;

namespace WindowMover.App;

/// <summary>
/// Application entry point: single-instance enforcement, system tray, startup.
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handling to prevent silent crashes
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "WindowMover Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Fatal error:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "WindowMover Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        // Single-instance check
        _singleInstanceMutex = new Mutex(true, "WindowMover_SingleInstance_{B7A3E2F1-4C8D-4A9B-BE6F-12345ABCDEF0}", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "WindowMover is already running. Check the system tray.",
                "WindowMover", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        SetupTrayIcon();
        ShowMainWindow();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "WindowMover",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _trayIcon.ContextMenuStrip.Items.Add("Open Settings", null, (_, _) => ShowMainWindow());
        _trayIcon.ContextMenuStrip.Items.Add("Apply Rules Now", null, (_, _) => ApplyRulesNow());
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow();
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ApplyRulesNow()
    {
        if (_mainWindow?.DataContext is MainViewModel vm)
        {
            vm.ApplyNowCommand.Execute(null);
            _trayIcon?.ShowBalloonTip(2000, "WindowMover", "Rules applied", ToolTipIcon.Info);
        }
    }

    private void ExitApplication()
    {
        if (_mainWindow?.DataContext is MainViewModel vm)
        {
            vm.Dispose();
        }

        // Allow the window to actually close
        if (_mainWindow != null)
        {
            _mainWindow.ForceClose();
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        Shutdown();
    }
}
