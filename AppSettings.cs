using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RemoteX;

/// <summary>
/// 应用配置，持久化到 %LocalAppData%\RemoteX\appsettings.json。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly string FilePath = AppPaths.AppSettings;

    public int DefaultPort            { get; set; } = 3389;
    public int HealthCheckTimeoutMs   { get; set; } = 3000;
    public int HealthCheckConcurrency { get; set; } = 8;

    /// <summary>RDP 连接超时（Connect 到 OnConnected），毫秒。</summary>
    public int ConnectTimeoutMs       { get; set; } = 30_000;

    /// <summary>RDP 认证级别：0=默认，1=无要求，2=要求 NLA。</summary>
    public int RdpAuthLevel           { get; set; } = 0;

    /// <summary>SSH/Telnet 连接超时，秒。</summary>
    public int TerminalConnectTimeoutSec { get; set; } = 15;

    /// <summary>关闭主窗口时的行为：null=弹窗选择，"tray"=最小化到托盘，"exit"=直接退出。</summary>
    public string? CloseAction { get; set; } = null;

    public int       MaxRecentCount  { get; set; } = 10;
    public List<int> RecentServerIds { get; set; } = new();
    public string LastExportDirectory { get; set; } = "";
    public string LastImportDirectory { get; set; } = "";

    /// <summary>SOCKS5 代理列表，供 RDP/SSH 连接选用。</summary>
    public List<SocksProxyEntry> SocksProxies { get; set; } = new();

    /// <summary>云同步配置。SecretKey/SyncPassword 内存为明文，磁盘 DPAPI 加密。</summary>
    public CloudSyncConfig CloudSync { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            settings.SocksProxies ??= new List<SocksProxyEntry>();
            var needsSave = NormalizeSocksProxies(settings.SocksProxies);
            if (settings.SocksProxies.Count == 0 && TryGetLegacySocksFromJson(json, out var legacy))
            {
                legacy.EnsureId();
                settings.SocksProxies.Add(legacy);
                needsSave = true;
            }
            // 解密云同步密钥
            settings.CloudSync ??= new CloudSyncConfig();
            var cs = settings.CloudSync;
            if (!string.IsNullOrEmpty(cs.SecretKey) && CredentialProtector.IsProtected(cs.SecretKey))
                cs.SecretKey = CredentialProtector.Unprotect(cs.SecretKey);
            if (!string.IsNullOrEmpty(cs.SyncPassword) && CredentialProtector.IsProtected(cs.SyncPassword))
                cs.SyncPassword = CredentialProtector.Unprotect(cs.SyncPassword);

            if (needsSave)
                settings.Save();
            return settings;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"load appsettings failed: {ex.Message}");
            return new AppSettings();
        }
    }

    private static bool TryGetLegacySocksFromJson(string json, out SocksProxyEntry entry)
    {
        entry = new SocksProxyEntry();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("SocksHost", out var host) && host.GetString() is { Length: > 0 } h)
            {
                entry.Name = "默认";
                entry.Host = h;
                entry.Port = root.TryGetProperty("SocksPort", out var p) && p.TryGetInt32(out var port) ? port : 1080;
                entry.Username = root.TryGetProperty("SocksUsername", out var u) ? u.GetString() ?? "" : "";
                entry.Password = root.TryGetProperty("SocksPassword", out var pw) ? pw.GetString() ?? "" : "";
                return true;
            }
        }
        catch { }
        return false;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var persisted = CreatePersistedSnapshot();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(persisted, JsonOpts));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"save appsettings failed: {ex.Message}");
        }
    }

    /// <summary>将 serverId 加入最近连接列表并持久化。</summary>
    public void AddRecentServer(int serverId)
    {
        RecentServerIds.Remove(serverId);
        RecentServerIds.Insert(0, serverId);
        while (RecentServerIds.Count > MaxRecentCount)
            RecentServerIds.RemoveAt(RecentServerIds.Count - 1);
        Save();
    }

    /// <summary>从最近连接中移除指定 serverId。</summary>
    public void RemoveRecentServer(int serverId)
    {
        if (RecentServerIds.Remove(serverId)) Save();
    }

    private static bool NormalizeSocksProxies(List<SocksProxyEntry> proxies)
    {
        bool needsSave = false;
        foreach (var proxy in proxies)
        {
            if (string.IsNullOrWhiteSpace(proxy.Id))
            {
                proxy.EnsureId();
                needsSave = true;
            }

            if (!string.IsNullOrEmpty(proxy.Password))
            {
                if (CredentialProtector.IsProtected(proxy.Password))
                {
                    proxy.Password = CredentialProtector.Unprotect(proxy.Password);
                }
                else
                {
                    // 兼容旧版本明文配置，加载后立即迁移为 DPAPI 加密存储。
                    needsSave = true;
                }
            }
        }
        return needsSave;
    }

    private AppSettings CreatePersistedSnapshot()
    {
        return new AppSettings
        {
            DefaultPort = DefaultPort,
            HealthCheckTimeoutMs = HealthCheckTimeoutMs,
            HealthCheckConcurrency = HealthCheckConcurrency,
            ConnectTimeoutMs = ConnectTimeoutMs,
            RdpAuthLevel = RdpAuthLevel,
            TerminalConnectTimeoutSec = TerminalConnectTimeoutSec,
            CloseAction = CloseAction,
            MaxRecentCount = MaxRecentCount,
            RecentServerIds = new List<int>(RecentServerIds),
            LastExportDirectory = LastExportDirectory,
            LastImportDirectory = LastImportDirectory,
            SocksProxies = SocksProxies.Select(p =>
            {
                p.EnsureId();
                return new SocksProxyEntry
                {
                    Id = p.Id,
                    Name = p.Name,
                    Host = p.Host,
                    Port = p.Port,
                    Username = p.Username,
                    Password = CredentialProtector.Protect(p.Password),
                    UseTls = p.UseTls,
                    TlsServerName = p.TlsServerName,
                    TlsPinnedSha256 = p.TlsPinnedSha256
                };
            }).ToList(),
            CloudSync = new CloudSyncConfig
            {
                Enabled        = CloudSync.Enabled,
                Endpoint       = CloudSync.Endpoint,
                Region         = CloudSync.Region,
                Bucket         = CloudSync.Bucket,
                AccessKey      = CloudSync.AccessKey,
                SecretKey      = CredentialProtector.Protect(CloudSync.SecretKey),
                ObjectKey      = string.IsNullOrWhiteSpace(CloudSync.ObjectKey)
                                     ? "remotex/sync.json"
                                     : CloudSync.ObjectKey,
                UsePathStyle   = CloudSync.UsePathStyle,
                IgnoreSslErrors = CloudSync.IgnoreSslErrors,
                SyncPassword   = CredentialProtector.Protect(CloudSync.SyncPassword),
                LastSyncUtc    = CloudSync.LastSyncUtc
            }
        };
    }
}
