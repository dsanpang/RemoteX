using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using MessageBox = System.Windows.MessageBox;

namespace RemoteX;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private List<SocksProxyEntry> _socksList = new();

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

        _socksList = _settings.SocksProxies.Select(p => new SocksProxyEntry
        {
            Name = p.Name,
            Host = p.Host,
            Port = p.Port,
            Username = p.Username,
            Password = p.Password
        }).ToList();
        SocksProxyListBox.ItemsSource = _socksList;
        SocksProxyListBox.SelectedIndex = -1;
        SocksFormToState(false);

        HealthTimeoutBox.Focus();
    }

    private void SocksProxyListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SocksProxyListBox.SelectedItem is SocksProxyEntry p)
        {
            SocksNameBox.Text = p.Name;
            SocksHostBox.Text = p.Host;
            SocksPortBox.Text = p.Port.ToString();
            SocksUsernameBox.Text = p.Username;
            SocksPasswordBox.Password = p.Password;
        }
        else
            SocksFormToState(false);
    }

    private void SocksFormToState(bool toModel)
    {
        if (toModel && SocksProxyListBox.SelectedItem is SocksProxyEntry p)
        {
            p.Name = SocksNameBox.Text?.Trim() ?? "";
            p.Host = SocksHostBox.Text?.Trim() ?? "";
            p.Port = int.TryParse(SocksPortBox.Text, out var port) && port >= 1 && port <= 65535 ? port : 1080;
            p.Username = SocksUsernameBox.Text?.Trim() ?? "";
            p.Password = SocksPasswordBox.Password ?? "";
        }
        else
        {
            SocksNameBox.Text = "";
            SocksHostBox.Text = "";
            SocksPortBox.Text = "1080";
            SocksUsernameBox.Text = "";
            SocksPasswordBox.Password = "";
        }
    }

    private void SocksAdd_Click(object sender, RoutedEventArgs e)
    {
        var entry = new SocksProxyEntry { Name = "新机房", Port = 1080 };
        _socksList.Add(entry);
        SocksProxyListBox.Items.Refresh();
        SocksProxyListBox.SelectedItem = entry;
    }

    private void SocksSave_Click(object sender, RoutedEventArgs e)
    {
        if (SocksProxyListBox.SelectedItem is not SocksProxyEntry) return;
        SocksFormToState(true);
        SocksProxyListBox.Items.Refresh();
    }

    private void SocksDel_Click(object sender, RoutedEventArgs e)
    {
        if (SocksProxyListBox.SelectedItem is not SocksProxyEntry p) return;
        _socksList.Remove(p);
        SocksProxyListBox.Items.Refresh();
        SocksProxyListBox.SelectedIndex = _socksList.Count > 0 ? 0 : -1;
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

        SocksFormToState(true);
        foreach (var p in _socksList)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                ShowError("SOCKS 代理名称不能为空");
                return;
            }
            if (string.IsNullOrWhiteSpace(p.Host))
            {
                ShowError($"SOCKS 代理「{p.Name}」的主机不能为空");
                return;
            }
            if (p.Port < 1 || p.Port > 65535)
            {
                ShowError($"SOCKS 代理「{p.Name}」的端口必须在 1-65535 之间");
                return;
            }
        }

        _settings.HealthCheckTimeoutMs   = healthTimeout;
        _settings.HealthCheckConcurrency = concurrency;
        _settings.ConnectTimeoutMs       = connectTimeout;
        _settings.DefaultPort            = port;
        _settings.RdpAuthLevel          = rdpAuth;
        _settings.MaxRecentCount         = maxRecent;
        _settings.SocksProxies           = _socksList.Select(x => new SocksProxyEntry
        {
            Name = x.Name,
            Host = x.Host,
            Port = x.Port,
            Username = x.Username,
            Password = x.Password
        }).ToList();
        _settings.Save();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
        => MessageBox.Show(message, "输入错误",
               MessageBoxButton.OK, MessageBoxImage.Warning);
}
