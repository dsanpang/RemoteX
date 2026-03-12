using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace RemoteX;

/// <summary>
/// ZMODEM 文件传输协议实现。
/// 支持接收（服务端 sz → 客户端下载）和发送（服务端 rz ← 客户端上传）。
/// 直接操作 SSH ShellStream，不经过 xterm.js。
/// </summary>
internal sealed class ZmodemTransfer
{
    // ── 协议常量 ──────────────────────────────────────────────────────────────

    private const byte ZPAD   = 0x2A;
    private const byte ZDLESC = 0x18;
    private const byte ZBIN   = 0x41; // binary header CRC-16
    private const byte ZHEX   = 0x42; // hex header
    private const byte ZBIN32 = 0x43; // binary header CRC-32
    private const byte XON    = 0x11;
    private const byte XOFF   = 0x13;

    private const byte ZRQINIT = 0;
    private const byte ZRINIT  = 1;
    private const byte ZSINIT  = 2;
    private const byte ZACK    = 3;
    private const byte ZFILE   = 4;
    private const byte ZSKIP   = 5;
    private const byte ZNACK   = 6;
    private const byte ZABORT  = 7;
    private const byte ZFIN    = 8;
    private const byte ZRPOS   = 9;
    private const byte ZDATA   = 10;
    private const byte ZEOF    = 11;
    private const byte ZFERR   = 12;
    private const byte ZCAN    = 16;

    private const byte ZCRCE = 0x68; // subpacket ends, header follows
    private const byte ZCRCG = 0x69; // subpacket continues, no ACK
    private const byte ZCRCQ = 0x6A; // subpacket continues, ACK expected
    private const byte ZCRCW = 0x6B; // subpacket ends, ACK expected

    private const byte CANFDX  = 0x01;
    private const byte CANOVIO = 0x02;
    private const byte CANFC32 = 0x20;

    private const int ChunkSize = 8192;

    // ── 内部状态 ──────────────────────────────────────────────────────────────

    private readonly Stream       _stream;
    private readonly Queue<byte>  _pushback = new();
    private bool _useCrc32 = true; // lrzsz 默认使用 CRC-32

    // ── 外部回调（由 TerminalSessionService 注入）──────────────────────────────

    /// <summary>传输进度文本（在 UI 线程外调用）。</summary>
    public Action<string>? StatusChanged;

    /// <summary>下载完成：文件名 + 数据，需在 UI 线程弹 SaveFileDialog。</summary>
    public Action<string, byte[]>? FileReceived;

    /// <summary>上传：弹 OpenFileDialog，返回 (文件名, 数据)；取消则抛 OCE。</summary>
    public Func<Task<(string Name, byte[] Data)>>? RequestUploadFile;

    // ── 构造 ──────────────────────────────────────────────────────────────────

    /// <param name="stream">SSH ShellStream</param>
    /// <param name="lookahead">已从流中读取但尚未处理的字节</param>
    public ZmodemTransfer(Stream stream, ReadOnlySpan<byte> lookahead)
    {
        _stream = stream;
        foreach (var b in lookahead)
            _pushback.Enqueue(b);
    }

