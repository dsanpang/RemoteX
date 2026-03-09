using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteX.SocksServer;

/// <summary>SOCKS5 服务端，运行在 Windows 跳板机上，供 RemoteX 通过 SOCKS 连接内网服务器。</summary>
internal static class Program
{
    private static int _listenPort = 1080;
    private static string? _authUser;
    private static string? _authPass;

    static async Task Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "-p" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                _listenPort = p;
                i++;
            }
            else if (args[i] == "--user" && i + 1 < args.Length)
            {
                _authUser = args[i + 1];
                i++;
            }
            else if (args[i] == "--password" && i + 1 < args.Length)
            {
                _authPass = args[i + 1];
                i++;
            }
        }

        var listener = new TcpListener(IPAddress.Any, _listenPort);
        listener.Start();
        Console.WriteLine($"SOCKS5 服务端已启动，监听 0.0.0.0:{_listenPort}");
        if (_authUser != null)
            Console.WriteLine("已启用用户名/密码认证");
        else
            Console.WriteLine("无认证（仅限可信网络使用）");
        Console.WriteLine("按 Ctrl+C 退出。");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client);
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            await using var clientStream = client.GetStream();

            // SOCKS5 握手：版本 + 方法列表
            var greet = await ReadExactlyAsync(clientStream, 2);
            if (greet[0] != 0x05)
            {
                await clientStream.WriteAsync([0x05, 0xFF]);
                return;
            }
            var nmethods = greet[1];
            var methods = await ReadExactlyAsync(clientStream, nmethods);
            bool supportNoAuth = methods.Contains(0x00);
            bool supportUserPass = methods.Contains(0x02);
            byte chosen = 0xFF;
            if (_authUser != null && supportUserPass)
                chosen = 0x02;
            else if (supportNoAuth)
                chosen = 0x00;
            await clientStream.WriteAsync([0x05, chosen]);

            if (chosen == 0xFF)
                return;

            if (chosen == 0x02)
            {
                var authHdr = await ReadExactlyAsync(clientStream, 2);
                if (authHdr[0] != 0x01) return;
                var ulen = authHdr[1];
                var user = Encoding.UTF8.GetString(await ReadExactlyAsync(clientStream, ulen));
                var plen = (await ReadExactlyAsync(clientStream, 1))[0];
                var pass = Encoding.UTF8.GetString(await ReadExactlyAsync(clientStream, plen));
                byte ok = (user == _authUser && pass == _authPass) ? (byte)0x00 : (byte)0x01;
                await clientStream.WriteAsync([0x01, ok]);
                if (ok != 0x00) return;
            }

            var req = await ReadExactlyAsync(clientStream, 4);
            if (req[0] != 0x05 || req[1] != 0x01)
            {
                await SendReply(clientStream, 0x01);
                return;
            }
            string dstHost;
            switch (req[3])
            {
                case 0x01:
                    var ip4 = await ReadExactlyAsync(clientStream, 4);
                    dstHost = new IPAddress(ip4).ToString();
                    break;
                case 0x03:
                    var dlen = (await ReadExactlyAsync(clientStream, 1))[0];
                    dstHost = Encoding.UTF8.GetString(await ReadExactlyAsync(clientStream, dlen));
                    break;
                case 0x04:
                    var ip6 = await ReadExactlyAsync(clientStream, 16);
                    dstHost = new IPAddress(ip6).ToString();
                    break;
                default:
                    await SendReply(clientStream, 0x08);
                    return;
            }
            var portBytes = await ReadExactlyAsync(clientStream, 2);
            int dstPort = (portBytes[0] << 8) | portBytes[1];

            TcpClient? target = null;
            try
            {
                target = new TcpClient();
                await target.ConnectAsync(dstHost, dstPort);
                await SendReply(clientStream, 0x00, IPAddress.Loopback, (ushort)_listenPort);
            }
            catch
            {
                await SendReply(clientStream, 0x05);
                return;
            }

            using (target)
            {
                var targetStream = target.GetStream();
                var cts = new CancellationTokenSource();
                var toTarget = CopyStreamAsync(clientStream, targetStream, cts.Token);
                var toClient = CopyStreamAsync(targetStream, clientStream, cts.Token);
                await Task.WhenAny(toTarget, toClient);
                cts.Cancel();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"会话异常: {ex.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task SendReply(NetworkStream stream, byte rep, IPAddress? bindAddr = null, ushort bindPort = 0)
    {
        bindAddr ??= IPAddress.Loopback;
        var addr = bindAddr.GetAddressBytes();
        await stream.WriteAsync([0x05, rep, 0x00, (byte)(addr.Length == 4 ? 0x01 : 0x04), .. addr, (byte)(bindPort >> 8), (byte)bindPort]);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(off, count - off));
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
}
