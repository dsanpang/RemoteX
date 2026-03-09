using System.Windows;
using System.Windows.Media;

namespace RemoteX;

public partial class CloseActionDialog : Window
{
    public bool MinimizeToTray { get; private set; }
    public bool RememberChoice => RememberCheck.IsChecked == true;

    public CloseActionDialog()
    {
        InitializeComponent();
    }

    private void TrayOption_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MinimizeToTray = true;
        DialogResult   = true;
        Close();
    }

    private void ExitOption_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MinimizeToTray = false;
        DialogResult   = true;
        Close();
    }
}