    // ── 公开入口 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 运行一次完整的 ZMODEM 会话（接收 or 发送，由服务端第一帧决定）。
    /// 在后台线程调用。
    /// </summary>
    public void Run(CancellationToken ct)
    {
        try
        {
            var (frameType, _) = ReadHeader(ct);

            if (frameType == ZRQINIT)
                RunReceive(ct);          // 服务端 sz → 我们下载
            else if (frameType == ZRINIT)
                RunSend(ct);             // 服务端 rz → 我们上传
            else
                SendAbort();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"ZMODEM 错误：{ex.Message}");
            try { SendAbort(); } catch { }
        }
    }

    // ── 接收流程（服务端 sz） ─────────────────────────────────────────────────

    private void RunReceive(CancellationToken ct)
    {
        SendZrinit(ct);

        while (!ct.IsCancellationRequested)
        {
            var (type, data) = ReadHeader(ct);

            switch (type)
            {
                case ZFILE:
                    HandleZfile(data, ct);
                    break;
                case ZFIN:
                    SendHexHeader(ZFIN, 0, ct);
                    return;
                case ZRINIT:
                    // 多文件：继续等待
                    break;
                default:
                    break;
            }
        }
    }

    private void HandleZfile(uint _, CancellationToken ct)
    {
        // ZFILE 数据子包：filename\0size\0...
        var (subData, _) = ReadDataSubpacket(ct);
        var nulPos = Array.IndexOf(subData, (byte)0);
        var filename = nulPos >= 0
            ? Encoding.UTF8.GetString(subData, 0, nulPos)
            : "downloaded_file";

        long fileSize = 0;
        if (nulPos >= 0 && nulPos + 1 < subData.Length)
        {
            var meta = Encoding.ASCII.GetString(subData, nulPos + 1,
                subData.Length - nulPos - 1);
            var parts = meta.Split(' ', 2);
            long.TryParse(parts[0], out fileSize);
        }

        StatusChanged?.Invoke($"接收文件：{filename}（{FormatSize(fileSize)}）");

        // 发送 ZRPOS(0) 告知从头接收
        SendHexHeader(ZRPOS, 0, ct);

        // 接收文件数据
        var ms = new MemoryStream(fileSize > 0 ? (int)Math.Min(fileSize, 64 * 1024 * 1024) : 65536);
        long received = 0;
        bool done = false;

        while (!done && !ct.IsCancellationRequested)
        {
            var (hdrType, hdrData) = ReadHeader(ct);
            if (hdrType == ZDATA)
            {
                while (!done)
                {
                    var (chunk, endType) = ReadDataSubpacket(ct);
                    ms.Write(chunk, 0, chunk.Length);
                    received += chunk.Length;

                    if (fileSize > 0)
                        StatusChanged?.Invoke(
                            $"接收：{filename}  {FormatSize(received)} / {FormatSize(fileSize)}");

                    if (endType == ZCRCE || endType == ZCRCW)
                    {
                        done = true;
                    }
                    else if (endType == ZCRCQ)
                    {
                        SendHexHeader(ZACK, (uint)received, ct);
                    }
                    // ZCRCG: continue without ACK
                }
            }
            else if (hdrType == ZEOF)
            {
                done = true;
            }
            else if (hdrType == ZABORT || hdrType == ZFIN || hdrType == ZCAN)
            {
                SendAbort();
                return;
            }
        }

        var fileData = ms.ToArray();
        StatusChanged?.Invoke($"文件接收完成：{filename}（{FormatSize(fileData.Length)}）");
        FileReceived?.Invoke(filename, fileData);

        // 告知服务端准备下一文件
        SendZrinit(ct);
    }

    // ── 发送流程（服务端 rz） ─────────────────────────────────────────────────

    private void RunSend(CancellationToken ct)
    {
        if (RequestUploadFile == null)
        {
            StatusChanged?.Invoke("未配置文件上传处理器");
            SendAbort();
            return;
        }

        // 检查服务端是否支持 CRC-32
        // ZRINIT data byte[0] = flags, byte[2..3] = buffer size
        // CANFC32 = 0x20 in flags[0]
        // （我们已经消费了 ZRINIT header，flags 在 data 低字节）

        Task<(string Name, byte[] Data)> fileTask = RequestUploadFile();
        fileTask.Wait(ct);
        var (fileName, fileData) = fileTask.Result;

        StatusChanged?.Invoke($"发送文件：{fileName}（{FormatSize(fileData.Length)}）");

        // 发送 ZFILE 帧
        SendZfile(fileName, fileData.Length, ct);

        // 等待 ZRPOS
        var (respType, _) = ReadHeader(ct);
        if (respType == ZSKIP || respType == ZABORT)
        {
            StatusChanged?.Invoke("服务端拒绝接收文件");
            return;
        }

        // 发送数据
        SendFileData(fileData, ct);

        // 等待服务端确认
        var (finType, _) = ReadHeader(ct);
        if (finType == ZRINIT)
        {
            // 成功：发送 ZFIN
            SendHexHeader(ZFIN, 0, ct);
            // 等待服务端 ZFIN
            try { ReadHeader(ct); } catch { }
        }

        StatusChanged?.Invoke($"文件发送完成：{fileName}");
    }

    private void SendZfile(string filename, long fileSize, CancellationToken ct)
    {
        // ZFILE header with offset=0
        var hdr = BuildBinHeader32(ZFILE, 0);
        _stream.Write(hdr, 0, hdr.Length);

        // ZFILE data subpacket: filename\0size_decimal\0
        var meta = Encoding.ASCII.GetBytes($"{filename}\0{fileSize} 0 0 0\0");
        var pkt  = BuildDataSubpacket(meta, 0, meta.Length, ZCRCW);
        _stream.Write(pkt, 0, pkt.Length);
        _stream.Flush();
    }

    private void SendFileData(byte[] data, CancellationToken ct)
    {
        int offset = 0;
        long total  = data.Length;

        while (offset < data.Length && !ct.IsCancellationRequested)
        {
            // Send ZDATA header with current offset
            var hdr = BuildBinHeader32(ZDATA, (uint)offset);
            _stream.Write(hdr, 0, hdr.Length);

            // Send chunks; use ZCRCQ for intermediate (expect ACK) and ZCRCW for last in block
            int chunkLen = Math.Min(ChunkSize, data.Length - offset);
            bool isLast  = offset + chunkLen >= data.Length;
            byte endType = isLast ? ZCRCW : ZCRCQ;

            var pkt = BuildDataSubpacket(data, offset, chunkLen, endType);
            _stream.Write(pkt, 0, pkt.Length);
            _stream.Flush();

            offset += chunkLen;

            if (!isLast)
            {
                // Wait for ZACK before sending next chunk
                var (ackType, _) = ReadHeader(ct);
                if (ackType == ZABORT) return;
            }

            StatusChanged?.Invoke(
                $"发送：{FormatSize(offset)} / {FormatSize(total)}");
        }

        // ZEOF
        SendHexHeader(ZEOF, (uint)data.Length, ct);
    }

    // ── Frame 读取 ────────────────────────────────────────────────────────────

    private (byte Type, uint Data) ReadHeader(CancellationToken ct)
    {
        int pads = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int b = ReadRaw(ct);
            if (b < 0) throw new EndOfStreamException("ZMODEM: stream ended");

            if (b == ZPAD) { pads++; continue; }

            if (pads >= 1 && b == ZDLESC)
            {
                int htype = ReadRaw(ct);
                if (htype < 0) throw new EndOfStreamException("ZMODEM: stream ended");

                return htype switch
                {
                    ZHEX   => ReadHexHeader(ct),
                    ZBIN   => ReadBinHeader(ct, crc32: false),
                    ZBIN32 => ReadBinHeader(ct, crc32: true),
                    _      => throw new InvalidDataException($"ZMODEM: unknown header type 0x{htype:X2}")
                };
            }

            pads = 0;
        }
    }

    private (byte Type, uint Data) ReadHexHeader(CancellationToken ct)
    {
        // Read 14 hex digits (skip CR/LF/XON mixed in)
        var hex = new byte[14];
        int pos = 0;
        while (pos < 14)
        {
            int b = ReadRaw(ct);
            if (b < 0) throw new EndOfStreamException();
            if (b == '\r' || b == '\n' || b == XON || b == XOFF) continue;
            hex[pos++] = (byte)b;
        }
        // Consume trailing CR LF XON (push back non-whitespace)
        while (true)
        {
            int b = ReadRaw(ct);
            if (b < 0) break;
            if (b != '\r' && b != '\n' && b != XON && b != XOFF) { Unget((byte)b); break; }
        }

        byte type = ParseHex2(hex, 0);
        byte d0   = ParseHex2(hex, 2);
        byte d1   = ParseHex2(hex, 4);
        byte d2   = ParseHex2(hex, 6);
        byte d3   = ParseHex2(hex, 8);
        uint data = (uint)(d0 | (d1 << 8) | (d2 << 16) | (d3 << 24));

        // Detect if server supports CRC-32 (from ZRINIT flags)
        if (type == ZRINIT) _useCrc32 = (d0 & CANFC32) != 0;

        return (type, data);
    }

    private (byte Type, uint Data) ReadBinHeader(CancellationToken ct, bool crc32)
    {
        int crcBytes = crc32 ? 4 : 2;
        var buf      = new byte[5 + crcBytes];
        for (int i = 0; i < buf.Length; i++)
        {
            int b = ReadEscaped(ct);
            if (b < 0) throw new EndOfStreamException();
            buf[i] = (byte)b;
        }
        uint data = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
        return (buf[0], data);
    }

    // ── Data 子包读取 ─────────────────────────────────────────────────────────

    private (byte[] Data, byte EndType) ReadDataSubpacket(CancellationToken ct)
    {
        var buf = new List<byte>(4096);
        while (true)
        {
            int b = ReadRaw(ct);
            if (b < 0) throw new EndOfStreamException();

            if (b == ZDLESC)
            {
                int b2 = ReadRaw(ct);
                if (b2 < 0) throw new EndOfStreamException();

                // Sub-packet end markers appear as raw bytes after ZDLESC
                if (b2 == ZCRCE || b2 == ZCRCG || b2 == ZCRCQ || b2 == ZCRCW)
                {
                    // Skip CRC (4 bytes, possibly escaped)
                    int crcLen = _useCrc32 ? 4 : 2;
                    for (int i = 0; i < crcLen; i++) ReadEscaped(ct);
                    return (buf.ToArray(), (byte)b2);
                }
                buf.Add((byte)(b2 ^ 0x40));
            }
            else if (b == XON || b == XOFF || b == (XON | 0x80) || b == (XOFF | 0x80))
            {
                continue;
            }
            else
            {
                buf.Add((byte)b);
            }
        }
    }

    // ── Frame 构建 ────────────────────────────────────────────────────────────

    private void SendZrinit(CancellationToken ct)
    {
        // Flags: CANFDX | CANOVIO | CANFC32 = 0x23
        uint flags = CANFDX | CANOVIO | CANFC32;
        SendHexHeader(ZRINIT, flags, ct);
    }

    private void SendHexHeader(byte type, uint data, CancellationToken ct)
    {
        var hdr = new byte[5]
        {
            type,
            (byte)(data & 0xFF),
            (byte)((data >> 8) & 0xFF),
            (byte)((data >> 16) & 0xFF),
            (byte)((data >> 24) & 0xFF)
        };
        var crc = Crc16(hdr);

        var sb = new StringBuilder(32);
        sb.Append("**\x18B");
        foreach (var b in hdr)          sb.Append(b.ToString("x2"));
        sb.Append(((byte)(crc >> 8)).ToString("x2"));
        sb.Append(((byte)(crc & 0xFF)).ToString("x2"));
        sb.Append("\r\n\x11");

        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    private static byte[] BuildBinHeader32(byte type, uint data)
    {
        var hdr = new byte[5]
        {
            type,
            (byte)(data & 0xFF),
            (byte)((data >> 8) & 0xFF),
            (byte)((data >> 16) & 0xFF),
            (byte)((data >> 24) & 0xFF)
        };
        var crc = Crc32(hdr);

        var buf = new List<byte>(16);
        buf.Add(ZPAD);
        buf.Add(ZDLESC);
        buf.Add(ZBIN32);
        foreach (var b in hdr) AddEscaped(buf, b);
        AddEscaped(buf, (byte)(crc & 0xFF));
        AddEscaped(buf, (byte)((crc >> 8) & 0xFF));
        AddEscaped(buf, (byte)((crc >> 16) & 0xFF));
        AddEscaped(buf, (byte)((crc >> 24) & 0xFF));
        return buf.ToArray();
    }

    private static byte[] BuildDataSubpacket(byte[] data, int offset, int count, byte endType)
    {
        var buf = new List<byte>(count + 16);

        uint crc = 0xFFFFFFFF;
        for (int i = offset; i < offset + count; i++)
        {
            crc = UpdateCrc32(crc, data[i]);
            AddEscaped(buf, data[i]);
        }
        crc = UpdateCrc32(crc, endType);
        crc ^= 0xFFFFFFFF;

        buf.Add(ZDLESC);
        buf.Add(endType);
        AddEscaped(buf, (byte)(crc & 0xFF));
        AddEscaped(buf, (byte)((crc >> 8) & 0xFF));
        AddEscaped(buf, (byte)((crc >> 16) & 0xFF));
        AddEscaped(buf, (byte)((crc >> 24) & 0xFF));
        return buf.ToArray();
    }

    private void SendAbort()
    {
        try
        {
            var cancel = new byte[] { 0x18, 0x18, 0x18, 0x18, 0x18, 0x08, 0x08, 0x08, 0x08, 0x08 };
            _stream.Write(cancel, 0, cancel.Length);
            _stream.Flush();
        }
        catch { }
    }

    // ── 底层读取 ──────────────────────────────────────────────────────────────

    private int ReadRaw(CancellationToken ct)
    {
        if (_pushback.Count > 0) return _pushback.Dequeue();
        ct.ThrowIfCancellationRequested();
        var buf = new byte[1];
        int n = _stream.Read(buf, 0, 1);
        return n == 0 ? -1 : buf[0];
    }

    private int ReadEscaped(CancellationToken ct)
    {
        while (true)
        {
            int b = ReadRaw(ct);
            if (b < 0) return -1;
            if (b == XON || b == XOFF || b == (XON | 0x80) || b == (XOFF | 0x80)) continue;
            if (b != ZDLESC) return b;
            int b2 = ReadRaw(ct);
            if (b2 < 0) return -1;
            return b2 ^ 0x40;
        }
    }

    private void Unget(byte b) => _pushback.Enqueue(b);

    // ── 工具方法 ──────────────────────────────────────────────────────────────

    private static void AddEscaped(List<byte> buf, byte b)
    {
        if (b == ZDLESC || b == XON || b == XOFF ||
            b == (byte)(XON | 0x80) || b == (byte)(XOFF | 0x80))
        {
            buf.Add(ZDLESC);
            buf.Add((byte)(b ^ 0x40));
        }
        else
        {
            buf.Add(b);
        }
    }

    private static byte ParseHex2(byte[] data, int offset)
    {
        static int H(byte c) => c >= '0' && c <= '9' ? c - '0' :
                                 c >= 'a' && c <= 'f' ? c - 'a' + 10 :
                                 c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;
        return (byte)((H(data[offset]) << 4) | H(data[offset + 1]));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024           => $"{bytes} B",
        < 1024 * 1024    => $"{bytes / 1024.0:F1} KB",
        _                => $"{bytes / 1024.0 / 1024.0:F1} MB"
    };

    // ── CRC ──────────────────────────────────────────────────────────────────

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
            crc = ((crc << 8) ^ s_crc16Table[((crc >> 8) ^ b) & 0xFF]) & 0xFFFF;
        return (ushort)crc;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = UpdateCrc32(crc, b);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint UpdateCrc32(uint crc, byte b)
        => (crc >> 8) ^ s_crc32Table[(crc ^ b) & 0xFF];

    private static readonly uint[] s_crc16Table = BuildCrc16Table();
    private static readonly uint[] s_crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc16Table()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 8;
            for (int j = 0; j < 8; j++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1;
            t[i] = c & 0xFFFF;
        }
        return t;
    }

    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
