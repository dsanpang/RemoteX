using System.Windows;

using MessageBox = System.Windows.MessageBox;

namespace RemoteX;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        HealthTimeoutBox.Text     = _settings.HealthCheckTimeoutMs.ToString();
        HealthConcurrencyBox.Text = _settings.HealthCheckConcurrency.ToString();
        ConnectTimeoutBox.Text    = _settings.ConnectTimeoutMs.ToString();
        DefaultPortBox.Text       = _settings.DefaultPort.ToString();
        RdpAuthLevelBox.Text      = _settings.RdpAuthLevel.ToString();
        MaxRecentBox.Text         = _settings.MaxRecentCount.ToString();

        HealthTimeoutBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HealthTimeoutBox.Text, out int healthTimeout) || healthTimeout < 100)
        {
            ShowError("检测超时必须 >= 100 毫秒，请重新输入");
            HealthTimeoutBox.Focus();
            return;
        }

        if (!int.TryParse(HealthConcurrencyBox.Text, out int concurrency) || concurrency < 1 || concurrency > 64)
        {
            ShowError("并发检测数必须在 1-64 之间");
            HealthConcurrencyBox.Focus();
            return;
        }

        if (!int.TryParse(ConnectTimeoutBox.Text, out int connectTimeout) || connectTimeout < 1000)
        {
            ShowError("连接超时必须 >= 1000 毫秒，请重新输入");
            ConnectTimeoutBox.Focus();
            return;
        }

        if (!int.TryParse(DefaultPortBox.Text, out int port) || port < 1 || port > 65535)
        {
            ShowError("默认端口必须在 1-65535 之间");
            DefaultPortBox.Focus();
            return;
        }

        if (!int.TryParse(RdpAuthLevelBox.Text, out int rdpAuth) || rdpAuth < 0 || rdpAuth > 2)
        {
            ShowError("RDP 证书验证必须为 0 或 2");
            RdpAuthLevelBox.Focus();
            return;
        }

        if (!int.TryParse(MaxRecentBox.Text, out int maxRecent) || maxRecent < 1 || maxRecent > 50)
        {
            ShowError("最近连接记录数必须在 1-50 之间");
            MaxRecentBox.Focus();
            return;
        }

        _settings.HealthCheckTimeoutMs   = healthTimeout;
        _settings.HealthCheckConcurrency = concurrency;
        _settings.ConnectTimeoutMs       = connectTimeout;
        _settings.DefaultPort            = port;
        _settings.RdpAuthLevel          = rdpAuth;
        _settings.MaxRecentCount         = maxRecent;
        _settings.Save();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Input_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb) tb.SelectAll();
    }

    private void ShowError(string message)
        => MessageBox.Show(message, "输入错误",
               MessageBoxButton.OK, MessageBoxImage.Warning);
}
