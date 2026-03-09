using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteX;

// ── 导出 DTO（不�?DB 主键、运行时状态） ─────────────────────────────────────

internal sealed class ServerExportDto
{
    public string Name              { get; set; } = "";
    public string IP                { get; set; } = "";
    public int    Port              { get; set; } = 3389;
    public string Username          { get; set; } = "";
    public string Password          { get; set; } = "";
    public string Description       { get; set; } = "";
    public string Group             { get; set; } = "";
    public string Protocol          { get; set; } = "RDP";
    public string SshPrivateKeyPath { get; set; } = "";
}

// ── 文件信封（统一头部�?──────────────────────────────────────────────────────

internal sealed class ExportEnvelope
{
    [JsonPropertyName("v")]       public int                   Version   { get; set; }
    [JsonPropertyName("ts")]      public string                Timestamp { get; set; } = "";
    [JsonPropertyName("enc")]     public bool                  Encrypted { get; set; }
    [JsonPropertyName("servers")] public List<ServerExportDto>? Servers  { get; set; }
    [JsonPropertyName("salt")]    public string? Salt   { get; set; }
    [JsonPropertyName("nonce")]   public string? Nonce  { get; set; }
    [JsonPropertyName("tag")]     public string? Tag    { get; set; }
    [JsonPropertyName("data")]    public string? Data   { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────

internal static class ServerExportImport
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── Export ───────────────────────────────────────────────────────────────

    public static void Export(IEnumerable<ServerInfo> servers, string filePath, string? password)
    {
        var dtos = servers.Select(s => new ServerExportDto
        {
            Name              = s.Name,
            IP                = s.IP,
            Port              = s.Port,
            Username          = s.Username,
            Password          = s.Password,
            Description       = s.Description,
            Group             = s.Group,
            Protocol          = s.Protocol.ToString(),
            SshPrivateKeyPath = s.SshPrivateKeyPath
        }).ToList();

        ExportEnvelope envelope;

        if (string.IsNullOrEmpty(password))
        {
            envelope = new ExportEnvelope
            {
                Version   = 1,
                Timestamp = DateTime.UtcNow.ToString("O"),
                Encrypted = false,
                Servers   = dtos
            };
        }
        else
        {
            var payload              = JsonSerializer.SerializeToUtf8Bytes(dtos, JsonOpts);
            var (salt, nonce, tag, cipher) = EncryptAesGcm(payload, password);

            envelope = new ExportEnvelope
            {
                Version   = 1,
                Timestamp = DateTime.UtcNow.ToString("O"),
                Encrypted = true,
                Salt      = Convert.ToBase64String(salt),
                Nonce     = Convert.ToBase64String(nonce),
                Tag       = Convert.ToBase64String(tag),
                Data      = Convert.ToBase64String(cipher)
            };
        }

        var json = JsonSerializer.Serialize(envelope, JsonOpts);
        File.WriteAllText(filePath, json, Encoding.UTF8);
        AppLogger.Info($"exported {dtos.Count} servers to {filePath}");
    }

    // ── Import ───────────────────────────────────────────────────────────────

    public static List<ServerInfo> Import(string filePath, string? password)
    {
        var json     = File.ReadAllText(filePath, Encoding.UTF8);
        var envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, JsonOpts)
                       ?? throw new InvalidOperationException("文件格式无法解析");

        if (envelope.Version != 1)
            throw new InvalidOperationException($"不支持的文件版本: {envelope.Version}");

        List<ServerExportDto>? dtos;

        if (!envelope.Encrypted)
        {
            dtos = envelope.Servers ?? throw new InvalidOperationException("文件格式错误：缺少 servers 字段");
        }
        else
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("此备份文件已加密，请输入导入密码");

            if (envelope.Salt == null || envelope.Nonce == null
                || envelope.Tag == null || envelope.Data == null)
                throw new InvalidOperationException("文件格式错误：加密字段不完整");

            var salt   = Convert.FromBase64String(envelope.Salt);
            var nonce  = Convert.FromBase64String(envelope.Nonce);
            var tag    = Convert.FromBase64String(envelope.Tag);
            var cipher = Convert.FromBase64String(envelope.Data);

            var plain = DecryptAesGcm(salt, nonce, tag, cipher, password);
            dtos = JsonSerializer.Deserialize<List<ServerExportDto>>(plain, JsonOpts)
            ?? throw new InvalidOperationException("解密后内容无法解析");
        }

        var result = dtos.Select(d =>
        {
            var proto = Enum.TryParse<ServerProtocol>(d.Protocol, out var p) ? p : ServerProtocol.RDP;
            int defaultPort = proto == ServerProtocol.SSH ? 22 : proto == ServerProtocol.Telnet ? 23 : 3389;
            return new ServerInfo
            {
                Name              = d.Name,
                IP                = d.IP,
                Port              = d.Port > 0 ? d.Port : defaultPort,
                Username          = d.Username,
                Password          = d.Password,
                Description       = d.Description,
                Group             = d.Group,
                Protocol          = proto,
                SshPrivateKeyPath = d.SshPrivateKeyPath
            };
        }).ToList();

        AppLogger.Info($"imported {result.Count} servers from {filePath}");
        return result;
    }

    // ── AES-256-GCM ──────────────────────────────────────────────────────────

    private static (byte[] salt, byte[] nonce, byte[] tag, byte[] cipher)
        EncryptAesGcm(byte[] plaintext, string password)
    {
        var salt   = RandomNumberGenerator.GetBytes(16);
        var nonce  = RandomNumberGenerator.GetBytes(12);
        var key    = DeriveKey(password, salt);
        var cipher = new byte[plaintext.Length];
        var tag    = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return (salt, nonce, tag, cipher);
    }

    private static byte[] DecryptAesGcm(
        byte[] salt, byte[] nonce, byte[] tag, byte[] cipher, string password)
    {
        var key   = DeriveKey(password, salt);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, 16);
        try
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("备份文件已损坏，解析失败");
        }

        return plain;
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password, salt, 200_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}
