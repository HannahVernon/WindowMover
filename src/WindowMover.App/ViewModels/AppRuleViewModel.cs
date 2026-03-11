using WindowMover.Core.Models;
using WindowMover.Core.Services;

namespace WindowMover.App.ViewModels;

/// <summary>
/// ViewModel representing a draggable application in the layout editor.
/// </summary>
public class AppRuleViewModel : ViewModelBase
{
    private string _displayName = string.Empty;
    private string _processName = string.Empty;
    private string? _executablePath;
    private int _windowCount;
    private bool _isDragging;

    public AppRuleViewModel() { }

    public AppRuleViewModel(AppInfo appInfo)
    {
        DisplayName = appInfo.DisplayName;
        ProcessName = appInfo.ProcessName;
        ExecutablePath = appInfo.ExecutablePath;
        WindowCount = appInfo.WindowCount;
    }

    public AppRuleViewModel(WindowRule rule)
    {
        DisplayName = rule.DisplayName;
        ProcessName = rule.ProcessName;
        ExecutablePath = rule.ExecutablePath;
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string ProcessName
    {
        get => _processName;
        set => SetProperty(ref _processName, value);
    }

    public string? ExecutablePath
    {
        get => _executablePath;
        set => SetProperty(ref _executablePath, value);
    }

    public int WindowCount
    {
        get => _windowCount;
        set => SetProperty(ref _windowCount, value);
    }

    public bool IsDragging
    {
        get => _isDragging;
        set => SetProperty(ref _isDragging, value);
    }

    /// <summary>
    /// Creates a WindowRule from this ViewModel for the given target monitor.
    /// </summary>
    public WindowRule ToRule(string targetMonitorId) => new()
    {
        ProcessName = ProcessName,
        ExecutablePath = ExecutablePath,
        TargetMonitorId = targetMonitorId,
        DisplayName = DisplayName
    };
}
