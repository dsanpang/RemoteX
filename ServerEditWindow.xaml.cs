using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using MessageBox = System.Windows.MessageBox;

namespace RemoteX;

public partial class ServerEditWindow : Window
{
    private readonly ServerInfo _server;
    private readonly List<string> _socksProxyNames = new();

    public ServerEditWindow(ServerInfo server, bool isNew, IEnumerable<string>? socksProxyNames = null)
    {
        InitializeComponent();
        _server = server;
        if (socksProxyNames != null)
            _socksProxyNames.AddRange(socksProxyNames);
        Title = isNew ? "新增服务器" : "编辑服务器";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 协议下拉
        var protoIndex = _server.Protocol switch
        {
            ServerProtocol.SSH    => 1,
            ServerProtocol.Telnet => 2,
            _                     => 0
        };
        ProtocolBox.SelectedIndex = protoIndex;

        NameBox.Text             = _server.Name;
        IpBox.Text               = _server.IP;
        PortBox.Text             = _server.Port.ToString();
        UsernameBox.Text         = _server.Username;
        PasswordBox.Password     = _server.Password;
        GroupBox.Text            = _server.Group;
        SshKeyBox.Text           = _server.SshPrivateKeyPath;
        DescriptionBox.Text      = _server.Description;
        SocksProxyCombo.Items.Clear();
        SocksProxyCombo.Items.Add("直连");
        foreach (var n in _socksProxyNames)
            SocksProxyCombo.Items.Add(n);
        if (string.IsNullOrEmpty(_server.SocksProxyName))
            SocksProxyCombo.SelectedIndex = 0;
        else
        {
            var idx = _socksProxyNames.IndexOf(_server.SocksProxyName);
            SocksProxyCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
        }

        ApplyProtocolUi(_server.Protocol);

        NameBox.Focus();
        NameBox.SelectAll();
    }

    // ── 协议切换 ──────────────────────────────────────────────────────────────

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolBox.SelectedItem is not ComboBoxItem item) return;
        var proto = ParseProtocol(item.Tag as string);
        ApplyProtocolUi(proto);

        // 若端口仍是上一个协议的默认值，则自动切换
        if (int.TryParse(PortBox.Text, out int currentPort))
        {
            if (currentPort == 3389 || currentPort == 22 || currentPort == 23)
                PortBox.Text = DefaultPort(proto).ToString();
        }
    }

    private void ApplyProtocolUi(ServerProtocol proto)
    {
        SshKeyRow.Visibility = proto == ServerProtocol.SSH
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 私钥认证时密码字段变为「私钥口令」
        if (PasswordLabel != null)
            PasswordLabel.Text = proto == ServerProtocol.SSH ? "私钥口令" : "密码";
    }

    private static ServerProtocol ParseProtocol(string? tag) => tag switch
    {
        "SSH"    => ServerProtocol.SSH,
        "Telnet" => ServerProtocol.Telnet,
        _        => ServerProtocol.RDP
    };

    private static int DefaultPort(ServerProtocol p) => p switch
    {
        ServerProtocol.SSH    => 22,
        ServerProtocol.Telnet => 23,
        _                     => 3389
    };

    // ── 私钥浏览 ──────────────────────────────────────────────────────────────

    private void BrowseSshKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "选择 SSH 私钥文件",
            Filter = "私钥文件 (*.pem;*.ppk;*.key)|*.pem;*.ppk;*.key|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
            SshKeyBox.Text = dlg.FileName;
    }

    // ── 确定 ──────────────────────────────────────────────────────────────────

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) ||
            string.IsNullOrWhiteSpace(IpBox.Text))
        {
            MessageBox.Show("请输入 IP 地址或主机名", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            IpBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text, out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("端口号必须在 1-65535 之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            return;
        }

        var proto = ProtocolBox.SelectedItem is ComboBoxItem ci
            ? ParseProtocol(ci.Tag as string)
            : ServerProtocol.RDP;

        _server.Protocol          = proto;
        _server.Name              = NameBox.Text.Trim();
        _server.IP                = IpBox.Text.Trim();
        _server.Port              = port;
        _server.Username          = UsernameBox.Text.Trim();
        _server.Password          = PasswordBox.Password;
        _server.Group             = GroupBox.Text.Trim();
        _server.SshPrivateKeyPath = SshKeyBox.Text.Trim();
        _server.Description       = DescriptionBox.Text;
        _server.SocksProxyName    = SocksProxyCombo.SelectedItem is string s && s != "直连" ? s : "";

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OkButton_Click(sender, new RoutedEventArgs());
    }

    private void Input_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb) tb.SelectAll();
    }

    private void PasswordBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb) pb.SelectAll();
    }
}
