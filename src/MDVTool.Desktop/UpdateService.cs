using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>远端发布的一个版本</summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public long Size { get; set; }
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>更新包 SHA-256（可选）；应用内更新时用来校验下载是否完整</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>清单签名（base64，ECDSA P-256 / SHA-256）；为空表示该清单未签名</summary>
    public string Signature { get; set; } = "";

    /// <summary>清单里原样的 url（签名是对原样字段做的，不能用补全后的路径）</summary>
    public string RawUrl { get; set; } = "";

    public string SizeText => Size > 0 ? Fmt.Human(Size) : "";
    public string Summary => string.Join(" · ",
        new[] { Version, Date, SizeText }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>
/// 联网更新检查。
/// 读一个 version.json（可以放 GitHub raw / jsDelivr / Gitee raw 任意静态地址），
/// 格式：{ "version": "2.4.0", "date": "2026-11-06", "size": 62464828,
///        "url": "https://…/MDVTool.exe", "notes": "修了什么" }
/// 地址为空时整个功能静默跳过。
/// </summary>
public static class UpdateService
{
    public static Version Current =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText
    {
        get
        {
            var v = Current;
            return $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    /// <summary>最近一次检查失败的原因（成功或"已是最新"时为 null）</summary>
    public static string? LastError { get; private set; }

    /// <summary>有更新返回信息，没有 / 未配置 / 失败都返回 null</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        LastError = null;
        var feed = (App.Settings.UpdateFeed ?? "").Trim();
        if (feed.Length == 0) return null;

        // 本地 / UNC / file:// 更新源：不连公网也能推送（共享盘、U 盘、映射盘、内网目录）。
        // 只要把新的 version.json 和新版 exe 放到同一个目录，客户端就能检查到更新。
        if (!IsHttp(feed))
        {
            try
            {
                string local = LocalPath(feed);
                if (!File.Exists(local))
                {
                    LastError = "更新源文件不存在：" + local;
                    return null;
                }
                return Finish(Parse(File.ReadAllText(local)), local);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { LastError = "读取更新源失败：" + ex.Message; return null; }
        }

        // 先按「最新 tag」取文件：jsDelivr 对 @main 有最长 12 小时的缓存，
        // 而 @v1.2.3 这种 tag 地址是实时的。tag 从 github.com 的 atom 源读（国内可达）。
        var byTag = await TryByNewestTagAsync(feed, ct);
        if (byTag != null) return byTag;

        // 依次尝试多个镜像：国内直连 raw.githubusercontent 基本不通，jsDelivr 通常可达
        string? lastFail = null;
        foreach (var url in Candidates(feed))
        {
            try
            {
                var json = await ComfyNet.Http.GetStringAsync(url, ct);
                var info = Finish(Parse(json), url);
                if (info == null && LastError != null) return null;   // 签名/内容有问题，不再试别的镜像
                return info;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastFail = "连接更新服务器超时";           // HttpClient 超时也走这里，不能当成"已是最新"
            }
            catch (Exception ex)
            {
                lastFail = ex.Message;
            }
        }
        LastError = lastFail == null ? "更新源不可达" : "检查更新失败：" + lastFail;
        return null;
    }


    /// <summary>从 releases.atom 读最新 tag，再取该 tag 下的 version.json（无缓存延迟）</summary>
    private static async Task<UpdateInfo?> TryByNewestTagAsync(string feed, CancellationToken ct)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                feed, @"jsdelivr\.net/gh/([^/]+)/([^@/]+)@");
            if (!m.Success) return null;

            var owner = m.Groups[1].Value;
            var repo = m.Groups[2].Value;

            var atom = await ComfyNet.Http.GetStringAsync(
                $"https://github.com/{owner}/{repo}/releases.atom", ct);

            var tag = System.Text.RegularExpressions.Regex.Match(
                atom, @"<entry>\s*<id>[^<]*/([^/<]+)</id>");
            if (!tag.Success) return null;

            var url = $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{tag.Groups[1].Value}/version.json";
            var json = await ComfyNet.Http.GetStringAsync(url, ct);
            var info = Finish(Parse(json), url);
            if (info == null && LastError != null) return null;   // 签名无效等情况，别再用镜像重试
            return info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;   // 失败就走后面的常规候选
        }
    }

    /// <summary>把配置的一个地址展开成多个候选（raw ↔ jsDelivr ↔ gcore）</summary>
    private static IEnumerable<string> Candidates(string feed)
    {
        yield return feed;

        var raw = System.Text.RegularExpressions.Regex.Match(
            feed, @"raw\.githubusercontent\.com/([^/]+)/([^/]+)/([^/]+)/(.+)");
        if (raw.Success)
        {
            var owner = raw.Groups[1].Value;
            var repo = raw.Groups[2].Value;
            var branch = raw.Groups[3].Value;
            var path = raw.Groups[4].Value;
            yield return $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/{path}";
            yield return $"https://gcore.jsdelivr.net/gh/{owner}/{repo}@{branch}/{path}";
            yield break;
        }

        var cdn = System.Text.RegularExpressions.Regex.Match(
            feed, @"(?:cdn|fastly|gcore)\.jsdelivr\.net/gh/([^/]+)/([^@]+)@([^/]+)/(.+)");
        if (cdn.Success)
        {
            var owner = cdn.Groups[1].Value;
            var repo = cdn.Groups[2].Value;
            var branch = cdn.Groups[3].Value;
            var path = cdn.Groups[4].Value;
            yield return $"https://gcore.jsdelivr.net/gh/{owner}/{repo}@{branch}/{path}";
            yield return $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}";
        }
    }

    private static bool IsHttp(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 file://、环境变量、引号都归一化成本地路径（UNC 共享路径原样保留）。</summary>
    private static string LocalPath(string feed)
    {
        string s = Environment.ExpandEnvironmentVariables(feed.Trim().Trim('"'));
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(s).LocalPath; } catch { return s; }
        }
        return s;
    }

    /// <summary>
    /// version.json 里的 url 可以只写文件名（如 "MDVTool-1.0.0-Setup.exe"），
    /// 按 version.json 所在目录自动补全。同一份清单放公网、内网 HTTP 或共享盘都能用。
    /// </summary>
    private static UpdateInfo? Finish(UpdateInfo? info, string source)
    {
        if (info == null) return null;

        if (!VerifySignature(info))
        {
            LastError = "更新清单签名校验失败，已丢弃（可能被篡改）";
            return null;
        }

        string u = (info.Url ?? "").Trim();
        if (u.Length == 0 || IsHttp(u) || Path.IsPathRooted(u) ||
            u.StartsWith("\\\\", StringComparison.Ordinal)) return info;

        if (IsHttp(source))
        {
            int i = source.LastIndexOf('/');
            if (i > 0) info.Url = source.Substring(0, i + 1) + u.Replace('\\', '/');
        }
        else
        {
            string dir = Path.GetDirectoryName(source) ?? "";
            try
            {
                info.Url = Path.GetFullPath(
                    Path.Combine(dir, u.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch { }
        }
        return info;
    }

    // ==================================================================
    // 清单签名：ECDSA P-256 / SHA-256
    // 签名对象是清单里的原始字段 "version|url|sha256|size"，签名私钥不进仓库。
    // 规则：清单带 sig 就必须验签通过，否则丢整条更新（fail-closed）；
    //       不带 sig 视为旧格式，暂时放行（后续版本会改成必须签名）。
    // ==================================================================
    private const string ManifestPublicKey = @"-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyJ8b/UF2zDAmD/DSBPJqXeqold7s
aLbK6EZtTAUc8+v64S5vVkho8g+UCg/n1UVp4RweCYBLZlnkqs4KFW7Ykw==
-----END PUBLIC KEY-----";

    /// <summary>签名明文（与 scripts\sign-manifest.ps1 里的拼法必须一致）</summary>
    public static string SignedPayload(UpdateInfo i) =>
        $"{i.Version}|{i.RawUrl}|{i.Sha256.ToLowerInvariant()}|{i.Size}";

    private static bool VerifySignature(UpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Signature)) return true;   // 旧清单：未签名

        try
        {
            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportFromPem(ManifestPublicKey);
            var data = System.Text.Encoding.UTF8.GetBytes(SignedPayload(info));
            var sig = Convert.FromBase64String(info.Signature.Trim());

            // openssl 产出的签名是 DER（RFC3279）序列，.NET 默认按 IEEE-P1363 验，必须显式指定
            return ecdsa.VerifyData(data, sig,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }

    private static UpdateInfo? Parse(string json)    {
        try
        {
            // PowerShell 的 Set-Content -Encoding UTF8 会带 BOM，JsonDocument 不接受
            json = json.TrimStart('\uFEFF', '\u200B').Trim();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ver = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
            if (ver.Length == 0) return null;
            if (!Version.TryParse(ver.TrimStart('v', 'V'), out var remote)) return null;

            // 只比 Major.Minor.Build，避免 2.3.0 与 2.3.0.0 被当成不同版本
            var a = new Version(remote.Major, remote.Minor, Math.Max(0, remote.Build));
            var b = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
            if (a <= b) return null;

            return new UpdateInfo
            {
                Version = ver,
                Date = root.TryGetProperty("date", out var d) ? (d.GetString() ?? "") : "",
                Size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                Url = root.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "",
                RawUrl = root.TryGetProperty("url", out var ru) ? (ru.GetString() ?? "") : "",
                Notes = root.TryGetProperty("notes", out var n) ? (n.GetString() ?? "") : "",
                Sha256 = root.TryGetProperty("sha256", out var h) ? (h.GetString() ?? "") : "",
                Signature = root.TryGetProperty("sig", out var g) ? (g.GetString() ?? "") : "",
            };
        }
        catch
        {
            return null;   // 内容不合法就当作没有更新
        }
    }
}
