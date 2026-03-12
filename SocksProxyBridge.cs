using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteX;

/// <summary>
/// 本地端口转发：监听随机端口，有连接时通过 SOCKS5 代理 CONNECT 到目标，并双向桥接流。
/// 用于 RDP/SSH/Telnet 经跳板机 SOCKS 连接内网服务器。
/// </summary>
internal sealed class SocksProxyBridge : IDisposable
{
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>获取本地监听端口（Start 之后有效）</summary>
    public int LocalPort { get; private set; }

    /// <summary>
    /// 启动桥接：在本地随机端口监听，每个接入连接通过 SOCKS5 连接到 targetHost:targetPort 并桥接。
    /// </summary>
    /// <returns>本地监听端口</returns>
    public int Start(SocksProxyEntry proxy, string targetHost, int targetPort)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = AcceptLoopAsync(proxy, targetHost, targetPort);
        return LocalPort;
    }

    private async Task AcceptLoopAsync(SocksProxyEntry proxy, string targetHost, int targetPort)
    {
        while (!_cts.Token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = BridgeOneAsync(client, proxy, targetHost, targetPort);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!_disposed) AppLogger.Warn($"socks bridge accept: {ex.Message}");
            }
        }
    }

    private static async Task BridgeOneAsync(TcpClient client, SocksProxyEntry proxy, string targetHost, int targetPort)
    {
        try
        {
            await using var clientStream = client.GetStream();

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var tunnel = await Socks5Helper.ConnectAsync(proxy, targetHost, targetPort, connectCts.Token);
            // 禁用 Nagle 算法，确保退格键等单字节交互数据立即发送，不被缓冲延迟
            client.NoDelay = true;
            var socksStream = tunnel.Stream;

            var cts = new CancellationTokenSource();
            var toTarget = CopyStreamAsync(clientStream, socksStream, cts.Token);
            var toClient = CopyStreamAsync(socksStream, clientStream, cts.Token);
            await Task.WhenAny(toTarget, toClient);
            cts.Cancel();
            try { await Task.WhenAll(toTarget, toClient); } catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"socks bridge: {ex.Message}");
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    private static async Task CopyStreamAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buf = new byte[8192];
        int n;
        while ((n = await from.ReadAsync(buf, ct)) > 0)
            await to.WriteAsync(buf.AsMemory(0, n), ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener?.Stop(); _listener = null; } catch { }
        _cts.Dispose();
    }
}
