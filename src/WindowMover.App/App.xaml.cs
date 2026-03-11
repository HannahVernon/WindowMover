using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using WindowMover.App.ViewModels;
using WindowMover.Core.Services;

namespace WindowMover.App;

/// <summary>
/// Application entry point: single-instance enforcement, system tray, startup.
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private static readonly AppLogger Log = AppLogger.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Info("WindowMover starting");

        // Global exception handling — log and show to user
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled dispatcher exception", args.Exception);
            ErrorDialog.Show(args.Exception.Message);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Error("Fatal unhandled exception", ex);
                ErrorDialog.Show(ex.Message);
            }
        };

        // Single-instance check
        _singleInstanceMutex = new Mutex(true, "WindowMover_SingleInstance_{B7A3E2F1-4C8D-4A9B-BE6F-12345ABCDEF0}", out bool createdNew);
        if (!createdNew)
        {
            Log.Warn("Another instance is already running — exiting");
            System.Windows.MessageBox.Show(
                "WindowMover is already running. Check the system tray.",
                "WindowMover", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        SetupTrayIcon();
        ShowMainWindow();
        Log.Info("WindowMover started successfully");
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "WindowMover",
            Icon = LoadAppIcon(),
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
            Log.Info("Creating new main window");
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
        Log.Info("WindowMover shutting down");

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

        Log.Info("WindowMover exited");
        Log.Dispose();

        Shutdown();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) return icon;
            }
        }
        catch { }

        return SystemIcons.Application;
    }
}
