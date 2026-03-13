using WindowMover.Core.Models;
using WindowMover.Core.Services;

namespace WindowMover.ViewModels;

/// <summary>
/// ViewModel representing a draggable application in the layout editor.
/// </summary>
public class AppRuleViewModel : ViewModelBase
{
    private string _displayName = string.Empty;
    private string _processName = string.Empty;
    private string _windowTitle = string.Empty;
    private string? _executablePath;
    private int _windowCount;
    private uint _processId;
    private bool _isDragging;

    public AppRuleViewModel() { }

    public AppRuleViewModel(AppInfo appInfo)
    {
        DisplayName = appInfo.DisplayName;
        ProcessName = appInfo.ProcessName;
        ExecutablePath = appInfo.ExecutablePath;
        WindowCount = appInfo.WindowCount;
        ProcessId = appInfo.ProcessId;
    }

    public AppRuleViewModel(WindowRule rule)
    {
        DisplayName = rule.DisplayName;
        ProcessName = rule.ProcessName;
        WindowTitle = rule.WindowTitle ?? string.Empty;
        ExecutablePath = rule.ExecutablePath;
        ProcessId = rule.ProcessId;
    }

    public AppRuleViewModel(WindowInfo window)
    {
        DisplayName = window.DisplayName ?? window.ProcessName;
        ProcessName = window.ProcessName;
        WindowTitle = window.Title;
        ExecutablePath = window.ExecutablePath;
        ProcessId = window.ProcessId;
        WindowHandle = window.Handle;
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

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    /// <summary>
    /// Runtime-only window handle for correlating this card with a live window.
    /// Not serialized to profiles.
    /// </summary>
    public IntPtr WindowHandle { get; set; }

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

    public uint ProcessId
    {
        get => _processId;
        set => SetProperty(ref _processId, value);
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
        DisplayName = DisplayName,
        ProcessId = ProcessId,
        WindowTitle = string.IsNullOrEmpty(WindowTitle) ? null : WindowTitle
    };
}
