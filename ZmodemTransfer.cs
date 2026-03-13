using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteX;

/// <summary>
/// ZMODEM 文件传输协议实现。
/// 发送侧使用保守的 16-bit CRC，接收侧兼容 32-bit CRC；直接操作 SSH ShellStream。
/// </summary>
internal sealed class ZmodemTransfer
{
    private const byte ZPAD   = 0x2A;
    private const byte ZDLESC = 0x18;
    private const byte ZBIN   = 0x41;
    private const byte ZHEX   = 0x42;
    private const byte ZBIN32 = 0x43;
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

    private const byte ZCRCE = 0x68;
    private const byte ZCRCG = 0x69;
    private const byte ZCRCQ = 0x6A;
    private const byte ZCRCW = 0x6B;

    private const byte ZRUB0 = 0x6C;
    private const byte ZRUB1 = 0x6D;

    private const byte CANFDX  = 0x01;
    private const byte CANOVIO = 0x02;
    private const byte CANFC32 = 0x20;
    private const byte ESCCTL  = 0x40;

    private const int ChunkSize = 4096; // 4KB 块大小（lrzsz 默认 8KB，保守取 4KB）

    private readonly Stream       _stream;
    private readonly Queue<byte>  _pushback = new();
    private bool _useCrc32 = false;
    private string _waitReason = "等待 ZMODEM 数据";

    private readonly byte[] _readBuf = new byte[4096];
    private int _readBufPos;
    private int _readBufLen;

    public Func<bool>? CheckDataAvailable;
    private const int ReadPollMs    = 50;
    private const int ReadTimeoutMs = 15_000;

    public Action<string>? StatusChanged;
    public Action<string, byte[]>? FileReceived;
    public Func<Task<(string Name, byte[] Data)>>? RequestUploadFile;

    public ZmodemTransfer(Stream stream, ReadOnlySpan<byte> lookahead)
    {
        _stream = stream;
        foreach (var b in lookahead) _pushback.Enqueue(b);
    }

    public bool Run(CancellationToken ct)
    {
        try
        {
            var (frameType, frameData) = ReadHeader(ct);

            if (frameType == ZFIN)
            {
                SendHexHeader(ZFIN, 0, ct);
                return false;
            }

            if (frameType == ZRQINIT || frameType == ZFILE)
            {
                RunReceive(ct, frameType, frameData);
            }
            else if (frameType == ZRINIT)
            {
                RunSend(ct);
            }
            else
            {
                SendAbort();
            }

            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            AppLogger.Error("zmodem protocol error", ex);
            StatusChanged?.Invoke($"ZMODEM 错误：{ex.Message}");
            try { SendAbort(); } catch { }
            return true;
        }
    }

    // ── 接收流程（服务端 sz） ─────────────────────────────────────────────────

    private void RunReceive(CancellationToken ct, byte firstType, uint firstData)
    {
        if (firstType == ZFILE)
            HandleZfile(firstData, ct);
        else
            SendZrinit(ct);

        while (!ct.IsCancellationRequested)
        {
            _waitReason = "等待发送端下一帧（ZFILE/ZSINIT/ZFIN）";
            var (type, data) = ReadHeader(ct);
            switch (type)
            {
                case ZFILE: HandleZfile(data, ct); break;
                case ZSINIT:
                    try { ReadDataSubpacket(ct); } catch { }
                    SendHexHeader(ZACK, 0, ct);
                    break;
                case ZFIN: SendHexHeader(ZFIN, 0, ct); return;
                case ZRQINIT: SendZrinit(ct); break;
                case ZRINIT:
                case ZEOF: break;
            }
        }
    }

