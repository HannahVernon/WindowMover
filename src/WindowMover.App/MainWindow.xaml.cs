using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowMover.App.ViewModels;

namespace WindowMover.App;

/// <summary>
/// Main window: drag-and-drop layout editor for assigning apps to monitors.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        // Strip the +commithash suffix that .NET appends
        var plusIdx = version.IndexOf('+');
        if (plusIdx >= 0) version = version[..plusIdx];
        Title = $"WindowMover v{version} — Monitor Layout Manager";

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Initialize();
        }
        catch (Exception ex)
        {
            Core.Services.AppLogger.Instance.Error("Initialization failed", ex);
            ViewModel.StatusMessage = "Initialization error — check log for details";
            ErrorDialog.Show(ex.Message);
        }
    }

    /// <summary>
    /// Minimize to tray when the user minimizes the window.
    /// </summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    /// <summary>
    /// Called by App.ExitApplication() to allow the window to truly close.
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    private void Profiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfilesDialog(ViewModel.ProfileManager, ViewModel.ActiveFingerprint)
        {
            Owner = this
        };
        dialog.ShowDialog();

        if (dialog.ProfilesChanged)
        {
            // If the active profile was renamed, refresh the setup name
            var active = ViewModel.ProfileManager.GetProfile(ViewModel.ActiveFingerprint ?? "");
            if (active != null)
                ViewModel.CurrentSetupName = active.Name;

            ViewModel.StatusMessage = "Profiles updated";
        }
    }

    // ===== Drag-and-drop handling =====

    private void AppCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (sender is FrameworkElement element && element.Tag is AppRuleViewModel app)
        {
            var data = new DataObject("AppRule", app);

            // Find source container info
            var sourceMonitor = FindParentMonitorViewModel(element);
            if (sourceMonitor != null)
                data.SetData("SourceMonitor", sourceMonitor);
            else
                data.SetData("SourceUnassigned", true);

            DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
        }
    }

    private void MonitorPanel_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("AppRule"))
        {
            e.Effects = DragDropEffects.Move;
            if (sender is Border border)
                border.Background = (SolidColorBrush)FindResource("DragOverBrush");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void MonitorPanel_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
            border.Background = (SolidColorBrush)FindResource("SurfaceBrush");
    }

    private void MonitorPanel_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border border)
            return;

        border.Background = (SolidColorBrush)FindResource("SurfaceBrush");

        if (e.Data.GetData("AppRule") is not AppRuleViewModel app)
            return;

        if (border.Tag is not MonitorViewModel targetMonitor)
            return;

        MonitorViewModel? sourceMonitor = e.Data.GetData("SourceMonitor") as MonitorViewModel;
        ViewModel.MoveApp(app, sourceMonitor, targetMonitor);

        e.Handled = true;
    }

    private void UnassignedPanel_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("AppRule"))
        {
            e.Effects = DragDropEffects.Move;
            if (sender is Border border)
                border.Background = (SolidColorBrush)FindResource("DragOverBrush");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void UnassignedPanel_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
            border.Background = (SolidColorBrush)FindResource("SurfaceBrush");
    }

    private void UnassignedPanel_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
            border.Background = (SolidColorBrush)FindResource("SurfaceBrush");

        if (e.Data.GetData("AppRule") is not AppRuleViewModel app)
            return;

        MonitorViewModel? sourceMonitor = e.Data.GetData("SourceMonitor") as MonitorViewModel;
        if (sourceMonitor != null)
        {
            ViewModel.MoveApp(app, sourceMonitor, null);
        }

        e.Handled = true;
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is AppRuleViewModel app)
        {
            // Find which monitor this app is assigned to
            foreach (var monitor in ViewModel.Monitors)
            {
                if (monitor.AssignedApps.Contains(app))
                {
                    ViewModel.UnassignApp(app, monitor);
                    break;
                }
            }
        }
    }

    private MonitorViewModel? FindParentMonitorViewModel(DependencyObject child)
    {
        var current = child;
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.Tag is MonitorViewModel monitorVm)
                return monitorVm;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // Minimize to tray instead of closing (unless force-closing)
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}