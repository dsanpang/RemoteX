using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace RemoteX;

/// <summary>
/// 基于 S3 兼容协议（AWS SigV4）的云同步服务。
/// 支持 Alibaba Cloud OSS、AWS S3、MinIO、Cloudflare R2、Tencent COS 等。
/// 数据以 AES-256-GCM + PBKDF2 加密后存入云端对象存储。
/// </summary>
internal sealed class CloudSyncService
{
    private readonly HttpClient      _http;
    private readonly CloudSyncConfig _cfg;

    public CloudSyncService(CloudSyncConfig cfg)
    {
        _cfg = cfg;

        var handler = new HttpClientHandler();
        if (cfg.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        _http = new HttpClient(handler);
    }

    // ── 公开操作 ──────────────────────────────────────────────────────────────

    /// <summary>测试连接：HEAD 目标对象（200/404 均视为连接成功）。</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync()
    {
        try
        {
            ValidateConfig();
            var (url, host, path) = BuildAddress(_cfg.ObjectKey);
            using var req = MakeRequest(HttpMethod.Head, url, host, path, Array.Empty<byte>());
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode ||
                resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (true, $"连接成功（HTTP {(int)resp.StatusCode}）");

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (false, $"HTTP {(int)resp.StatusCode}：{ExtractS3Error(body)}");
        }
        catch (Exception ex)
        {
            return (false, BuildErrorMessage(ex));
        }
    }

    /// <summary>将服务器列表和代理列表加密后上传到云端。</summary>
    public async Task UploadAsync(
        IEnumerable<ServerInfo> servers,
        IEnumerable<SocksProxyEntry> proxies)
    {
        ValidateConfig();
        if (string.IsNullOrWhiteSpace(_cfg.SyncPassword))
            throw new InvalidOperationException("同步密码不能为空");

        var data = ServerExportImport.ExportToBytes(servers, proxies, _cfg.SyncPassword);
        await PutObjectAsync(_cfg.ObjectKey, data).ConfigureAwait(false);
    }

    /// <summary>从云端下载并解密，返回导入结果。</summary>
    public async Task<ImportResult> DownloadAsync()
    {
        ValidateConfig();
        if (string.IsNullOrWhiteSpace(_cfg.SyncPassword))
            throw new InvalidOperationException("同步密码不能为空");

        var data = await GetObjectAsync(_cfg.ObjectKey).ConfigureAwait(false);
        return ServerExportImport.ImportFromBytes(data, _cfg.SyncPassword);
    }

    // ── S3 操作 ───────────────────────────────────────────────────────────────

    private async Task PutObjectAsync(string key, byte[] body)
    {
        var (url, host, path) = BuildAddress(key);
        using var req = MakeRequest(HttpMethod.Put, url, host, path, body);
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body2 = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"上传失败 HTTP {(int)resp.StatusCode}：{ExtractS3Error(body2)}");
        }
    }

    private async Task<byte[]> GetObjectAsync(string key)
    {
        var (url, host, path) = BuildAddress(key);
        using var req = MakeRequest(HttpMethod.Get, url, host, path, Array.Empty<byte>());

        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"下载失败 HTTP {(int)resp.StatusCode}：{ExtractS3Error(body)}");
        }
        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    // ── 地址构造 ──────────────────────────────────────────────────────────────

    private (string Url, string Host, string Path) BuildAddress(string key)
    {
        var ep = _cfg.Endpoint.Trim();
        if (ep.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ep = ep[8..];
        else if (ep.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) ep = ep[7..];
        var endpoint = ep.TrimEnd('/');

        // 对每段路径分量编码，保留斜杠作为路径分隔符
        var safeKey = string.Join("/",
            key.TrimStart('/').Split('/').Select(Uri.EscapeDataString));

        if (_cfg.UsePathStyle)
        {
            // https://endpoint/bucket/key
            var host = endpoint;
            var path = $"/{_cfg.Bucket}/{safeKey}";
            return ($"https://{host}{path}", host, path);
        }
        else
        {
            // https://bucket.endpoint/key
            var host = $"{_cfg.Bucket}.{endpoint}";
            var path = $"/{safeKey}";
            return ($"https://{host}{path}", host, path);
        }
    }

    // ── 签名路由：根据端点自动选择 AWS SigV4 或阿里云 OSS V4 ────────────────

    private bool IsAliyunOss()
    {
        var ep = _cfg.Endpoint.Trim();
        if (ep.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ep = ep[8..];
        else if (ep.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) ep = ep[7..];
        return ep.Contains("aliyuncs.com", StringComparison.OrdinalIgnoreCase);
    }

    private HttpRequestMessage MakeRequest(
        HttpMethod method, string url, string host, string path, byte[] body)
        => IsAliyunOss()
            ? BuildOssRequest(method, url, host, path, body)
            : BuildAwsRequest(method, url, host, path, body);

    // ── 阿里云 OSS V1 签名（HMAC-SHA1，最简且经过大量实战验证）──────────────
    // StringToSign = Method\nContent-MD5\nContent-Type\nDate\nCanonicalizedOSSHeaders+CanonicalizedResource
    // Authorization: OSS <AccessKeyId>:<Base64(HMAC-SHA1(SecretKey, StringToSign))>

    private HttpRequestMessage BuildOssRequest(
        HttpMethod method, string url, string host, string path, byte[] body)
    {
        var date        = DateTime.UtcNow.ToString("R"); // RFC 1123，例如 "Tue, 15 Jan 2024 08:00:00 GMT"
        var contentType = method == HttpMethod.Put ? "application/octet-stream" : "";

        // CanonicalizedResource：始终为 /<bucket>/<key> 形式
        //   路径模式: path 已是 /bucket/key
        //   虚拟托管: path 是 /key，需前置 /bucket
        var canonicalResource = _cfg.UsePathStyle
            ? path
            : $"/{_cfg.Bucket}{path}";

        // StringToSign: HTTP-Verb\n\nContent-Type\nDate\n<CanonicalizedOSSHeaders><CanonicalizedResource>
        // Content-MD5 留空（第2行为空行），无 x-oss-* headers 故 CanonicalizedOSSHeaders 为空
        var stringToSign = $"{method.Method}\n\n{contentType}\n{date}\n{canonicalResource}";

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_cfg.SecretKey));
        var signature  = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Host",          host);
        req.Headers.TryAddWithoutValidation("Date",          date);
        req.Headers.TryAddWithoutValidation("Authorization", $"OSS {_cfg.AccessKey}:{signature}");
        return req;
    }

    // ── AWS SigV4 签名（AWS S3 / MinIO / Cloudflare R2 / 腾讯 COS 等）────────

    private HttpRequestMessage BuildAwsRequest(
        HttpMethod method, string url, string host, string path, byte[] body)
    {
        var utcNow    = DateTime.UtcNow;
        var amzDate   = utcNow.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = utcNow.ToString("yyyyMMdd");
        var region    = string.IsNullOrWhiteSpace(_cfg.Region) ? "us-east-1" : _cfg.Region.Trim();

        var payloadHash = Sha256Hex(body);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"]                 = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"]           = amzDate,
        };

        var signedHeaderNames = string.Join(";", headers.Keys);
        var canonicalHeaders  = string.Concat(headers.Select(kv => $"{kv.Key}:{kv.Value}\n"));

        var canonicalRequest =
            $"{method.Method}\n{path}\n\n{canonicalHeaders}\n{signedHeaderNames}\n{payloadHash}";

        var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n" +
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));

        var signingKey = HmacSha256(
            HmacSha256(
                HmacSha256(
                    HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _cfg.SecretKey), dateStamp),
                    region),
                "s3"),
            "aws4_request");

        var signature = BitConverter.ToString(HmacSha256(signingKey, stringToSign))
                            .Replace("-", "").ToLowerInvariant();

        var authorization =
            $"AWS4-HMAC-SHA256 Credential={_cfg.AccessKey}/{credentialScope}, " +
            $"SignedHeaders={signedHeaderNames}, Signature={signature}";

        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Host",                  host);
        req.Headers.TryAddWithoutValidation("x-amz-date",            amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256",  payloadHash);
        req.Headers.TryAddWithoutValidation("Authorization",          authorization);
        return req;
    }

    // ── 加密工具 ──────────────────────────────────────────────────────────────

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    // ── 验证 & 辅助 ───────────────────────────────────────────────────────────

    private void ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(_cfg.Endpoint))
            throw new InvalidOperationException("终端节点（Endpoint）不能为空");
        if (string.IsNullOrWhiteSpace(_cfg.Bucket))
            throw new InvalidOperationException("存储桶（Bucket）不能为空");
        if (string.IsNullOrWhiteSpace(_cfg.AccessKey))
            throw new InvalidOperationException("访问密钥 ID（AccessKey）不能为空");
        if (string.IsNullOrWhiteSpace(_cfg.SecretKey))
            throw new InvalidOperationException("访问密钥 Secret 不能为空");
    }

    /// <summary>将异常链展开为可读字符串，帮助用户诊断 SSL / 网络问题。</summary>
    private static string BuildErrorMessage(Exception ex)
    {
        var sb = new System.Text.StringBuilder(ex.Message);
        var inner = ex.InnerException;
        while (inner != null)
        {
            sb.Append("\n→ ").Append(inner.Message);
            inner = inner.InnerException;
        }
        return sb.ToString();
    }

    private static string ExtractS3Error(string xml)
    {
        // 简单从 S3 XML 错误响应中提取 <Message> 标签
        var start = xml.IndexOf("<Message>", StringComparison.Ordinal);
        var end   = xml.IndexOf("</Message>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return xml[(start + 9)..end];
        return xml.Length > 300 ? xml[..300] : xml;
    }
}
