using System.Windows;

namespace WindowMover.App;

public partial class RenameDialog : Window
{
    public string NewName => NameBox.Text;

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.SelectAll();
            NameBox.Focus();
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
