using System;
using System.Net.Sockets;
using System.Text;
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
        using var socks = new TcpClient();
        socks.NoDelay = true;
        await socks.ConnectAsync(proxy.Host, proxy.Port, ct);
        await using var stream = socks.GetStream();

        // ── 握手阶段 ────────────────────────────────────────────────────────
        bool hasAuth = !string.IsNullOrEmpty(proxy.Username);

        // 问候：支持的认证方式
        byte[] greet = hasAuth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await stream.WriteAsync(greet, ct);

        var greetResp = await ReadExactlyAsync(stream, 2, ct);
        if (greetResp[0] != 0x05)
            throw new Exception("SOCKS5 服务器响应版本错误");

        // 用户名/密码认证
        if (greetResp[1] == 0x02)
        {
            if (!hasAuth)
                throw new Exception("代理要求认证但未配置用户名");

            var u = Encoding.UTF8.GetBytes(proxy.Username);
            var p = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            var auth = new byte[3 + u.Length + p.Length];
            auth[0] = 0x01;
            auth[1] = (byte)u.Length;
            u.CopyTo(auth, 2);
            auth[2 + u.Length] = (byte)p.Length;
            p.CopyTo(auth, 3 + u.Length);
            await stream.WriteAsync(auth, ct);

            var authResp = await ReadExactlyAsync(stream, 2, ct);
            if (authResp[1] != 0x00)
                throw new Exception("SOCKS5 认证失败（用户名或密码错误）");
        }
        else if (greetResp[1] != 0x00)
        {
            throw new Exception($"SOCKS5 无可用认证方式 (0x{greetResp[1]:X2})");
        }

        // ── CONNECT 目标 ────────────────────────────────────────────────────
        var hostBytes = Encoding.UTF8.GetBytes(host);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[5 + hostBytes.Length] = (byte)(port >> 8);
        req[6 + hostBytes.Length] = (byte)port;
        await stream.WriteAsync(req, ct);

        var rep = await ReadExactlyAsync(stream, 4, ct);
        if (rep[1] != 0x00)
            throw new Exception($"SOCKS5 CONNECT 被拒绝 (0x{rep[1]:X2})");

        // 读完 BND.ADDR 和 BND.PORT（不使用，但必须消费）
        switch (rep[3])
        {
            case 0x01: await ReadExactlyAsync(stream, 6, ct); break;
            case 0x04: await ReadExactlyAsync(stream, 18, ct); break;
            case 0x03:
                var lenBuf = await ReadExactlyAsync(stream, 1, ct);
                await ReadExactlyAsync(stream, lenBuf[0] + 2, ct);
                break;
        }
        // 到达这里即说明 CONNECT 成功，目标端口可达
    }

    private static async Task<byte[]> ReadExactlyAsync(
        NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (n == 0) throw new Exception("连接已关闭（读取 SOCKS5 响应时）");
            offset += n;
        }
        return buf;
    }
}
