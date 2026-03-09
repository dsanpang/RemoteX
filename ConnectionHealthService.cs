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
    public async Task<ConnectionHealthResult> CheckAsync(
        string host,
        int port,
        int tcpTimeoutMs = 1800)
    {
        bool portOk = false;
        string portMessage;

        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(tcpTimeoutMs);
            await tcp.ConnectAsync(host, port, cts.Token);
            portOk = true;
            portMessage = $"TCP {port} 可达";
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
}
