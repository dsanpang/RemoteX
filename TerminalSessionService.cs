using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Renci.SshNet;

namespace RemoteX;

/// <summary>管理单个 SSH 或 Telnet 终端会话的生命周期。</summary>
internal sealed class TerminalSessionService : IDisposable
{
    // ── 事件 ──────────────────────────────────────────────────────────────────
    public event Action<string>? DataReceived;
    public event Action?         Connected;
    public event Action?         Disconnected;

    // ── SSH ──────────────────────────────────────────────────────────────────
    private SshClient?   _sshClient;
    private ShellStream? _shellStream;

    // ── Telnet ────────────────────────────────────────────────────────────────
    private TcpClient?     _telnetClient;
    private NetworkStream? _telnetStream;

    // 公共状态
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public bool IsConnected { get; private set; }

    // ── 连接入口 ──────────────────────────────────────────────────────────────

    /// <summary>连接超时（秒），默认 15 s。</summary>
    public static int ConnectTimeoutSeconds { get; set; } = 15;

    public async Task ConnectAsync(ServerInfo server)
    {
        _readCts = new CancellationTokenSource();

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(ConnectTimeoutSeconds));

        try
        {
            if (server.Protocol == ServerProtocol.SSH)
                await ConnectSshAsync(server, timeoutCts.Token).ConfigureAwait(false);
            else
                await ConnectTelnetAsync(server, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"连接 {server.IP}:{server.Port} 超时（{ConnectTimeoutSeconds} 秒）");
        }
    }

    // ── SSH ───────────────────────────────────────────────────────────────────

    private async Task ConnectSshAsync(ServerInfo server, CancellationToken timeoutCt)
    {
        AuthenticationMethod auth;

        if (!string.IsNullOrWhiteSpace(server.SshPrivateKeyPath))
        {
            var passphrase = string.IsNullOrWhiteSpace(server.Password) ? null : server.Password;
            var keyFile    = passphrase == null
                ? new PrivateKeyFile(server.SshPrivateKeyPath)
                : new PrivateKeyFile(server.SshPrivateKeyPath, passphrase);
            auth = new PrivateKeyAuthenticationMethod(server.Username, keyFile);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(server.Username, server.Password ?? "");
        }

        var connInfo = new ConnectionInfo(server.IP, server.Port, server.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds)
        };
        _sshClient = new SshClient(connInfo);

        // TOFU：首次自动信任并存储，后续验证主机密钥（HostKeyReceived 在 SshClient 上）
        _sshClient.HostKeyReceived += (_, e) =>
        {
            var fp = e.FingerPrintSHA256 ?? (e.FingerPrint != null && e.FingerPrint.Length > 0
                ? BitConverter.ToString(e.FingerPrint).Replace("-", "") : null);
            if (string.IsNullOrEmpty(fp)) { e.CanTrust = true; return; }
            if (SshKnownHosts.TryGet(server.IP, server.Port, out var stored))
            {
                e.CanTrust = string.Equals(stored, fp, StringComparison.OrdinalIgnoreCase);
                if (!e.CanTrust)
                    AppLogger.Warn($"SSH host key changed for {server.IP}:{server.Port}, possible MITM");
            }
            else
            {
                SshKnownHosts.Add(server.IP, server.Port, fp);
                e.CanTrust = true;
            }
        };

        await Task.Run(() => _sshClient.Connect(), timeoutCt).ConfigureAwait(false);

        // 80 �?× 24 行，之后通过 Resize 更新实际大小
        _shellStream = _sshClient.CreateShellStream("xterm-256color", 80, 24, 0, 0, 4096);

        IsConnected = true;
        Connected?.Invoke();

        _ = Task.Run(() => ReadSshLoop(_readCts!.Token));
    }

    private void ReadSshLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _shellStream != null && IsConnected)
            {
                int count = _shellStream.Read(buffer, 0, buffer.Length);
                if (count <= 0) break;
                var data = Encoding.UTF8.GetString(buffer, 0, count);
                DataReceived?.Invoke(data);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLogger.Error("ssh read error", ex);
        }
        finally
        {
            if (IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke();
            }
        }
    }

    // ── Telnet ────────────────────────────────────────────────────────────────

    // 自动登录凭据
    private string _telnetUsername = "";
    private string _telnetPassword = "";

    private async Task ConnectTelnetAsync(ServerInfo server, CancellationToken timeoutCt)
    {
        _telnetUsername = server.Username ?? "";
        _telnetPassword = server.Password ?? "";

        _telnetClient = new TcpClient();
        await _telnetClient.ConnectAsync(server.IP, server.Port, timeoutCt).ConfigureAwait(false);
        _telnetStream = _telnetClient.GetStream();

        IsConnected = true;
        Connected?.Invoke();

        _ = Task.Run(() => ReadTelnetLoop(_readCts!.Token));
    }

    private void ReadTelnetLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];

        // 自动登录状态：0=等待login提示, 1=已发用户名等待password, 2=完成
        int autoState = string.IsNullOrEmpty(_telnetUsername) ? 2 : 0;
        var promptBuf = new System.Text.StringBuilder(512);

        try
        {
            while (!ct.IsCancellationRequested && _telnetStream != null && IsConnected)
            {
                int count = _telnetStream.Read(buffer, 0, buffer.Length);
                if (count <= 0) break;

                // 处理 Telnet IAC 协商，只将可打印数据送到终端
                var (printable, response) = ProcessTelnetIac(buffer, count);
                if (response.Count > 0)
                {
                    var resp = response.ToArray();
                    _telnetStream.Write(resp, 0, resp.Length);
                }

                if (printable.Count > 0)
                {
                    var data = Encoding.UTF8.GetString(printable.ToArray());
                    DataReceived?.Invoke(data);

                    // 自动登录检测
                    if (autoState < 2)
                    {
                        promptBuf.Append(data);
                        // 只保留最近 256 字符，避免无限增长
                        if (promptBuf.Length > 256)
                            promptBuf.Remove(0, promptBuf.Length - 256);

                        var recent = promptBuf.ToString();

                        if (autoState == 0 &&
                            (ContainsIgnoreCase(recent, "login:") ||
                             ContainsIgnoreCase(recent, "username:") ||
                             ContainsIgnoreCase(recent, "用户名")))
                        {
                            SendInput(_telnetUsername + "\r\n");
                            autoState = string.IsNullOrEmpty(_telnetPassword) ? 2 : 1;
                            promptBuf.Clear();
                        }
                        else if (autoState == 1 &&
                                 (ContainsIgnoreCase(recent, "password:") ||
                                  ContainsIgnoreCase(recent, "密码:")))
                        {
                            SendInput(_telnetPassword + "\r\n");
                            autoState = 2;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLogger.Error("telnet read error", ex);
        }
        finally
        {
            if (IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke();
            }
        }
    }

    private static bool ContainsIgnoreCase(string source, string value)
        => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// 解析 Telnet IAC 序列，返回可打印数据和需要回复的协商字节。
    /// 对 WILL 回复 DONT，对 DO 回复 WONT，SUPPRESS-GO-AHEAD 例外（接受）。
    /// </summary>
    private static (List<byte> printable, List<byte> response) ProcessTelnetIac(byte[] buf, int count)
    {
        const byte IAC  = 0xFF;
        const byte WILL = 0xFB;
        const byte WONT = 0xFC;
        const byte DO   = 0xFD;
        const byte DONT = 0xFE;
        const byte SB   = 0xFA;
        const byte SE   = 0xF0;
        const byte SGA  = 0x03; // Suppress Go Ahead

        var printable = new List<byte>(count);
        var response  = new List<byte>();
        int i = 0;

        while (i < count)
        {
            if (buf[i] != IAC)
            {
                printable.Add(buf[i++]);
                continue;
            }

            // IAC sequence
            if (i + 1 >= count) { i++; break; }

            byte cmd = buf[i + 1];

            if (cmd == SB)
            {
                // 跳过子协商直至 IAC SE
                i += 2;
                while (i < count && !(buf[i] == IAC && i + 1 < count && buf[i + 1] == SE))
                    i++;
                i += 2;
            }
            else if (cmd == WILL || cmd == DO)
            {
                if (i + 2 >= count) { i += 2; break; }
                byte opt = buf[i + 2];
                if (cmd == WILL)
                    // 接受 SGA（Suppress Go Ahead），拒绝其他
                    response.AddRange(opt == SGA ? [IAC, DO, opt] : [IAC, DONT, opt]);
                else
                    // DO 时告知我们 WONT，除 SGA
                    response.AddRange(opt == SGA ? [IAC, WILL, opt] : [IAC, WONT, opt]);
                i += 3;
            }
            else if (cmd == WONT || cmd == DONT)
            {
                i += 3;
            }
            else
            {
                i += 2;
            }
        }

        return (printable, response);
    }

    // ── 输入 & 调整大小 ───────────────────────────────────────────────────────

    public void SendInput(string data)
    {
        if (!IsConnected) return;

        try
        {
            if (_shellStream != null)
            {
                _shellStream.Write(data);
                _shellStream.Flush();
            }
            else if (_telnetStream != null)
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                _telnetStream.Write(bytes, 0, bytes.Length);
                _telnetStream.Flush();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("terminal send input error", ex);
        }
    }

    public void Resize(int cols, int rows)
    {
        // SSH.NET 2025.x 的 ShellStream 不直接暴露窗口大小更新 API，
        // 终端大小调整通过重建 ShellStream 实现（成本较高），此处保留接口供将来扩展。
        _ = cols; _ = rows;
    }

    // ── 释放 ─────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        IsConnected = false;
        _readCts?.Cancel();
        _readCts?.Dispose();

        try { _shellStream?.Dispose(); }  catch { }
        try { _sshClient?.Disconnect(); _sshClient?.Dispose(); } catch { }
        try { _telnetStream?.Dispose(); } catch { }
        try { _telnetClient?.Dispose(); } catch { }
    }
}
