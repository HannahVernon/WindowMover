using System.Windows;
using WindowMover.Core.Models;
using WindowMover.Core.Services;

namespace WindowMover;

public partial class ProfilesDialog : Window
{
    private readonly ProfileManager _profileManager;
    private string? _activeFingerprint;

    public bool ProfilesChanged { get; private set; }
    public string? ActivatedFingerprint { get; private set; }

    public ProfilesDialog(ProfileManager profileManager, string? activeFingerprint)
    {
        InitializeComponent();
        _profileManager = profileManager;
        _activeFingerprint = activeFingerprint;
        RefreshList();
    }

    private void RefreshList()
    {
        var items = _profileManager.Profiles.Values
            .OrderBy(p => p.Name)
            .Select(p => new ProfileListItem
            {
                Fingerprint = p.SetupFingerprint,
                Name = p.Name,
                MonitorCount = p.Setup.Monitors.Count,
                RuleCount = p.Rules.Count,
                IsActive = p.SetupFingerprint == _activeFingerprint,
                ActiveIndicator = p.SetupFingerprint == _activeFingerprint ? "\u2713" : "",
                LastModified = p.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            })
            .ToList();

        ProfileList.ItemsSource = items;
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => RenameSelected();

    private void ProfileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => RenameSelected();

    private void RenameSelected()
    {
        if (ProfileList.SelectedItem is not ProfileListItem item) return;

        var dialog = new RenameDialog(item.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            _profileManager.RenameProfile(item.Fingerprint, dialog.NewName.Trim());
            ProfilesChanged = true;
            RefreshList();
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileListItem item) return;
        if (item.IsActive) return;

        _activeFingerprint = item.Fingerprint;
        ActivatedFingerprint = item.Fingerprint;
        ProfilesChanged = true;
        RefreshList();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileListItem item) return;

        var activeWarning = item.IsActive ? " This is the currently active profile." : "";
        var result = MessageBox.Show(
            $"Delete profile \"{item.Name}\"?{activeWarning}\n\nThis cannot be undone.",
            "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _profileManager.DeleteProfile(item.Fingerprint);
            ProfilesChanged = true;
            RefreshList();
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var nameDialog = new RenameDialog("New Profile") { Owner = this, Title = "New Profile" };
        if (nameDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDialog.NewName))
            return;

        var name = nameDialog.NewName.Trim();

        // Detect current monitors
        var identifier = new MonitorIdentifier();
        var monitors = identifier.GetConnectedMonitors();
        var isRemote = SessionDetector.IsRemoteSession();
        var setup = MonitorSetup.FromMonitors(monitors, isRemote);
        setup.Name = name;

        // Generate a unique fingerprint so we don't overwrite existing profiles
        // for the same monitor configuration
        var baseFingerprint = setup.Fingerprint;
        var fingerprint = baseFingerprint;
        int suffix = 2;
        while (_profileManager.Profiles.ContainsKey(fingerprint))
        {
            fingerprint = baseFingerprint + $"-{suffix}";
            suffix++;
        }
        setup.Fingerprint = fingerprint;

        _profileManager.SaveProfile(setup, new List<WindowRule>());
        _profileManager.RenameProfile(fingerprint, name);

        ProfilesChanged = true;
        RefreshList();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal class ProfileListItem
{
    public string Fingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public int MonitorCount { get; set; }
    public int RuleCount { get; set; }
    public bool IsActive { get; set; }
    public string ActiveIndicator { get; set; } = "";
    public string LastModified { get; set; } = "";
}
