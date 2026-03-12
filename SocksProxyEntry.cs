namespace RemoteX;

/// <summary>多机房场景下的一个 SOCKS5 代理（跳板机）配置。</summary>
public sealed class SocksProxyEntry
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

    /// <summary>显示名称，用于区分机房（如：北京机房、上海机房）</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1080;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
    public string TlsServerName { get; set; } = "";
    public string TlsPinnedSha256 { get; set; } = "";

    public string DisplayName => UseTls ? $"{Name} (TLS)" : Name;

    public void EnsureId()
    {
        if (string.IsNullOrWhiteSpace(Id))
            Id = System.Guid.NewGuid().ToString("N");
    }
}
