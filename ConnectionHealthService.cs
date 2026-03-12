using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteX;

internal sealed class ConnectionHealthResult
{
    public bool PortReachable { get; init; }
    public string Message { get; init; } = "";
}

internal sealed class ConnectionHealthService
{
    /// <summary>
    /// 检测目标 host:port 是否可达。
    /// 若提供了 proxy，则通过 SOCKS5 代理发起连接（模拟真实访问路径）。
    /// </summary>
    public async Task<ConnectionHealthResult> CheckAsync(
        string host,
        int port,
        int tcpTimeoutMs = 3000,
        SocksProxyEntry? proxy = null)
    {
        bool portOk = false;
        string portMessage;

        try
        {
            using var cts = new CancellationTokenSource(tcpTimeoutMs);

            if (proxy is { Host.Length: > 0 })
                await ConnectViaSocksAsync(proxy, host, port, cts.Token);
            else
                await ConnectDirectAsync(host, port, cts.Token);

            portOk = true;
            portMessage = proxy != null
                ? $"TCP {port} 可达（经 {proxy.Name}）"
                : $"TCP {port} 可达";
        }
        catch (OperationCanceledException)
        {
            portMessage = $"TCP {port} 超时";
        }
        catch (Exception ex)
        {
            portMessage = $"TCP {port} 失败 ({ex.Message})";
        }

        return new ConnectionHealthResult
        {
            PortReachable = portOk,
            Message = portMessage
        };
    }

    private static async Task ConnectDirectAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
    }

    /// <summary>
    /// 通过 SOCKS5 代理执行 CONNECT 握手，成功即代表目标可达。
    /// 复用 SocksProxyBridge 相同的握手逻辑，结果与实际连接路径一致。
    /// </summary>
    private static async Task ConnectViaSocksAsync(
        SocksProxyEntry proxy, string host, int port, CancellationToken ct)
    {
        await Socks5Helper.ProbeAsync(proxy, host, port, ct);
    }
}
