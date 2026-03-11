using System.Collections.ObjectModel;
using WindowMover.Core.Models;

namespace WindowMover.ViewModels;

/// <summary>
/// ViewModel representing a single monitor column in the drag-and-drop UI.
/// </summary>
public class MonitorViewModel : ViewModelBase
{
    private string _displayName = string.Empty;
    private string _resolution = string.Empty;
    private bool _isBuiltIn;
    private bool _isDragOver;

    public MonitorViewModel(MonitorInfo monitor)
    {
        MonitorInfo = monitor;
        DeviceId = monitor.DeviceId;
        DisplayName = monitor.FriendlyName;
        Resolution = $"{monitor.CurrentWidth} × {monitor.CurrentHeight}";
        IsBuiltIn = monitor.IsBuiltIn;
        AssignedApps = [];
    }

    public MonitorInfo MonitorInfo { get; }
    public string DeviceId { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string Resolution
    {
        get => _resolution;
        set => SetProperty(ref _resolution, value);
    }

    public bool IsBuiltIn
    {
        get => _isBuiltIn;
        set => SetProperty(ref _isBuiltIn, value);
    }

    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
    }

    /// <summary>
    /// Apps assigned to this monitor via drag-and-drop.
    /// </summary>
    public ObservableCollection<AppRuleViewModel> AssignedApps { get; }
}