    private void HandleZfile(uint _, CancellationToken ct)
    {
        var (subData, _) = ReadDataSubpacket(ct);
        var nulPos = Array.IndexOf(subData, (byte)0);
        var filename = nulPos >= 0 ? Encoding.UTF8.GetString(subData, 0, nulPos) : "downloaded_file";

        long fileSize = 0;
        if (nulPos >= 0 && nulPos + 1 < subData.Length)
        {
            var meta = Encoding.ASCII.GetString(subData, nulPos + 1, subData.Length - nulPos - 1);
            long.TryParse(meta.Split(' ', 2)[0], out fileSize);
        }

        StatusChanged?.Invoke($"接收文件：{filename}（{FormatSize(fileSize)}）");
        SendHexHeader(ZRPOS, 0, ct);

        var ms = new MemoryStream(fileSize > 0 ? (int)Math.Min(fileSize, 64 * 1024 * 1024) : 65536);
        long received = 0;

        while (!ct.IsCancellationRequested)
        {
            _waitReason = $"等待文件数据或 ZEOF（{filename}）";
            var (hdrType, _) = ReadHeader(ct);

            if (hdrType == ZDATA)
            {
                bool blockDone = false;
                while (!blockDone && !ct.IsCancellationRequested)
                {
                    var (chunk, endType) = ReadDataSubpacket(ct);
                    ms.Write(chunk, 0, chunk.Length);
                    received += chunk.Length;

                    if (fileSize > 0)
                        StatusChanged?.Invoke($"接收：{filename}  {FormatSize(received)} / {FormatSize(fileSize)}");

                    switch (endType)
                    {
                        case ZCRCE: 
                            blockDone = true; 
                            break;
                        case ZCRCW:
                            SendHexHeader(ZACK, (uint)received, ct); 
                            blockDone = true; 
                            break;
                        case ZCRCQ: 
                            SendHexHeader(ZACK, (uint)received, ct); 
                            break;
                        case ZCRCG: 
                            break;
                    }
                }
            }
            else if (hdrType == ZEOF)
            {
                var fileData = ms.ToArray();
                StatusChanged?.Invoke($"文件接收完成：{filename}（{FormatSize(fileData.Length)}）");
                FileReceived?.Invoke(filename, fileData);
                SendZrinit(ct);
                return;
            }
            else if (hdrType == ZABORT || hdrType == ZCAN) { SendAbort(); return; }
            else if (hdrType == ZFIN) { SendHexHeader(ZFIN, 0, ct); return; }
        }
    }

    // ── 发送流程（服务端 rz） ─────────────────────────────────────────────────

    private void RunSend(CancellationToken ct)
    {
        if (RequestUploadFile == null) { SendAbort(); return; }

        string fileName; byte[] fileData;
        try
        {
            var fileTask = RequestUploadFile();
            fileTask.Wait(ct);
            (fileName, fileData) = fileTask.Result;
        }
        catch { SendAbort(); return; }

        StatusChanged?.Invoke($"发送文件：{fileName}（{FormatSize(fileData.Length)}）");

        SendZfile16(fileName, fileData.Length, ct);

        // ── 阶段一：等待 ZRPOS ────────────────────────────────────────────────
        // lrzsz 在等待我们发 ZFILE 期间会反复重传 ZRINIT；这些旧帧积压在
        // ShellStream 缓冲区，发完 ZFILE 后必须全部丢弃，只保留 ZRPOS。
        uint startOffset = 0;
        while (!ct.IsCancellationRequested)
        {
            _waitReason = "等待服务端响应 ZRPOS/ZSKIP（上传准备阶段）";
            var (r, d) = ReadHeader(ct);
            if (r == ZRPOS)  { startOffset = d; break; }
            if (r == ZSKIP || r == ZABORT || r == ZFERR)
            {
                StatusChanged?.Invoke("服务端拒绝接收文件或中止了传输。");
                return;
            }
            // ZRINIT（重传）、ZACK 等均忽略
        }

        // ── 阶段二：发送数据，等待最终结果 ───────────────────────────────────
        // 只有在此处（已成功发出文件数据并收到 ZEOF 响应之后）收到的 ZRINIT
        // 才代表传输完成，不会与阶段一的残留重传帧混淆。
        if (startOffset > 0)
            StatusChanged?.Invoke($"服务端要求断点续传（偏移 {FormatSize(startOffset)}）");

        SendFileData16(fileData, startOffset, ct);

        bool waitingForFinishAck = false;
        while (!ct.IsCancellationRequested)
        {
            _waitReason = waitingForFinishAck
                ? "等待服务端响应 ZFIN/ZRPOS（上传收尾阶段）"
                : "等待服务端响应 ZRINIT/ZRPOS（上传完成确认）";
            var (r, d) = ReadHeader(ct);

            if (waitingForFinishAck)
            {
                if (r == ZFIN)
                {
                    SendOverAndOut();
                    StatusChanged?.Invoke($"文件发送完成：{fileName}");
                    return;
                }

                if (r == ZRINIT)
                {
                    // 对端还在等待会话关闭，重发 ZFIN 即可。
                    SendHexHeader(ZFIN, 0, ct);
                    continue;
                }

                if (r == ZRPOS)
                {
                    waitingForFinishAck = false;
                    startOffset = d;
                    StatusChanged?.Invoke($"重传（偏移 {FormatSize(startOffset)}）");
                    SendFileData16(fileData, startOffset, ct);
                    continue;
                }

                if (r == ZSKIP || r == ZABORT || r == ZFERR || r == ZCAN)
                {
                    StatusChanged?.Invoke("服务端在收尾阶段中止了传输。");
                    return;
                }

                // ZACK 等收尾期间的中途信息忽略
                continue;
            }

            if (r == ZRINIT)
            {
                // 文件主体已经确认完成，继续走 ZFIN -> ZFIN -> OO 收尾，
                // 否则 lrzsz rz 会将文件视为异常接收并执行 removed 清理。
                waitingForFinishAck = true;
                SendHexHeader(ZFIN, 0, ct);
                continue;
            }

            if (r == ZRPOS)
            {
                // CRC 校验失败，从指定偏移重传
                startOffset = d;
                StatusChanged?.Invoke($"重传（偏移 {FormatSize(startOffset)}）");
                SendFileData16(fileData, startOffset, ct);
            }
            else if (r == ZSKIP || r == ZABORT || r == ZFERR)
            {
                StatusChanged?.Invoke("服务端拒绝接收文件或中止了传输。");
                return;
            }
            // ZACK 等中途信息忽略
        }
    }

