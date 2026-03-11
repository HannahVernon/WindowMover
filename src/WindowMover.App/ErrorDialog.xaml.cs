using System.Diagnostics;
using System.Windows;

namespace WindowMover.App;

public partial class ErrorDialog : Window
{
    public string Message { get; }

    private ErrorDialog(string message)
    {
        Message = message;
        DataContext = this;
        InitializeComponent();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowMover", "logs");
            var today = Path.Combine(logDir, $"WindowMover-{DateTime.Now:yyyy-MM-dd}.log");

            if (File.Exists(today))
                Process.Start("notepad.exe", today);
            else if (Directory.Exists(logDir))
                Process.Start("explorer.exe", logDir);
            else
                System.Windows.MessageBox.Show("No log files found yet.", "WindowMover");
        }
        catch { }
    }

    private void OK_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Shows the error dialog with a generic message and a "View Log" button.
    /// </summary>
    public static void Show(string context)
    {
        var dialog = new ErrorDialog(
            $"Something went wrong{(string.IsNullOrEmpty(context) ? "." : $": {context}")}\n\n" +
            "Details have been written to the log file.");
        dialog.ShowDialog();
    }
}
