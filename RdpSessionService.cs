using System;
using System.Windows;

namespace RemoteX;

internal sealed class RdpSessionService : IDisposable
{
    private readonly AxMSTSCLib.AxMsRdpClient9NotSafeForScripting _rdpClient;
    private bool _disposed;

    public System.Windows.Forms.Integration.WindowsFormsHost Host { get; }

    public event Action? Connected;
    public event Action? Disconnected;

    public RdpSessionService()
    {
        _rdpClient = new AxMSTSCLib.AxMsRdpClient9NotSafeForScripting
        {
            Dock = System.Windows.Forms.DockStyle.Fill
        };

        _rdpClient.OnConnected    += (_, __)  => Connected?.Invoke();
        _rdpClient.OnDisconnected += (_, e)   =>
        {
            AppLogger.Info($"rdp disconnected, reason={e.discReason}");
            Disconnected?.Invoke();
        };

        Host = new System.Windows.Forms.Integration.WindowsFormsHost
        {
            Child = _rdpClient,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment   = System.Windows.VerticalAlignment.Stretch
        };
    }

    public bool IsConnected => _rdpClient.Connected != 0;
    public string ServerAddress => _rdpClient.Server ?? "";

    public void Connect(ServerInfo server, int desktopWidth, int desktopHeight, int authLevel = 0, string? connectAddressOverride = null)
    {
        // AxMsRdpClient.Server 只能接受纯主机名/IP，不能含 ":port"。
        // 端口必须通过 AdvancedSettings2.RDPPort 单独设置，否则会抛出
        // "Value does not fall within the expected range"（COM E_INVALIDARG）。
        string rdpHost;
        int    rdpPort;
        if (connectAddressOverride != null)
        {
            (rdpHost, rdpPort) = SplitHostPort(connectAddressOverride, server.Port);
        }
        else
        {
            rdpHost = server.IP;
            rdpPort = server.Port;
        }

        _rdpClient.Server                                    = rdpHost;
        _rdpClient.AdvancedSettings2.RDPPort                = rdpPort;
        _rdpClient.UserName                                  = server.Username;
        _rdpClient.AdvancedSettings2.ClearTextPassword       = server.Password;
        _rdpClient.AdvancedSettings2.SmartSizing             = true;
        _rdpClient.DesktopWidth                              = desktopWidth;
        _rdpClient.DesktopHeight                             = desktopHeight;
        _rdpClient.AdvancedSettings5.AuthenticationLevel     = (uint)authLevel;
        _rdpClient.AdvancedSettings7.EnableCredSspSupport    = true;
        _rdpClient.Connect();
    }

    /// <summary>将 "host:port" 或 "host" 拆分为 (host, port)。</summary>
    private static (string host, int port) SplitHostPort(string address, int fallbackPort)
    {
        var colonIdx = address.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(address.AsSpan(colonIdx + 1), out var p))
            return (address[..colonIdx], p);
        return (address, fallbackPort);
    }

    public void Disconnect()
    {
        if (_rdpClient.Connected != 0)
            _rdpClient.Disconnect();
    }

    public void Refresh()
    {
        _rdpClient.Refresh();
    }

    /// <param name="physicalWidth">??????????DPI ????/param>
    /// <param name="physicalHeight">??????????DPI ????/param>
    /// <param name="logicalWidth">???????WPF ????/param>
    /// <param name="logicalHeight">???????WPF ????/param>
    public void UpdateDisplaySettings(
        int physicalWidth, int physicalHeight,
        int logicalWidth,  int logicalHeight)
    {
        _rdpClient.UpdateSessionDisplaySettings(
            (uint)logicalWidth,   (uint)logicalHeight,
            (uint)physicalWidth,  (uint)physicalHeight,
            0, 100, 100);
    }

    public static string BuildAddress(ServerInfo server)
        => server.Port == 3389 ? server.IP : $"{server.IP}:{server.Port}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Disconnect(); } catch { }

        try
        {
            Host.Child = null;
            _rdpClient.Dispose();
        }
        catch { }

        try { Host.Dispose(); } catch { }
    }
}
