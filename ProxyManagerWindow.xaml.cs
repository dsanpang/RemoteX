using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;



namespace RemoteX;

public partial class ProxyManagerWindow : Window
{
    private readonly AppSettings _settings;
    private List<SocksProxyEntry> _list = new();

    public ProxyManagerWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _list = _settings.SocksProxies.Select(p => new SocksProxyEntry
        {
            Id       = p.Id,
            Name     = p.Name,
            Host     = p.Host,
            Port     = p.Port,
            Username = p.Username,
            Password = p.Password,
            UseTls   = p.UseTls,
            TlsServerName = p.TlsServerName,
            TlsPinnedSha256 = p.TlsPinnedSha256
        }).ToList();

        ProxyListBox.ItemsSource = _list;
        ProxyListBox.SelectedIndex = _list.Count > 0 ? 0 : -1;
        ClearForm();
    }

    // ── 列表选中 ─────────────────────────────────────────────────────────────

    private void ProxyListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyListBox.SelectedItem is SocksProxyEntry p)
            FillForm(p);
        else
            ClearForm();
    }

    // ── 增删改 ───────────────────────────────────────────────────────────────

    private void AddProxy_Click(object sender, RoutedEventArgs e)
    {
        var entry = new SocksProxyEntry { Name = "新代理", Port = 1080 };
        _list.Add(entry);
        ProxyListBox.Items.Refresh();
        ProxyListBox.SelectedItem = entry;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void SaveProxy_Click(object sender, RoutedEventArgs e)
    {
        if (ProxyListBox.SelectedItem is not SocksProxyEntry p) return;

        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            ShowError("代理名称不能为空");
            NameBox.Focus();
            return;
        }
        if (_list.Any(x => !ReferenceEquals(x, p) &&
                           string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("代理名称已存在，请使用不同名称");
            NameBox.Focus();
            NameBox.SelectAll();
            return;
        }
        if (string.IsNullOrEmpty(host))
        {
            ShowError("主机地址不能为空");
            HostBox.Focus();
            return;
        }
        if (!int.TryParse(PortBox.Text, out int port) || port < 1 || port > 65535)
        {
            ShowError("端口必须在 1–65535 之间");
            PortBox.Focus();
            return;
        }

        p.Name     = name;
        p.Host     = host;
        p.Port     = port;
        p.Username = UsernameBox.Text.Trim();
        p.Password = PasswordBox.Password;
        p.UseTls = UseTlsBox.IsChecked == true;
        p.TlsServerName = TlsServerNameBox.Text.Trim();
        p.TlsPinnedSha256 = TlsPinnedSha256Box.Text.Trim();

        ProxyListBox.Items.Refresh();
        Persist();
    }

    private void DeleteProxy_Click(object sender, RoutedEventArgs e)
    {
        if (ProxyListBox.SelectedItem is not SocksProxyEntry p) return;
        _list.Remove(p);
        ProxyListBox.Items.Refresh();
        ProxyListBox.SelectedIndex = _list.Count > 0 ? 0 : -1;
        Persist();
    }

    // ── 关闭 ─────────────────────────────────────────────────────────────────

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ── 辅助 ─────────────────────────────────────────────────────────────────

    private void FillForm(SocksProxyEntry p)
    {
        NameBox.Text         = p.Name;
        HostBox.Text         = p.Host;
        PortBox.Text         = p.Port.ToString();
        UsernameBox.Text     = p.Username;
        PasswordBox.Password = p.Password;
        UseTlsBox.IsChecked  = p.UseTls;
        TlsServerNameBox.Text = p.TlsServerName;
        TlsPinnedSha256Box.Text = p.TlsPinnedSha256;
    }

    private void ClearForm()
    {
        NameBox.Text         = "";
        HostBox.Text         = "";
        PortBox.Text         = "1080";
        UsernameBox.Text     = "";
        PasswordBox.Password = "";
        UseTlsBox.IsChecked  = false;
        TlsServerNameBox.Text = "";
        TlsPinnedSha256Box.Text = "";
    }

    private void Persist()
    {
        _settings.SocksProxies = _list.Select(x => new SocksProxyEntry
        {
            Id       = x.Id,
            Name     = x.Name,
            Host     = x.Host,
            Port     = x.Port,
            Username = x.Username,
            Password = x.Password,
            UseTls   = x.UseTls,
            TlsServerName = x.TlsServerName,
            TlsPinnedSha256 = x.TlsPinnedSha256
        }).ToList();
        _settings.Save();
    }

    // 获得焦点时全选，方便直接覆盖输入
    private void Input_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
            tb.SelectAll();
    }

    private void PasswordBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            pb.SelectAll();
    }

    private void ShowError(string msg)
        => AppMsg.Show(this, msg, "输入错误", AppMsgIcon.Warning);
}
