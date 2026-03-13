using System;
using System.Collections.Generic;
using System.IO;
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

    // ── ZMODEM 回调（由 MainWindow.Terminal 注入）──────────────────────────────
    /// <summary>传输进度文字，在非 UI 线程调用。</summary>
    public Action<string>? ZmodemStatus;
    /// <summary>文件接收完成，需弹 SaveFileDialog。在非 UI 线程调用。</summary>
    public Action<string, byte[]>? ZmodemFileReceived;
    /// <summary>需要上传文件，弹 OpenFileDialog。在非 UI 线程调用（必须内部 Dispatch）。</summary>
    public Func<Task<(string Name, byte[] Data)>>? ZmodemRequestUpload;

    // ── SSH ──────────────────────────────────────────────────────────────────
    private SshClient?   _sshClient;
    private ShellStream? _shellStream;

    // ── Telnet ────────────────────────────────────────────────────────────────
    private TcpClient?     _telnetClient;
    private Stream? _telnetStream;
    private SocksTunnelConnection? _telnetTunnel;

    // 公共状态
    private CancellationTokenSource? _readCts;
    private bool _disposed;
    private int _terminalCols = 80;
    private int _terminalRows = 24;

    public bool IsConnected { get; private set; }

    // ── 连接入口 ──────────────────────────────────────────────────────────────

    /// <summary>连接超时（秒），默认 15 s。</summary>
    public static int ConnectTimeoutSeconds { get; set; } = 15;

    public async Task ConnectAsync(ServerInfo server, SocksProxyEntry? proxy = null)
    {
        _readCts = new CancellationTokenSource();

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(ConnectTimeoutSeconds));

        try
        {
            if (server.Protocol == ServerProtocol.SSH)
                await ConnectSshAsync(server, proxy, timeoutCts.Token).ConfigureAwait(false);
            else
                await ConnectTelnetAsync(server, proxy, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"连接 {server.IP}:{server.Port} 超时（{ConnectTimeoutSeconds} 秒）");
        }
    }

    // ── SSH ───────────────────────────────────────────────────────────────────

    private async Task ConnectSshAsync(ServerInfo server, SocksProxyEntry? proxy, CancellationToken timeoutCt)
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

        var authMethods = new[] { auth };
        var connInfo = proxy is { Host.Length: > 0 }
            ? new ConnectionInfo(
                server.IP, server.Port, server.Username,
                ProxyTypes.Socks5, proxy.Host, proxy.Port,
                string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username,
                string.IsNullOrWhiteSpace(proxy.Password) ? null : proxy.Password,
                authMethods)
            : new ConnectionInfo(server.IP, server.Port, server.Username, authMethods)
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
        _shellStream = _sshClient.CreateShellStream(
            "xterm-256color",
            (uint)_terminalCols,
            (uint)_terminalRows,
            0, 0, 4096);

        IsConnected = true;
        Connected?.Invoke();

        _ = Task.Run(() => ReadSshLoop(_readCts!.Token));
    }

    // ZMODEM 握手：rz 触发为 **B01xx（ZRINIT），sz 触发为 **B00xx（ZRQINIT）。
    // 必须基于原始 byte[] 识别，进入传输模式后全程操作字节流，不得用 UTF-8 解码协议数据，否则会破坏帧并导致乱码/校验失败。
    private static readonly byte[] s_zmodemMagic = { 0x2A, 0x2A, 0x18 };

    private void ReadSshLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested && _shellStream != null && IsConnected)
            {
                // 原始字节读取，不做 \n→\r\n 等转换，否则会破坏 ZMODEM 帧
                int count = _shellStream.Read(buffer, 0, buffer.Length);
                if (count <= 0) break;

                // 扫描是否包含 ZMODEM 握手 **\x18[ABC]（即 **B0100 / **B0000 等）
                int zStart = FindZmodemMagic(buffer, count);
                if (zStart >= 0)
                {
                    // 尾帧（ZFIN/ZACK）不当作新会话，避免多次「传输中/传输结束」
                    if (TrySkipTrailingHexFrame(buffer, zStart, count, out int skipBytes))
                    {
                        if (zStart > 0)
                            DataReceived?.Invoke(Encoding.UTF8.GetString(buffer, 0, zStart));
                        int rest = count - zStart - skipBytes;
                        if (rest > 0)
                            DataReceived?.Invoke(Encoding.UTF8.GetString(buffer, zStart + skipBytes, rest));
                        continue;
                    }

                    // 仅将握手之前的正常输出用 UTF-8 送终端显示；ZMODEM 段不经过任何字符解码
                    if (zStart > 0)
                        DataReceived?.Invoke(Encoding.UTF8.GetString(buffer, 0, zStart));

                    // 将 [zStart..count) 作为 lookahead 传给 ZmodemTransfer
                    int lookaheadLen = count - zStart;
                    var lookahead    = new ReadOnlySpan<byte>(buffer, zStart, lookaheadLen);

                    bool didTransfer = RunZmodemTransfer(lookahead, ct);
                    if (!didTransfer)
                    {
                        // 仅收尾（首帧 ZFIN），下一轮 Read 会继续读到 "rz: file removed" 等
                    }
                    continue; // 传输完成后继续正常读循环
                }

                DataReceived?.Invoke(Encoding.UTF8.GetString(buffer, 0, count));
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

    /// <summary>在 buffer[0..count) 中查找 **\x18 后跟 B/A/C 的起始位置，找不到返回 -1。</summary>
    private static int FindZmodemMagic(byte[] buffer, int count)
    {
        // 魔法序列：0x2A 0x2A 0x18 (0x42|0x41|0x43)
        for (int i = 0; i < count - 3; i++)
        {
            if (buffer[i]   == 0x2A && buffer[i+1] == 0x2A &&
                buffer[i+2] == 0x18 &&
                (buffer[i+3] == 0x42 || buffer[i+3] == 0x41 || buffer[i+3] == 0x43))
                return i;
        }
        return -1;
    }

    /// <summary>若 buffer[zStart..] 为 hex 帧且类型为 ZFIN(8) 或 ZACK(3)，返回 true 并输出跳过的字节数；否则返回 false。</summary>
    private static bool TrySkipTrailingHexFrame(byte[] buffer, int zStart, int count, out int skipBytes)
    {
        skipBytes = 0;
        // hex 帧：**\x18B + 14 个十六进制字符 = 4+14=18（尾部 CR/LF 留给下一段显示）
        const int hexHeaderLen = 4 + 14;
        if (count < zStart + 6 || buffer[zStart + 3] != 0x42) return false;
        byte type = (byte)((ParseHexChar(buffer[zStart + 4]) << 4) | ParseHexChar(buffer[zStart + 5]));
        if (type != 8 && type != 3) return false; // 非 ZFIN、ZACK 不跳过
        if (count < zStart + hexHeaderLen) return false;
        skipBytes = hexHeaderLen;
        return true;
    }

    private static int ParseHexChar(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9') return b - (byte)'0';
        if (b >= (byte)'a' && b <= (byte)'f') return b - (byte)'a' + 10;
        if (b >= (byte)'A' && b <= (byte)'F') return b - (byte)'A' + 10;
        return 0;
    }

    private bool RunZmodemTransfer(ReadOnlySpan<byte> lookahead, CancellationToken ct)
    {
        if (_shellStream == null) return false;

        DataReceived?.Invoke("\r\n[ZMODEM 传输中...]\r\n");

        // 设置读超时，防止 ShellStream.Read() 在服务端停止发送帧后永久阻塞
        int prevReadTimeout = Timeout.Infinite;
        try
        {
            if (_shellStream.CanTimeout)
            {
                prevReadTimeout = _shellStream.ReadTimeout;
                _shellStream.ReadTimeout = 30_000; // 30 秒单次读超时
            }
        }
        catch { }

        var receivedFiles = new List<(string Name, byte[] Data)>();
        bool didTransfer = false;
        byte[] unconsumed = Array.Empty<byte>(); // 记录残留数据
        try
        {
            var xfer = new ZmodemTransfer(_shellStream, lookahead);
            xfer.StatusChanged       = msg  => ZmodemStatus?.Invoke(msg);
            xfer.FileReceived        = (n, d) => receivedFiles.Add((n, d));
            xfer.RequestUploadFile   = ZmodemRequestUpload;
            xfer.CheckDataAvailable  = () => _shellStream.DataAvailable;

            didTransfer = xfer.Run(ct);
            // 关键修复：把多读的 Shell 提示符拿回来
            unconsumed = xfer.GetUnconsumedData();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("zmodem transfer error", ex);
            DataReceived?.Invoke($"\r\n[ZMODEM 错误：{ex.Message}]\r\n");
            didTransfer = true;
        }
        finally
        {
            try
            {
                if (_shellStream.CanTimeout)
                    _shellStream.ReadTimeout = prevReadTimeout;
            }
            catch { }
        }

        if (didTransfer)
        {
            DataReceived?.Invoke("\r\n[ZMODEM 传输结束]\r\n");
            foreach (var (name, data) in receivedFiles)
                ZmodemFileReceived?.Invoke(name, data);

            // 把终端原本的字符显示出来
            if (unconsumed.Length > 0)
            {
                DataReceived?.Invoke(Encoding.UTF8.GetString(unconsumed));
            }
        }
        return didTransfer;
    }

    // ── Telnet ────────────────────────────────────────────────────────────────

    // 自动登录凭据
    private string _telnetUsername = "";
    private string _telnetPassword = "";
    private readonly TelnetProtocolHandler _telnetProtocol = new();

    private async Task ConnectTelnetAsync(ServerInfo server, SocksProxyEntry? proxy, CancellationToken timeoutCt)
    {
        _telnetUsername = server.Username ?? "";
        _telnetPassword = server.Password ?? "";
        _telnetProtocol.Reset();

        if (proxy is { Host.Length: > 0 })
        {
            _telnetTunnel = await Socks5Helper.ConnectAsync(proxy, server.IP, server.Port, timeoutCt)
                .ConfigureAwait(false);
            _telnetClient = _telnetTunnel.Client;
            _telnetStream = _telnetTunnel.Stream;
        }
        else
        {
            _telnetClient = new TcpClient();
            await _telnetClient.ConnectAsync(server.IP, server.Port, timeoutCt).ConfigureAwait(false);
            _telnetStream = _telnetClient.GetStream();
        }
        _telnetClient.NoDelay = true; // 禁用 Nagle，确保退格键等单字节交互数据立即发送

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

                // 增量处理 Telnet IAC 协商，避免 TCP 分片导致协商状态丢失
                var (printable, response) = _telnetProtocol.Process(buffer, count);
                if (response.Count > 0)
                {
                    var resp = response.ToArray();
                    _telnetStream.Write(resp, 0, resp.Length);
                }
                if (_telnetProtocol.ConsumeWindowSizeRequest())
                {
                    SendTelnetWindowSize();
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
                             ContainsIgnoreCase(recent, "user:") ||
                             ContainsIgnoreCase(recent, "用户名")))
                        {
                            SendInput(_telnetUsername + "\r\n");
                            autoState = string.IsNullOrEmpty(_telnetPassword) ? 2 : 1;
                            promptBuf.Clear();
                        }
                        else if (autoState == 1 &&
                                 (ContainsIgnoreCase(recent, "password:") ||
                                  ContainsIgnoreCase(recent, "passwd:") ||
                                  ContainsIgnoreCase(recent, "密码")))
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
                // 将 \x7f (DEL/xterm 默认退格) 映射为 \x08 (BS/Ctrl+H)，
                // 确保 Cisco/Huawei 等网络设备无论经过何种路径（直连或 SOCKS 跳转）
                // 都能正确识别退格键（许多设备 VTY 线路只接受 \x08）。
                data = data.Replace('\x7f', '\x08');
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
        _terminalCols = Math.Max(1, cols);
        _terminalRows = Math.Max(1, rows);

        try
        {
            _shellStream?.ChangeWindowSize((uint)_terminalCols, (uint)_terminalRows, 0, 0);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ssh resize ignored: {ex.Message}");
        }

        if (_telnetProtocol.CanSendWindowSize)
            SendTelnetWindowSize();
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
        try { _telnetTunnel?.Dispose(); } catch { }
        try { _telnetClient?.Dispose(); } catch { }
    }

    private void SendTelnetWindowSize()
    {
        if (_telnetStream == null || !_telnetProtocol.CanSendWindowSize)
            return;

        try
        {
            var bytes = TelnetProtocolHandler.CreateWindowSizeCommand(_terminalCols, _terminalRows);
            _telnetStream.Write(bytes, 0, bytes.Length);
            _telnetStream.Flush();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"telnet resize ignored: {ex.Message}");
        }
    }

    /// <summary>
    /// 增量式 Telnet 协议解析器，正确处理跨包 IAC/WILL/DO/SB/SE 与 IAC IAC。
    /// </summary>
    private sealed class TelnetProtocolHandler
    {
        private const byte IAC  = 0xFF;
        private const byte WILL = 0xFB;
        private const byte WONT = 0xFC;
        private const byte DO   = 0xFD;
        private const byte DONT = 0xFE;
        private const byte SB   = 0xFA;
        private const byte SE   = 0xF0;
        private const byte SGA  = 0x03;
        private const byte ECHO = 0x01;
        private const byte NAWS = 0x1F;

        private State _state = State.Data;
        private byte _pendingCommand;
        private bool _windowSizePending;

        public bool CanSendWindowSize { get; private set; }

        private enum State
        {
            Data,
            SawIac,
            NeedOption,
            SubNegotiation,
            SubNegotiationSawIac
        }

        public void Reset()
        {
            _state = State.Data;
            _pendingCommand = 0;
            _windowSizePending = false;
            CanSendWindowSize = false;
        }

        public (List<byte> printable, List<byte> response) Process(byte[] buf, int count)
        {
            var printable = new List<byte>(count);
            var response = new List<byte>();

            for (int i = 0; i < count; i++)
            {
                byte b = buf[i];
                switch (_state)
                {
                    case State.Data:
                        if (b == IAC)
                            _state = State.SawIac;
                        else
                            printable.Add(b);
                        break;

                    case State.SawIac:
                        if (b == IAC)
                        {
                            printable.Add(IAC);
                            _state = State.Data;
                        }
                        else if (b is WILL or WONT or DO or DONT)
                        {
                            _pendingCommand = b;
                            _state = State.NeedOption;
                        }
                        else if (b == SB)
                        {
                            _state = State.SubNegotiation;
                        }
                        else
                        {
                            _state = State.Data;
                        }
                        break;

                    case State.NeedOption:
                        AppendNegotiationReply(response, _pendingCommand, b);
                        _pendingCommand = 0;
                        _state = State.Data;
                        break;

                    case State.SubNegotiation:
                        if (b == IAC)
                            _state = State.SubNegotiationSawIac;
                        break;

                    case State.SubNegotiationSawIac:
                        _state = b == SE ? State.Data : State.SubNegotiation;
                        break;
                }
            }

            return (printable, response);
        }

        public bool ConsumeWindowSizeRequest()
        {
            if (!_windowSizePending)
                return false;
            _windowSizePending = false;
            return true;
        }

        public static byte[] CreateWindowSizeCommand(int cols, int rows)
        {
            cols = Math.Clamp(cols, 1, 65535);
            rows = Math.Clamp(rows, 1, 65535);

            var bytes = new List<byte>(9) { IAC, SB, NAWS };
            AppendEscapedByte(bytes, (byte)(cols >> 8));
            AppendEscapedByte(bytes, (byte)cols);
            AppendEscapedByte(bytes, (byte)(rows >> 8));
            AppendEscapedByte(bytes, (byte)rows);
            bytes.Add(IAC);
            bytes.Add(SE);
            return bytes.ToArray();
        }

        private void AppendNegotiationReply(List<byte> response, byte command, byte option)
        {
            switch (command)
            {
                case WILL:
                    response.AddRange(option is ECHO or SGA ? [IAC, DO, option] : [IAC, DONT, option]);
                    break;
                case DO:
                    if (option == SGA || option == NAWS)
                    {
                        response.AddRange([IAC, WILL, option]);
                        if (option == NAWS)
                        {
                            CanSendWindowSize = true;
                            _windowSizePending = true;
                        }
                    }
                    else
                    {
                        response.AddRange([IAC, WONT, option]);
                    }
                    break;
                case WONT:
                case DONT:
                    if (option == NAWS)
                    {
                        CanSendWindowSize = false;
                        _windowSizePending = false;
                    }
                    break;
            }
        }

        private static void AppendEscapedByte(List<byte> buffer, byte value)
        {
            buffer.Add(value);
            if (value == IAC)
                buffer.Add(IAC);
        }
    }
}
