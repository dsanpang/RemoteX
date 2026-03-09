using System;
using System.IO;
using System.Reflection;

namespace RemoteX;

/// <summary>
/// 从程序集 EmbeddedResource（RemoteX.Assets.*）解压到 AppPaths.AssetsDir，供 WebView2 终端使用。
/// </summary>
internal static class AssetExtractor
{
    private const string Prefix = "RemoteX.Assets.";

    public static void ExtractAll()
    {
        Directory.CreateDirectory(AppPaths.AssetsDir);
        var asm = Assembly.GetExecutingAssembly();

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            var fileName = resourceName[Prefix.Length..];
            if (string.IsNullOrEmpty(fileName)) continue;

            var destPath = Path.Combine(AppPaths.AssetsDir, fileName);
            using var src  = asm.GetManifestResourceStream(resourceName)!;
            using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920);
            src.CopyTo(dest);
        }

        AppLogger.Info($"assets extracted to: {AppPaths.AssetsDir}");
    }
}
