namespace RemoteX;

/// <summary>多机房场景下的一个 SOCKS5 代理（跳板机）配置。</summary>
public sealed class SocksProxyEntry
{
    /// <summary>显示名称，用于区分机房（如：北京机房、上海机房）</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1080;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
