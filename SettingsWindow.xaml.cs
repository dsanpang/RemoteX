using System.Windows;


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
        HealthTimeoutBox.Text          = _settings.HealthCheckTimeoutMs.ToString();
        HealthConcurrencyBox.Text      = _settings.HealthCheckConcurrency.ToString();
        ConnectTimeoutBox.Text         = _settings.ConnectTimeoutMs.ToString();
        DefaultPortBox.Text            = _settings.DefaultPort.ToString();
        RdpAuthLevelBox.Text           = _settings.RdpAuthLevel.ToString();
        TerminalConnectTimeoutBox.Text = _settings.TerminalConnectTimeoutSec.ToString();
        MaxRecentBox.Text              = _settings.MaxRecentCount.ToString();

        // ── 云同步 ──────────────────────────────────────────────────────────
        var cs = _settings.CloudSync;
        SyncEnabledCheck.IsChecked  = cs.Enabled;
        SyncEndpointBox.Text        = cs.Endpoint;
        SyncRegionBox.Text          = cs.Region;
        SyncBucketBox.Text          = cs.Bucket;
        SyncAccessKeyBox.Text       = cs.AccessKey;
        SyncSecretKeyBox.Password   = cs.SecretKey;
        SyncObjectKeyBox.Text       = string.IsNullOrWhiteSpace(cs.ObjectKey)
                                          ? "remotex/sync.json"
                                          : cs.ObjectKey;
        SyncPathStyleCheck.IsChecked  = cs.UsePathStyle;
        SyncIgnoreSslCheck.IsChecked  = cs.IgnoreSslErrors;
        SyncPasswordBox.Password      = cs.SyncPassword;

        UpdateSyncLastTimeText(cs.LastSyncUtc);
        SyncConfigPanel.IsEnabled = cs.Enabled;

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

        if (!int.TryParse(TerminalConnectTimeoutBox.Text, out int terminalTimeout) ||
            terminalTimeout < 3 || terminalTimeout > 120)
        {
            ShowError("SSH/Telnet 超时必须在 3-120 秒之间");
            TerminalConnectTimeoutBox.Focus();
            return;
        }

        _settings.HealthCheckTimeoutMs      = healthTimeout;
        _settings.HealthCheckConcurrency    = concurrency;
        _settings.ConnectTimeoutMs          = connectTimeout;
        _settings.DefaultPort               = port;
        _settings.RdpAuthLevel              = rdpAuth;
        _settings.TerminalConnectTimeoutSec = terminalTimeout;
        _settings.MaxRecentCount            = maxRecent;

        // ── 保存云同步配置 ────────────────────────────────────────────────
        var cs = _settings.CloudSync;
        cs.Enabled      = SyncEnabledCheck.IsChecked == true;
        cs.Endpoint     = SyncEndpointBox.Text.Trim();
        cs.Region       = SyncRegionBox.Text.Trim();
        cs.Bucket       = SyncBucketBox.Text.Trim();
        cs.AccessKey    = SyncAccessKeyBox.Text.Trim();
        cs.SecretKey    = SyncSecretKeyBox.Password;
        cs.ObjectKey    = string.IsNullOrWhiteSpace(SyncObjectKeyBox.Text)
                              ? "remotex/sync.json"
                              : SyncObjectKeyBox.Text.Trim();
        cs.UsePathStyle    = SyncPathStyleCheck.IsChecked == true;
        cs.IgnoreSslErrors = SyncIgnoreSslCheck.IsChecked == true;
        cs.SyncPassword    = SyncPasswordBox.Password;

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

    // ── 云同步 ──────────────────────────────────────────────────────────────

    private void SyncEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (SyncConfigPanel != null)
            SyncConfigPanel.IsEnabled = SyncEnabledCheck.IsChecked == true;
    }

    private async void SyncTestBtn_Click(object sender, RoutedEventArgs e)
    {
        SyncStatusText.Text = "正在测试…";
        SyncTestBtn.IsEnabled = false;
        try
        {
            var cfg = BuildCurrentSyncConfig();
            var svc = new CloudSyncService(cfg);
            var (ok, msg) = await svc.TestConnectionAsync();
            SyncStatusText.Foreground = ok
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8));
            SyncStatusText.Text = msg;
        }
        catch (Exception ex)
        {
            SyncStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8));
            SyncStatusText.Text = ex.Message;
        }
        finally
        {
            SyncTestBtn.IsEnabled = true;
        }
    }

    /// <summary>从当前表单读取云同步配置（不保存到 _settings）。</summary>
    private CloudSyncConfig BuildCurrentSyncConfig() => new()
    {
        Enabled      = SyncEnabledCheck.IsChecked == true,
        Endpoint     = SyncEndpointBox.Text.Trim(),
        Region       = SyncRegionBox.Text.Trim(),
        Bucket       = SyncBucketBox.Text.Trim(),
        AccessKey    = SyncAccessKeyBox.Text.Trim(),
        SecretKey    = SyncSecretKeyBox.Password,
        ObjectKey    = string.IsNullOrWhiteSpace(SyncObjectKeyBox.Text)
                           ? "remotex/sync.json"
                           : SyncObjectKeyBox.Text.Trim(),
        UsePathStyle    = SyncPathStyleCheck.IsChecked == true,
        IgnoreSslErrors = SyncIgnoreSslCheck.IsChecked == true,
        SyncPassword    = SyncPasswordBox.Password,
        LastSyncUtc  = _settings.CloudSync.LastSyncUtc
    };

    private void UpdateSyncLastTimeText(string utcStr)
    {
        if (string.IsNullOrWhiteSpace(utcStr))
        {
            SyncLastTimeText.Text = "";
            return;
        }
        if (System.DateTime.TryParse(utcStr,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            var local = dt.ToLocalTime();
            SyncLastTimeText.Text = $"上次同步：{local:yyyy-MM-dd HH:mm:ss}";
        }
        else
        {
            SyncLastTimeText.Text = $"上次同步：{utcStr}";
        }
    }

    private void ShowError(string message)
        => AppMsg.Show(this, message, "输入错误", AppMsgIcon.Warning);
}