    private void SendZfile16(string filename, long fileSize, CancellationToken ct)
    {
        // 与 lrzsz 默认行为保持一致：ZFILE 的 ZF0/ZF1/ZF2/ZF3 先全部置 0，
        // 由接收端按本地策略决定；多数实现会把 0 视为普通二进制传输。
        uint hdrData = MakeHeaderData();
        var hdr = BuildBinHeader16(ZFILE, hdrData);
        _stream.Write(hdr, 0, hdr.Length);

        var nameBytes = Encoding.UTF8.GetBytes(filename);
        var sizeBytes = Encoding.ASCII.GetBytes($"{fileSize} 0 0 0 0");
        var meta      = new byte[nameBytes.Length + 1 + sizeBytes.Length + 1];
        Array.Copy(nameBytes, 0, meta, 0, nameBytes.Length);
        Array.Copy(sizeBytes, 0, meta, nameBytes.Length + 1, sizeBytes.Length);
        var pkt = BuildDataSubpacket16(meta, 0, meta.Length, ZCRCW);
        _stream.Write(pkt, 0, pkt.Length);
        _stream.Flush();
    }

    private void SendFileData16(byte[] data, uint startOffset, CancellationToken ct)
    {
        int total = data.Length;
        int offset = (int)Math.Min((long)startOffset, total);

        if (offset >= total)
        {
            SendBinHeader16(ZEOF, (uint)total);
            return;
        }

        // 发送 ZDATA 帧头（包含起始偏移），告知接收方从哪里开始
        var hdr = BuildBinHeader16(ZDATA, (uint)offset);
        _stream.Write(hdr, 0, hdr.Length);

        // ZCRCG 流式传输：中间块不等 ACK（与 lrzsz sz 的标准行为一致）；
        // 最后块用 ZCRCE（"帧头紧随"，无 ACK）。
        // 这样整个文件数据一次性发出，速度快，且 lrzsz rz 验证完整数据 CRC 后
        // 再发 ZRINIT（成功）或 ZRPOS(N)（从偏移 N 重传）。
        while (offset < total && !ct.IsCancellationRequested)
        {
            int chunkLen = Math.Min(ChunkSize, total - offset);
            bool isLast  = offset + chunkLen >= total;
            byte endType = isLast ? ZCRCE : ZCRCG; // 最后块 ZCRCE；中间块 ZCRCG

            var pkt = BuildDataSubpacket16(data, offset, chunkLen, endType);
            _stream.Write(pkt, 0, pkt.Length);

            offset += chunkLen;
            StatusChanged?.Invoke($"发送：{FormatSize(offset)} / {FormatSize(total)}");
        }

        // 所有数据发完后立即 Flush，然后发 ZEOF（偏移 = 文件总大小）
        _stream.Flush();
        SendBinHeader16(ZEOF, (uint)total);
    }

