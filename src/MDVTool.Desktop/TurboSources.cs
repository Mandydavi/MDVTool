using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// 极速引擎的下载计划：一组「内容完全一致」的镜像地址 + 分段策略。
/// aria2 在同一任务里给多个 URL 时，会把不同分段分散到这些 URL，
/// 因此带宽可以跨源叠加，而不是像单 URL 那样被一个 CDN 节点的上限卡死。
/// </summary>
public sealed class TurboPlan
{
    /// <summary>已通过内容一致性校验的地址，第一个是主源</summary>
    public List<string> Urls { get; set; } = new();
    /// <summary>总连接数（分段数）</summary>
    public int Connections = 64;
    /// <summary>每个 host 允许的连接数，用来强制把分段摊到不同源</summary>
    public int PerServer = 16;
    public long Total;
    public bool Ranges = true;
    /// <summary>各源实测速度，仅用于日志展示</summary>
    public List<(string Host, double Bps)> Scores { get; } = new();

    public int SourceCount => Urls.Count;
    public bool IsMultiSource => Urls.Count > 1;
}

public static class TurboSources
{
    public const int MaxConnections = 64;

    private const int FingerprintSize = 64 * 1024;
    private const double ProbeSeconds = 1.5;
    private const int MaxSources = 8;

    private sealed class Probe
    {
        public string Url = "";
        public string Host = "";
        public byte[] Head = Array.Empty<byte>();
        public long Total;
        public bool Ranges;
        public double Bps;
        public bool Ok;
    }

    /// <summary>
    /// 并行探测所有候选源：拿到文件总长、是否支持 Range、首 64KB 指纹、实测吞吐。
    /// 只有「指纹一致 + 总长一致」的源才会进入最终计划，避免镜像内容不同导致文件损坏。
    /// </summary>
    public static async Task<TurboPlan> BuildAsync(
        string url, UrlInfo info, bool multiSource, int wantedConnections,
        Action<string> log, CancellationToken ct)
    {
        var plan = new TurboPlan();
        var candidates = new List<string> { url };

        if (multiSource)
        {
            try
            {
                candidates.AddRange(ComfyNet.CandidatesFor(url, info));
            }
            catch { /* 源适配失败不影响主链接 */ }

            try
            {
                candidates.AddRange(DirectMirrors(url));
            }
            catch { /* 镜像拼接失败就只用原始候选 */ }
        }

        candidates = candidates
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSources)
            .ToList();

        // 并行探测：总耗时约等于单个探测，而不是「候选数 × 探测时长」
        var jobs = candidates.Select(u => ProbeAsync(u, ct)).ToArray();
        Probe[] results;
        try { results = await Task.WhenAll(jobs); }
        catch (OperationCanceledException) { throw; }
        catch { results = Array.Empty<Probe>(); }

        var primary = results.FirstOrDefault(r => r.Ok &&
            string.Equals(r.Url, url, StringComparison.Ordinal)) ?? results.FirstOrDefault(r => r.Ok);

        if (primary == null)
        {
            // 全部探测失败：不阻塞下载，退回单链接，让 aria2 自己判生死
            plan.Urls.Add(url);
            plan.Total = 0;
            plan.Ranges = true;
            plan.Connections = DecideConnections(wantedConnections, 0, true, 1);
            plan.PerServer = plan.Connections;
            log("    源探测失败，按原始链接单源下载");
            return plan;
        }

        plan.Total = primary.Total;
        plan.Ranges = primary.Ranges;

        foreach (var r in results.Where(r => r.Ok))
        {
            bool sameHead = r.Head.Length == primary.Head.Length &&
                            r.Head.AsSpan().SequenceEqual(primary.Head);
            bool sameTotal = primary.Total <= 0 || r.Total <= 0 || r.Total == primary.Total;
            if (!sameHead || !sameTotal)
            {
                log($"    {r.Host,-26} 内容不一致，已剔除");
                continue;
            }
            plan.Urls.Add(r.Url);
            plan.Scores.Add((r.Host, r.Bps));
        }

