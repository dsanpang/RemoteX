using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteX;

internal sealed class SocksTunnelConnection : IDisposable, IAsyncDisposable
{
    public SocksTunnelConnection(TcpClient client, Stream stream)
    {
        Client = client;
        Stream = stream;
    }

    public TcpClient Client { get; }
    public Stream Stream { get; }

    public void Dispose()
    {
        try { Stream.Dispose(); } catch { }
        try { Client.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Stream is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                Stream.Dispose();
        }
        catch { }

        try { Client.Dispose(); } catch { }
    }
}

internal static class Socks5Helper
{
    public static Task<SocksTunnelConnection> ConnectAsync(
        SocksProxyEntry proxy,
        string targetHost,
        int targetPort,
        CancellationToken ct)
        => ConnectAsync(proxy, proxy.Host, proxy.Port, targetHost, targetPort, ct);

    public static async Task<SocksTunnelConnection> ConnectAsync(
        SocksProxyEntry proxy,
        string proxyHost,
        int proxyPort,
        string targetHost,
        int targetPort,
        CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(proxyHost, proxyPort, ct);

            Stream transport = tcp.GetStream();
            if (proxy.UseTls)
                transport = await UpgradeToTlsAsync(proxy, transport, ct);

            await AuthenticateAndConnectAsync(
                transport,
                string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username,
                string.IsNullOrWhiteSpace(proxy.Password) ? null : proxy.Password,
                targetHost, targetPort, ct);

            return new SocksTunnelConnection(tcp, transport);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public static async Task ProbeAsync(
        SocksProxyEntry proxy,
        string targetHost,
        int targetPort,
        CancellationToken ct)
    {
        await using var tunnel = await ConnectAsync(proxy, targetHost, targetPort, ct);
    }

    private static async Task AuthenticateAndConnectAsync(
        Stream stream,
        string? proxyUser,
        string? proxyPass,
        string targetHost,
        int targetPort,
        CancellationToken ct)
    {
        bool hasAuth = !string.IsNullOrEmpty(proxyUser);

        byte[] greet = hasAuth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await stream.WriteAsync(greet, ct);

        var greetResp = await ReadExactlyAsync(stream, 2, ct);
        if (greetResp[0] != 0x05)
            throw new Exception("SOCKS5 服务器响应版本错误");

        if (greetResp[1] == 0x02)
        {
            if (!hasAuth)
                throw new Exception("代理要求认证但未配置用户名");

            var userBytes = Encoding.UTF8.GetBytes(proxyUser!);
            var passBytes = Encoding.UTF8.GetBytes(proxyPass ?? "");
            if (userBytes.Length > byte.MaxValue || passBytes.Length > byte.MaxValue)
                throw new Exception("SOCKS5 用户名或密码过长");

            var auth = new byte[3 + userBytes.Length + passBytes.Length];
            auth[0] = 0x01;
            auth[1] = (byte)userBytes.Length;
            userBytes.CopyTo(auth, 2);
            auth[2 + userBytes.Length] = (byte)passBytes.Length;
            passBytes.CopyTo(auth, 3 + userBytes.Length);
            await stream.WriteAsync(auth, ct);

            var authResp = await ReadExactlyAsync(stream, 2, ct);
            if (authResp[1] != 0x00)
                throw new Exception("SOCKS5 认证失败（用户名或密码错误）");
        }
        else if (greetResp[1] != 0x00)
        {
            throw new Exception($"SOCKS5 无可用认证方式 (0x{greetResp[1]:X2})");
        }

        byte[] connectReq = BuildConnectRequest(targetHost, targetPort);
        await stream.WriteAsync(connectReq, ct);

        var rep = await ReadExactlyAsync(stream, 4, ct);
        if (rep[1] != 0x00)
            throw new Exception($"SOCKS5 CONNECT 失败：{DescribeReplyCode(rep[1])} (0x{rep[1]:X2})");

        await ConsumeBoundAddressAsync(stream, rep[3], ct);
    }

    private static async Task<Stream> UpgradeToTlsAsync(
        SocksProxyEntry proxy,
        Stream baseStream,
        CancellationToken ct)
    {
        var targetHost = string.IsNullOrWhiteSpace(proxy.TlsServerName)
            ? proxy.Host
            : proxy.TlsServerName.Trim();

        var sslStream = new SslStream(
            baseStream,
            leaveInnerStreamOpen: false,
            (_, certificate, _, policyErrors) => ValidateServerCertificate(proxy, certificate, policyErrors));

        await sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            ct);

        return sslStream;
    }

    private static bool ValidateServerCertificate(
        SocksProxyEntry proxy,
        X509Certificate? certificate,
        SslPolicyErrors policyErrors)
    {
        if (certificate == null)
            return false;

        var pinned = NormalizeFingerprint(proxy.TlsPinnedSha256);
        if (!string.IsNullOrEmpty(pinned))
        {
            using var cert2 = new X509Certificate2(certificate);
            var actual = NormalizeFingerprint(cert2.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
            return string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase);
        }

        return policyErrors == SslPolicyErrors.None;
    }

    private static string NormalizeFingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    private static byte[] BuildConnectRequest(string host, int port)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            byte atyp;
            byte[] addrBytes;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                atyp = 0x01;
                addrBytes = ip.GetAddressBytes();
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                atyp = 0x04;
                addrBytes = ip.GetAddressBytes();
            }
            else
            {
                throw new Exception("不支持的 IP 地址类型");
            }

            var req = new byte[4 + addrBytes.Length + 2];
            req[0] = 0x05;
            req[1] = 0x01;
            req[2] = 0x00;
            req[3] = atyp;
            addrBytes.CopyTo(req, 4);
            req[^2] = (byte)(port >> 8);
            req[^1] = (byte)port;
            return req;
        }

        var hostBytes = Encoding.UTF8.GetBytes(host);
        if (hostBytes.Length == 0 || hostBytes.Length > byte.MaxValue)
            throw new Exception("目标主机名长度无效");

        var domainReq = new byte[7 + hostBytes.Length];
        domainReq[0] = 0x05;
        domainReq[1] = 0x01;
        domainReq[2] = 0x00;
        domainReq[3] = 0x03;
        domainReq[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(domainReq, 5);
        domainReq[^2] = (byte)(port >> 8);
        domainReq[^1] = (byte)port;
        return domainReq;
    }

    private static async Task ConsumeBoundAddressAsync(
        Stream stream,
        byte atyp,
        CancellationToken ct)
    {
        switch (atyp)
        {
            case 0x01:
                await ReadExactlyAsync(stream, 6, ct);
                break;
            case 0x04:
                await ReadExactlyAsync(stream, 18, ct);
                break;
            case 0x03:
                var len = await ReadExactlyAsync(stream, 1, ct);
                await ReadExactlyAsync(stream, len[0] + 2, ct);
                break;
            default:
                throw new Exception($"SOCKS5 返回了未知地址类型 0x{atyp:X2}");
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (n == 0)
                throw new EndOfStreamException("连接已关闭（读取 SOCKS5 响应时）");
            offset += n;
        }
        return buf;
    }

    private static string DescribeReplyCode(byte code)
        => code switch
        {
            0x01 => "普通服务器故障",
            0x02 => "规则集禁止连接",
            0x03 => "网络不可达",
            0x04 => "主机不可达",
            0x05 => "连接被拒绝",
            0x06 => "TTL 已过期",
            0x07 => "命令不支持",
            0x08 => "地址类型不支持",
            _    => "未知错误"
        };
}
