namespace RemoteX;

public enum PortForwardType { Local, Remote, Dynamic }

public sealed class PortForwardRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ServerId { get; set; }
    public string ServerName { get; set; } = "";
    public int LocalPort { get; set; } = 8080;
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 80;
    public PortForwardType ForwardType { get; set; } = PortForwardType.Local;
    public bool AutoStart { get; set; }

    // Runtime state (not persisted)
    public bool IsActive { get; set; }

    public string TypeBadge => ForwardType switch
    {
        PortForwardType.Local   => "LOCAL",
        PortForwardType.Remote  => "REMOTE",
        PortForwardType.Dynamic => "SOCKS",
        _                       => "?"
    };

    public string PortSummary => ForwardType switch
    {
        PortForwardType.Dynamic => $"本地端口 :{LocalPort}",
        _                       => $":{LocalPort} → {RemoteHost}:{RemotePort}"
    };
}
