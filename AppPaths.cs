using System;
using System.IO;

namespace RemoteX;

/// <summary>
/// 数据与缓存路径。所有路径位于 %LocalAppData%\RemoteX\ 下，
/// 包括 servers.db、appsettings.json、ui-state.json、assets、WebView2、logs 等。
/// </summary>
internal static class AppPaths
{
    public static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteX");

    private static readonly string LegacyDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyRdpManager");

    public static readonly string Database = Path.Combine(DataRoot, "servers.db");
    public static readonly string AppSettings = Path.Combine(DataRoot, "appsettings.json");
    public static readonly string UiState = Path.Combine(DataRoot, "ui-state.json");
    public static readonly string LogsDir = Path.Combine(DataRoot, "logs");
    public static readonly string AssetsDir = Path.Combine(DataRoot, "assets");
    public static readonly string WebView2UserDataFolder = Path.Combine(DataRoot, "WebView2");

    public static void EnsureDataRoot()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(WebView2UserDataFolder);
        MigrateLegacyData();
    }

    private static void MigrateLegacyData()
    {
        if (!Directory.Exists(LegacyDataRoot)) return;

        var filesToMigrate = new[] { "servers.db", "appsettings.json", "ui-state.json" };
        foreach (var fileName in filesToMigrate)
        {
            var src  = Path.Combine(LegacyDataRoot, fileName);
            var dest = Path.Combine(DataRoot, fileName);
            if (File.Exists(src) && !File.Exists(dest))
                File.Copy(src, dest);
        }

        try { Directory.Delete(LegacyDataRoot, recursive: true); }
        catch { /* 忽略占用或权限 */ }
    }
}
