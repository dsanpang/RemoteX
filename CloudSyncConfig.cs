namespace RemoteX;

/// <summary>
/// 云同步配置（存入 appsettings.json，SecretKey/SyncPassword 以 DPAPI 加密）。
/// 内存中字段均为明文，仅持久化时才加密。
/// </summary>
public sealed class CloudSyncConfig
{
    /// <summary>是否启用云同步。</summary>
    public bool   Enabled      { get; set; } = false;

    /// <summary>S3 兼容终端节点，仅主机名，例如 oss-cn-hangzhou.aliyuncs.com</summary>
    public string Endpoint     { get; set; } = "";

    /// <summary>区域，例如 cn-hangzhou 或 us-east-1。</summary>
    public string Region       { get; set; } = "";

    /// <summary>存储桶名称。</summary>
    public string Bucket       { get; set; } = "";

    /// <summary>AccessKey ID。</summary>
    public string AccessKey    { get; set; } = "";

    /// <summary>AccessKey Secret（内存明文，磁盘 DPAPI 加密）。</summary>
    public string SecretKey    { get; set; } = "";

    /// <summary>云端对象路径（key），例如 remotex/sync.json。</summary>
    public string ObjectKey    { get; set; } = "remotex/sync.json";

    /// <summary>是否使用路径风格 URL（MinIO 自托管时通常需要开启）。</summary>
    public bool   UsePathStyle { get; set; } = false;

    /// <summary>同步数据加密密码（内存明文，磁盘 DPAPI 加密）。所有设备须一致。</summary>
    public string SyncPassword { get; set; } = "";

    /// <summary>最近一次成功同步的 UTC 时间（ISO-8601），仅作展示用。</summary>
    public string LastSyncUtc  { get; set; } = "";

    /// <summary>跳过 SSL 证书验证（用于自签名证书或企业代理，存在安全风险）。</summary>
    public bool   IgnoreSslErrors { get; set; } = false;
}
