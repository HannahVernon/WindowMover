namespace WindowMover.Core.Models;

/// <summary>
/// Application-level settings persisted across sessions.
/// Stored as settings.json in %APPDATA%\WindowMover\.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// When true, changes are saved automatically without requiring
    /// the user to click the Save button.
    /// </summary>
    public bool AutoSave { get; set; }
}
