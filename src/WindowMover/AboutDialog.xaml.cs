using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace WindowMover;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        var plusIdx = version.IndexOf('+');
        if (plusIdx >= 0) version = version[..plusIdx];

        VersionText.Text = $"Version {version}";
        CopyrightText.Text = $"\u00a9 {DateTime.Now.Year} Hannah Vernon. All rights reserved.";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OK_Click(object sender, RoutedEventArgs e) => Close();
}