    // ── Frame 读取 ────────────────────────────────────────────────────────────

    private (byte Type, uint Data) ReadHeader(CancellationToken ct)
    {
        int pads = 0;
        while (true)
        {
            int b = ReadRaw(ct);
            if (b == ZPAD) { pads++; continue; }
            if (pads >= 1 && b == ZDLESC)
            {
                int htype = ReadRaw(ct);
                if (htype == ZHEX)
                {
                    var header = ReadHexHeader(ct);
                    return header;
                }
                if (htype == ZBIN)
                {
                    _useCrc32 = false;
                    var header = ReadBinHeader(ct, false);
                    return header;
                }
                if (htype == ZBIN32)
                {
                    _useCrc32 = true;
                    var header = ReadBinHeader(ct, true);
                    return header;
                }
                throw new InvalidDataException($"未知头部 0x{htype:X2}");
            }
            pads = 0; 
        }
    }

    private (byte Type, uint Data) ReadHexHeader(CancellationToken ct)
    {
        var hex = new byte[14];
        int pos = 0;
        while (pos < 14)
        {
            int b = ReadRaw(ct);
            if (b == '\r' || b == '\n' || b == XON || b == XOFF) continue;
            hex[pos++] = (byte)b;
        }

        // 核心修复 1：绝对禁止使用 while(true) 去吞掉剩余换行符！由于服务端随时可能挂起等待，盲目去 ReadRaw 会导致永久死锁！
        // 任何残余在流中的垃圾换行符，将会在下一次 ReadHeader 寻找 ZPAD 的循环中被天然丢弃，这才是最安全的。

        byte type = ParseHex2(hex, 0);
        byte d0 = ParseHex2(hex, 2), d1 = ParseHex2(hex, 4), d2 = ParseHex2(hex, 6), d3 = ParseHex2(hex, 8);
        return (type, (uint)(d0 | (d1 << 8) | (d2 << 16) | (d3 << 24)));
    }