        // 主源必须在最前，aria2 会以它作为首选；其余按实测速度排序
        plan.Urls = plan.Urls
            .OrderBy(u => string.Equals(u, url, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(u => -ScoreOf(u))
            .ToList();

        if (plan.Urls.Count == 0) plan.Urls.Add(url);

        int hosts = plan.Urls
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host.ToLowerInvariant() : "")
            .Distinct().Count();
        hosts = Math.Max(1, hosts);

        plan.Connections = DecideConnections(wantedConnections, plan.Total, plan.Ranges, plan.Urls.Count);
        plan.PerServer = Math.Clamp((int)Math.Ceiling(plan.Connections / (double)hosts), 1, plan.Connections);

        double sum = plan.Scores.Sum(s => s.Bps);
        log($"    可用源 {plan.Urls.Count} 个 / {hosts} 个 host" +
            (sum > 0 ? $"，合计实测 {sum / 1048576.0:0.00} MB/s" : "") +
            $" → {plan.Connections} 分段（每源 {plan.PerServer} 连接）");
        return plan;

        double ScoreOf(string u)
        {
            foreach (var s in plan.Scores)
                if (Uri.TryCreate(u, UriKind.Absolute, out var x) &&
                    string.Equals(x.Host, s.Host, StringComparison.OrdinalIgnoreCase))
                    return s.Bps;
            return 0;
        }
    }

    /// <summary>小文件不做过多分段；不支持 Range 就只能单连接</summary>
    private static int DecideConnections(int wanted, long total, bool ranges, int sources)
    {
        if (!ranges) return 1;
        int n = Math.Clamp(wanted <= 0 ? MaxConnections : wanted, 1, MaxConnections);
        if (total > 0)
        {
            if (total < 16L * 1024 * 1024) return Math.Min(n, 8);
            if (total < 64L * 1024 * 1024) return Math.Min(n, 16);
            if (total < 256L * 1024 * 1024) return Math.Min(n, 32);
        }
        if (sources > 1 && total > 64L * 1024 * 1024) return MaxConnections;
        return n;
    }

    private static async Task<Probe> ProbeAsync(string url, CancellationToken ct)
    {
        var p = new Probe { Url = url };
        try { p.Host = new Uri(url).Host; } catch { p.Host = "unknown"; }

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 4 * 1024 * 1024 - 1);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await ComfyNet.Http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return p;

            var cr = resp.Content.Headers.ContentRange;
            p.Total = cr?.Length ?? (resp.StatusCode == HttpStatusCode.OK
                ? resp.Content.Headers.ContentLength ?? 0
                : 0);
            p.Ranges = cr != null || resp.Headers.AcceptRanges.Contains("bytes");

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var head = new byte[FingerprintSize];
            int headLen = 0;
            long received = 0;
            var buf = new byte[131072];

            while (sw.Elapsed.TotalSeconds < ProbeSeconds)
            {
                int n = await stream.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                if (headLen < head.Length)
                {
                    int take = Math.Min(n, head.Length - headLen);
                    Buffer.BlockCopy(buf, 0, head, headLen, take);
                    headLen += take;
                }
                received += n;
            }

            if (headLen == 0) return p;
            p.Head = headLen == head.Length ? head : head[..headLen];
            p.Bps = received / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            p.Ok = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* 单个源失败不影响其它源 */ }
        return p;
    }

    /// <summary>
    /// 通用直链的常见镜像。只对「确定是同一份内容」的映射做替换，
    /// 其余一律交给 BuildAsync 的指纹校验把关。
    /// </summary>
    public static IEnumerable<string> DirectMirrors(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield break;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimStart('/');
        var query = uri.Query;

        if (host == "raw.githubusercontent.com")
        {
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 4)
            {
                var owner = seg[0];
                var repo = seg[1];
                var branch = seg[2];
                var file = string.Join("/", seg.Skip(3));
                yield return $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/{file}{query}";
                yield return $"https://fastly.jsdelivr.net/gh/{owner}/{repo}@{branch}/{file}{query}";
                yield return $"https://gcore.jsdelivr.net/gh/{owner}/{repo}@{branch}/{file}{query}";
            }
        }
        else if (host is "github.com" or "www.github.com" && path.Contains("/raw/"))
        {
            var i = path.IndexOf("/raw/", StringComparison.Ordinal);
            var left = path[..i].Split('/', StringSplitOptions.RemoveEmptyEntries);
            var right = path[(i + 5)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length >= 2 && right.Length >= 2)
            {
                var owner = left[0];
                var repo = left[1];
                var branch = right[0];
                var file = string.Join("/", right.Skip(1));
                yield return $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{file}{query}";
                yield return $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/{file}{query}";
            }
        }
        else if (host == "huggingface.co" || host.EndsWith(".huggingface.co", StringComparison.Ordinal))
        {
            // HuggingFaceSource 已经会加 hf-mirror，这里只兜底没有走适配器解析的情况
            yield return $"https://hf-mirror.com{uri.PathAndQuery}";
        }
    }
}
