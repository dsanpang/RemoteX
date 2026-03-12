using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RemoteX;

public enum ServerHealthState { Unknown, Checking, Online, Offline }

public enum ServerProtocol { RDP, SSH, Telnet }

public class ServerInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name);
    }

    // 主键，不参与绑定通知
    public int Id { get; set; }

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _ip = "";
    public string IP { get => _ip; set => Set(ref _ip, value); }

    private string _username = "";
    public string Username { get => _username; set => Set(ref _username, value); }

    // 密码不需要 INPC（不直接绑定到 UI）
    public string Password { get; set; } = "";

    private int _port = 3389;
    public int Port
    {
        get => _port;
        set
        {
            if (_port == value) return;
            _port = value;
            Notify();
            Notify(nameof(AddressDisplay));
        }
    }

    private string _description = "";
    public string Description { get => _description; set => Set(ref _description, value); }

    /// <summary>分组名称，空字符串表示「未分组」。</summary>
    private string _group = "";
    public string Group { get => _group; set => Set(ref _group, value); }

    /// <summary>拖拽排序权重，值越小越靠前。</summary>
    public int SortOrder { get; set; }

    private ServerProtocol _protocol = ServerProtocol.RDP;
    /// <summary>连接协议：RDP / SSH / Telnet。</summary>
    public ServerProtocol Protocol
    {
        get => _protocol;
        set
        {
            if (_protocol == value) return;
            _protocol = value;
            Notify();
            Notify(nameof(ProtocolBadge));
            Notify(nameof(AddressDisplay));
        }
    }

    private string _sshPrivateKeyPath = "";
    /// <summary>SSH 私钥文件路径（仅 SSH 协议有效）。</summary>
    public string SshPrivateKeyPath { get => _sshPrivateKeyPath; set => Set(ref _sshPrivateKeyPath, value); }

    /// <summary>通过 SOCKS 连接时使用的代理名称（来自设置中的代理列表）；空表示直连。</summary>
    private string _socksProxyId = "";
    public string SocksProxyId { get => _socksProxyId; set => Set(ref _socksProxyId, value ?? ""); }

    private string _socksProxyName = "";
    public string SocksProxyName { get => _socksProxyName; set => Set(ref _socksProxyName, value ?? ""); }

    // 健康状态

    private ServerHealthState _healthState = ServerHealthState.Unknown;
    public ServerHealthState HealthState
    {
        get => _healthState;
        set
        {
            if (_healthState == value) return;
            _healthState = value;
            Notify();
            Notify(nameof(HealthBadge));
            Notify(nameof(HealthOrder));
        }
    }

    private string _healthMessage = "";
    public string HealthMessage { get => _healthMessage; set => Set(ref _healthMessage, value); }

    public int HealthOrder => HealthState switch
    {
        ServerHealthState.Online   => 0,
        ServerHealthState.Checking => 1,
        ServerHealthState.Unknown  => 2,
        ServerHealthState.Offline  => 3,
        _ => 9
    };

    public string HealthBadge => HealthState switch
    {
        ServerHealthState.Online   => "● 在线",
        ServerHealthState.Checking => "○ 检测中",
        ServerHealthState.Offline  => "● 离线",
        _ => "○ 未知"
    };

    /// <summary>显示用分组标签（空分组时显示「未分组」）。</summary>
    public string GroupDisplay => string.IsNullOrWhiteSpace(Group) ? "未分组" : Group;
    /// <summary>列表中显示的地址，格式为 IP:Port（默认端口省略端口号）。</summary>
    public string AddressDisplay
    {
        get
        {
            int defaultPort = Protocol switch
            {
                ServerProtocol.SSH    => 22,
                ServerProtocol.Telnet => 23,
                _                     => 3389
            };
            return Port == defaultPort ? IP : $"{IP}:{Port}";
        }
    }

    /// <summary>协议徽章短文字。</summary>
    public string ProtocolBadge => Protocol switch
    {
        ServerProtocol.SSH    => "SSH",
        ServerProtocol.Telnet => "TEL",
        _                     => "RDP"
    };
}
