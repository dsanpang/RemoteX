using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;


using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace RemoteX;

public partial class PortForwardEditWindow : Window
{
    private readonly List<ServerInfo> _servers;
    private readonly PortForwardRule? _existing;

    public PortForwardRule? Result { get; private set; }

    public PortForwardEditWindow(List<ServerInfo> servers, PortForwardRule? existing = null)
    {
        InitializeComponent();
        _servers  = servers;
        _existing = existing;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Populate server combo (SSH only)
        ServerCombo.Items.Clear();
        foreach (var s in _servers)
        {
            if (s.Protocol == ServerProtocol.SSH)
                ServerCombo.Items.Add(s);
        }

        if (_existing != null)
        {
            Title = "编辑转发规则";
            TitleLabel.Text = "编辑转发规则";

            NameBox.Text       = _existing.Name;
            LocalPortBox.Text  = _existing.LocalPort.ToString();
            RemoteHostBox.Text = _existing.RemoteHost;
            RemotePortBox.Text = _existing.RemotePort.ToString();
            AutoStartCheck.IsChecked = _existing.AutoStart;

            // Select server
            foreach (var item in ServerCombo.Items)
            {
                if (item is ServerInfo si && si.Id == _existing.ServerId)
                {
                    ServerCombo.SelectedItem = si;
                    break;
                }
            }

            // Select type
            TypeCombo.SelectedIndex = _existing.ForwardType switch
            {
                PortForwardType.Remote  => 1,
                PortForwardType.Dynamic => 2,
                _                       => 0
            };
        }
        else
        {
            Title = "新增转发规则";
            TitleLabel.Text = "新增转发规则";
            TypeCombo.SelectedIndex = 0;
            LocalPortBox.Text  = "8080";
            RemoteHostBox.Text = "127.0.0.1";
            RemotePortBox.Text = "80";
            if (ServerCombo.Items.Count > 0)
                ServerCombo.SelectedIndex = 0;
        }

        UpdateRemoteRowVisibility();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRemoteRowVisibility();
    }

    private void UpdateRemoteRowVisibility()
    {
        if (RemoteHostRow == null || RemotePortRow == null) return;
        var isDynamic = TypeCombo.SelectedIndex == 2;
        RemoteHostRow.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
        RemotePortRow.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            AppMsg.Show(this, "请输入规则名称", "提示", AppMsgIcon.Warning);
            NameBox.Focus();
            return;
        }

        if (ServerCombo.SelectedItem is not ServerInfo selectedServer)
        {
            AppMsg.Show(this, "请选择 SSH 服务器", "提示", AppMsgIcon.Warning);
            ServerCombo.Focus();
            return;
        }

        if (!int.TryParse(LocalPortBox.Text, out int localPort) || localPort < 1 || localPort > 65535)
        {
            AppMsg.Show(this, "本地端口必须在 1-65535 之间", "提示", AppMsgIcon.Warning);
            LocalPortBox.Focus();
            return;
        }

        var forwardType = TypeCombo.SelectedIndex switch
        {
            1 => PortForwardType.Remote,
            2 => PortForwardType.Dynamic,
            _ => PortForwardType.Local
        };

        int remotePort = 80;
        string remoteHost = "127.0.0.1";

        if (forwardType != PortForwardType.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(RemoteHostBox.Text))
            {
                AppMsg.Show(this, "请输入远端主机", "提示", AppMsgIcon.Warning);
                RemoteHostBox.Focus();
                return;
            }

            if (!int.TryParse(RemotePortBox.Text, out remotePort) || remotePort < 1 || remotePort > 65535)
            {
                AppMsg.Show(this, "远端端口必须在 1-65535 之间", "提示", AppMsgIcon.Warning);
                RemotePortBox.Focus();
                return;
            }

            remoteHost = RemoteHostBox.Text.Trim();
        }

        Result = new PortForwardRule
        {
            Id          = _existing?.Id ?? 0,
            Name        = NameBox.Text.Trim(),
            ServerId    = selectedServer.Id,
            ServerName  = selectedServer.Name,
            LocalPort   = localPort,
            RemoteHost  = remoteHost,
            RemotePort  = remotePort,
            ForwardType = forwardType,
            AutoStart   = AutoStartCheck.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OkButton_Click(sender, new RoutedEventArgs());
    }
}
