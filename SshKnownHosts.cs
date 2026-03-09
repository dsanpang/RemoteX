using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RemoteX;

/// <summary>SSH 主机密钥 TOFU（首次信任），存储在 %LocalAppData%\RemoteX\known_hosts</summary>
internal static class SshKnownHosts
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataRoot, "known_hosts");
    private static readonly object _lock = new();
    private static Dictionary<string, string>? _cache;

    private static Dictionary<string, string> Load()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return _cache;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var t = line.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                    var idx = t.IndexOf(' ');
                    if (idx <= 0) continue;
                    var key = t[..idx].Trim();
                    var val = t[(idx + 1)..].Trim();
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                        _cache[key] = val;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"load known_hosts failed: {ex.Message}");
            }
            return _cache;
        }
    }

    public static string KeyFor(string host, int port)
        => port == 22 ? host : $"[{host}]:{port}";

    public static bool TryGet(string host, int port, out string? fingerprint)
    {
        var key = KeyFor(host, port);
        var d = Load();
        return d.TryGetValue(key, out fingerprint);
    }

    public static void Add(string host, int port, string fingerprint)
    {
        var key = KeyFor(host, port);
        lock (_lock)
        {
            var d = Load();
            d[key] = fingerprint;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var lines = new List<string> { "# RemoteX SSH known hosts (TOFU)" };
                foreach (var kv in d)
                    lines.Add($"{kv.Key} {kv.Value}");
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"save known_hosts failed: {ex.Message}");
            }
        }
    }
}
