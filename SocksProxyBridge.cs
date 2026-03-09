using System.Net;
using System.Net.Sockets;
using System.Text;
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
    public int Start(string socksHost, int socksPort, string? socksUser, string? socksPass,
        string targetHost, int targetPort)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = AcceptLoopAsync(socksHost, socksPort, socksUser, socksPass, targetHost, targetPort);
        return LocalPort;
    }

    private async Task AcceptLoopAsync(string socksHost, int socksPort, string? socksUser, string? socksPass,
        string targetHost, int targetPort)
    {
        while (!_cts.Token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = BridgeOneAsync(client, socksHost, socksPort, socksUser, socksPass, targetHost, targetPort);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!_disposed) AppLogger.Warn($"socks bridge accept: {ex.Message}");
            }
        }
    }

    private static async Task BridgeOneAsync(TcpClient client, string socksHost, int socksPort,
        string? socksUser, string? socksPass, string targetHost, int targetPort)
    {
        try
        {
            await using var clientStream = client.GetStream();

            using var socks = new TcpClient();
            await socks.ConnectAsync(socksHost, socksPort);
            await using var socksStream = socks.GetStream();

            // SOCKS5 握手：若有用户名则提供 0x00 与 0x02
            byte[] greetOut = string.IsNullOrEmpty(socksUser)
                ? [0x05, 0x01, 0x00]
                : [0x05, 0x02, 0x00, 0x02];
            await socksStream.WriteAsync(greetOut);
            var greet = await ReadExactlyAsync(socksStream, 2);
            if (greet[0] != 0x05)
                return;
            if (greet[1] == 0x02 && !string.IsNullOrEmpty(socksUser))
            {
                var u = Encoding.UTF8.GetBytes(socksUser);
                var p = Encoding.UTF8.GetBytes(socksPass ?? "");
                var auth = new byte[3 + u.Length + p.Length];
                auth[0] = 0x01;
                auth[1] = (byte)u.Length;
                u.CopyTo(auth, 2);
                auth[2 + u.Length] = (byte)p.Length;
                p.CopyTo(auth, 3 + u.Length);
                await socksStream.WriteAsync(auth);
                var authResp = await ReadExactlyAsync(socksStream, 2);
                if (authResp[1] != 0x00) return;
            }
            else if (greet[1] != 0x00)
                return;

            // CONNECT targetHost:targetPort
            var hostBytes = Encoding.UTF8.GetBytes(targetHost);
            var req = new byte[7 + hostBytes.Length];
            req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03;
            req[4] = (byte)hostBytes.Length;
            hostBytes.CopyTo(req, 5);
            req[5 + hostBytes.Length] = (byte)(targetPort >> 8);
            req[6 + hostBytes.Length] = (byte)targetPort;
            await socksStream.WriteAsync(req);
            var rep = await ReadExactlyAsync(socksStream, 4);
            if (rep[1] != 0x00)
            {
                if (rep[3] == 0x01) await ReadExactlyAsync(socksStream, 6);
                else if (rep[3] == 0x04) await ReadExactlyAsync(socksStream, 18);
                else if (rep[3] == 0x03) { var len = await ReadExactlyAsync(socksStream, 1); await ReadExactlyAsync(socksStream, len[0] + 2); }
                return;
            }
            if (rep[3] == 0x01) await ReadExactlyAsync(socksStream, 6);
            else if (rep[3] == 0x04) await ReadExactlyAsync(socksStream, 18);
            else if (rep[3] == 0x03) { var len = await ReadExactlyAsync(socksStream, 1); await ReadExactlyAsync(socksStream, len[0] + 2); }

            var cts = new CancellationTokenSource();
            var toTarget = CopyStreamAsync(clientStream, socksStream, cts.Token);
            var toClient = CopyStreamAsync(socksStream, clientStream, cts.Token);
            await Task.WhenAny(toTarget, toClient);
            cts.Cancel();
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

    private static async Task<byte[]> ReadExactlyAsync(Stream s, int count)
    {
        var buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(off, count - off));
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
        return buf;
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