    private (byte Type, uint Data) ReadBinHeader(CancellationToken ct, bool crc32)
    {
        int crcBytes = crc32 ? 4 : 2;
        var buf = new byte[5 + crcBytes];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)ReadEscaped(ct);
        uint data = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
        return (buf[0], data);
    }

    private (byte[] Data, byte EndType) ReadDataSubpacket(CancellationToken ct)
    {
        var buf = new List<byte>(4096);
        while (true)
        {
            int b = ReadRaw(ct);
            if (b == ZDLESC)
            {
                int b2 = ReadRaw(ct);
                if (b2 == ZCRCE || b2 == ZCRCG || b2 == ZCRCQ || b2 == ZCRCW)
                {
                    int crcLen = _useCrc32 ? 4 : 2;
                    for (int i = 0; i < crcLen; i++) ReadEscaped(ct);
                    return (buf.ToArray(), (byte)b2);
                }
                if (b2 == ZRUB0) buf.Add(0x7F);
                else if (b2 == ZRUB1) buf.Add(0xFF);
                else buf.Add((byte)(b2 ^ 0x40));
            }
            else if (b == XON || b == XOFF || b == (XON | 0x80) || b == (XOFF | 0x80)) continue;
            else buf.Add((byte)b);
        }
    }

    // ── Frame 构建（发送侧统一使用稳定的 16-bit CRC）───────────────────────────

    private void SendZrinit(CancellationToken ct)
    {
        // 先尽量贴近 lrzsz 的默认能力位，避免用 ESCCTL 把对端带进更激进的转义路径。
        uint flags = CANFDX | CANOVIO | CANFC32;
        SendHexHeader(ZRINIT, MakeHeaderData(zf0: (byte)flags), ct);
    }

    private void SendHexHeader(byte type, uint data, CancellationToken ct)
    {
        byte frameType = (byte)(type & 0x7F);
        var hdr = new byte[5] { frameType, (byte)(data & 0xFF), (byte)((data >> 8) & 0xFF), (byte)((data >> 16) & 0xFF), (byte)((data >> 24) & 0xFF) };
        ushort crc = FinalizeCrc16(Crc16(hdr));

        // 必须尽量按 lrzsz zshhdr 的字节序列发：
        // ** ZDLE ZHEX + 14 个 hex 字符 + CR + (LF|0x80) + 可选 XON。
        // 使用 0x8A 而不是普通 LF，可避免某些 PTY/line discipline 把 hex 头结尾改写。
        var bytes = new byte[22 + (type != ZACK && type != ZFIN ? 1 : 0)];
        int pos = 0;
        bytes[pos++] = ZPAD;
        bytes[pos++] = ZPAD;
        bytes[pos++] = ZDLESC;
        bytes[pos++] = ZHEX;
        pos = AppendHexByte(bytes, pos, frameType);
        pos = AppendHexByte(bytes, pos, hdr[1]);
        pos = AppendHexByte(bytes, pos, hdr[2]);
        pos = AppendHexByte(bytes, pos, hdr[3]);
        pos = AppendHexByte(bytes, pos, hdr[4]);
        pos = AppendHexByte(bytes, pos, (byte)(crc >> 8));
        pos = AppendHexByte(bytes, pos, (byte)(crc & 0xFF));
        bytes[pos++] = 0x0D;
        bytes[pos++] = 0x8A;
        if (type != ZACK && type != ZFIN)
            bytes[pos++] = XON;

        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    private static byte[] BuildBinHeader16(byte type, uint data)
    {
        var hdr = new byte[5] { type, (byte)(data & 0xFF), (byte)((data >> 8) & 0xFF), (byte)((data >> 16) & 0xFF), (byte)((data >> 24) & 0xFF) };
        ushort crc = FinalizeCrc16(Crc16(hdr));

        var buf = new List<byte>(16) { ZPAD, ZDLESC, ZBIN };
        foreach (var b in hdr) AddEscaped(buf, b);
        AddEscaped(buf, (byte)(crc >> 8)); 
        AddEscaped(buf, (byte)(crc & 0xFF));
        return buf.ToArray();
    }

    private void SendBinHeader16(byte type, uint data)
    {
        var hdr = BuildBinHeader16(type, data);
        _stream.Write(hdr, 0, hdr.Length);
        _stream.Flush();
    }

    private static byte[] BuildDataSubpacket16(byte[] data, int offset, int count, byte endType)
    {
        var buf = new List<byte>(count + 24);
        ushort crc = 0;
        for (int i = offset; i < offset + count; i++)
        {
            crc = UpdateCrc16(crc, data[i]);
            AddEscaped(buf, data[i]);
        }
        crc = UpdateCrc16(crc, endType);
        crc = FinalizeCrc16(crc);

        buf.Add(ZDLESC);
        buf.Add(endType);  // endType (0x68-0x6B) は制御文字でないため raw で送出（lrzsz xsendline と同じ）
        AddEscaped(buf, (byte)(crc >> 8));
        AddEscaped(buf, (byte)(crc & 0xFF));
        if (endType == ZCRCW)
            buf.Add(XON);
        return buf.ToArray();
    }

    private void SendAbort()
    {
        try { _stream.Write(new byte[] { 0x18, 0x18, 0x18, 0x18, 0x18, 0x08, 0x08, 0x08, 0x08, 0x08 }, 0, 10); _stream.Flush(); } catch { }
    }

    private void SendOverAndOut()
    {
        try
        {
            _stream.Write(new byte[] { (byte)'O', (byte)'O' }, 0, 2);
            _stream.Flush();
        }
        catch
        {
            // 会话已结束时无需再向上抛，保留已有传输结果即可。
        }
    }

    // ── 底层读取 ──────────────────────────────────────────────────────────────

    private int ReadRaw(CancellationToken ct)
    {
        if (_pushback.Count > 0) return _pushback.Dequeue();
        ct.ThrowIfCancellationRequested();

        if (_readBufPos >= _readBufLen)
        {
            _readBufLen = ReadIntoBufferWithTimeout(ct);
            _readBufPos = 0;
            if (_readBufLen <= 0) throw new EndOfStreamException();
        }
        return _readBuf[_readBufPos++];
    }

    private int ReadIntoBufferWithTimeout(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (_stream.CanTimeout)
            {
                int prevTimeout = _stream.ReadTimeout;
                try
                {
                    _stream.ReadTimeout = ReadTimeoutMs;
                    return _stream.Read(_readBuf, 0, _readBuf.Length);
                }
                finally
                {
                    _stream.ReadTimeout = prevTimeout;
                }
            }

            if (CheckDataAvailable != null)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(ReadTimeoutMs);
                while (!CheckDataAvailable() && DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(ReadPollMs);
                }
                if (!CheckDataAvailable())
                    throw new EndOfStreamException($"ZMODEM 读取超时（{ReadTimeoutMs / 1000} 秒无响应，{_waitReason}）");
            }

            return _stream.Read(_readBuf, 0, _readBuf.Length);
        }
        catch (TimeoutException)
        {
            throw new EndOfStreamException($"ZMODEM 读取超时（{ReadTimeoutMs / 1000} 秒无响应，{_waitReason}）");
        }
        catch (IOException ex)
        {
            throw new EndOfStreamException($"读取错误 ({ex.Message})，{_waitReason}");
        }
    }

    private int ReadEscaped(CancellationToken ct)
    {
        while (true)
        {
            int b = ReadRaw(ct);
            if (b == XON || b == XOFF || b == (XON | 0x80) || b == (XOFF | 0x80)) continue;
            if (b != ZDLESC) return b;

            int b2 = ReadRaw(ct);
            if (b2 == ZCRCE || b2 == ZCRCG || b2 == ZCRCQ || b2 == ZCRCW) return b2;
            if (b2 == ZRUB0) return 0x7F;
            if (b2 == ZRUB1) return 0xFF;
            return b2 ^ 0x40;
        }
    }

    private void Unget(byte b) => _pushback.Enqueue(b);

    public byte[] GetUnconsumedData()
    {
        var list = new List<byte>();
        while (_pushback.Count > 0) list.Add(_pushback.Dequeue());
        for (int i = _readBufPos; i < _readBufLen; i++) list.Add(_readBuf[i]);
        _readBufPos = _readBufLen;
        return list.ToArray();
    }

    private static void AddEscaped(List<byte> buf, byte b)
    {
        if ((b & 0x60) == 0) { buf.Add(ZDLESC); buf.Add((byte)(b ^ 0x40)); }
        else if (b == 0x7F) { buf.Add(ZDLESC); buf.Add(ZRUB0); }
        else if (b == 0xFF) { buf.Add(ZDLESC); buf.Add(ZRUB1); }
        else buf.Add(b);
    }

    private static byte ParseHex2(byte[] data, int offset) => (byte)((H(data[offset]) << 4) | H(data[offset + 1]));
    private static int H(byte c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;
    private static int AppendHexByte(byte[] buffer, int offset, byte value)
    {
        const string digits = "0123456789abcdef";
        buffer[offset++] = (byte)digits[(value >> 4) & 0x0F];
        buffer[offset++] = (byte)digits[value & 0x0F];
        return offset;
    }
    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes / 1024.0 / 1024.0:F1} MB";
    private static uint MakeHeaderData(byte p0 = 0, byte p1 = 0, byte zf1 = 0, byte zf0 = 0)
        => (uint)(p0 | (p1 << 8) | (zf1 << 16) | (zf0 << 24));
    // ── CRC ──────────────────────────────────────────────────────────────────

    private static ushort Crc16(ReadOnlySpan<byte> data) { ushort crc = 0; foreach (var b in data) crc = UpdateCrc16(crc, b); return crc; }
    private static ushort FinalizeCrc16(ushort crc) => UpdateCrc16(UpdateCrc16(crc, 0), 0);
    // 必须与 lrzsz 的 updcrc 宏保持完全一致：
    // crctab[((crc >> 8) & 255)] ^ (crc << 8) ^ cp
    private static ushort UpdateCrc16(ushort crc, byte b)
        => (ushort)((s_crc16Table[(crc >> 8) & 0xFF] ^ (crc << 8) ^ b) & 0xFFFF);
    private static readonly ushort[] s_crc16Table = BuildCrc16Table();
    private static ushort[] BuildCrc16Table() { var t = new ushort[256]; for (uint i = 0; i < 256; i++) { uint c = i << 8; for (int j = 0; j < 8; j++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1; t[i] = (ushort)(c & 0xFFFF); } return t; }
}