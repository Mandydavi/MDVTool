using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

public enum UrlKind { Unknown, File, Repo }

public sealed class UrlInfo
{
    public UrlKind Kind { get; set; } = UrlKind.Unknown;
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string? Org { get; set; }
    public string? Repo { get; set; }
    public string Rev { get; set; } = "main";
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Folder { get; set; }
    public string? CivitaiVersionId { get; set; }

    /// <summary>这条链接被哪个源认领（huggingface / modelscope / civitai / direct）</summary>
    public string SourceId { get; set; } = "";

    public string FullName => $"{Org}/{Repo}";
}

/// <summary>
/// 网络调度层：解析分发、跨源竞速、连接数自适应、HEAD 探测。
/// 各源自己的解析规则和地址形状在 Sources/ 下，这里不认识任何具体站点。
/// </summary>
public static class ComfyNet
{
    public static readonly HttpClient Http = CreateClient();

    // ==================================================================
    // 源注册表 —— 新增一个下载源只需要在这里加一行
    // ==================================================================
    private static readonly CivitaiSource Civitai = new();

    private static readonly IModelSource[] Registry =
    {
        new HuggingFaceSource(),
        new ModelScopeSource(),
        Civitai,
    };

    private static readonly GenericSource Fallback = new();
    private static readonly IRepoMirror[] MirrorProviders = Registry.OfType<IRepoMirror>().ToArray();

    /// <summary>已注册的源（设置页 / 日志里可以列出来）</summary>
    public static IReadOnlyList<IModelSource> Sources => Registry;

    /// <summary>按 host 找负责的源，找不到就用直链兜底</summary>
    public static IModelSource SourceFor(string host)
        => Registry.FirstOrDefault(s => s.Owns(host)) ?? Fallback;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 64,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MDVTool/2.0");
        c.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return c;
    }

    // ==================================================================
    // URL 解析
    // ==================================================================
    public static UrlInfo Parse(string raw)
    {
        var s = (raw ?? "").Trim().Trim('"');
        if (s.Length == 0) throw new ArgumentException("链接为空");
        if (!Regex.IsMatch(s, "^https?://", RegexOptions.IgnoreCase)) s = "https://" + s;

        Uri uri;
        try { uri = new Uri(s); }
        catch { throw new ArgumentException("这不是一个合法的网址"); }

        var host = uri.Host.ToLowerInvariant();
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var info = new UrlInfo { Host = host, Url = s };

        // 认领这个 host 的源先试；它说「认不出路径」就交给直链兜底
        var src = Registry.FirstOrDefault(x => x.Owns(host));
        if (src != null && src.TryParse(uri, parts, info))
        {
            info.SourceId = src.Id;
            return info;
        }

        Fallback.TryParse(uri, parts, info);
        info.SourceId = Fallback.Id;
        return info;
    }

    // ==================================================================
    // 仓库文件列表
    // ==================================================================
    public static Task<IReadOnlyList<RepoFile>> ListRepoAsync(UrlInfo info, CancellationToken ct)
        => SourceFor(info.Host).ListFilesAsync(info, ct);

    public static string FileUrlFor(UrlInfo info, string path)
        => SourceFor(info.Host).ResolveUrl(info, path);

    // ==================================================================
    // civitai 真实文件名 / 目录
    // ==================================================================
    public static Task<(string? Name, string? Folder)> LookupCivitaiAsync(string versionId, CancellationToken ct)
        => Civitai.LookupAsync(versionId, ct);

    // ==================================================================
    // 多源测速
    // ==================================================================
    public static List<string> CandidatesFor(string url, UrlInfo info)
    {
        var list = new List<string> { url };
        if (info.Kind != UrlKind.File || string.IsNullOrEmpty(info.Path) ||
            string.IsNullOrEmpty(info.Org) || string.IsNullOrEmpty(info.Repo))
            return list;

        // 同一个源的镜像 host（如 hf-mirror ↔ huggingface）
        var src = SourceFor(info.Host);
        foreach (var m in src.Mirrors(info)) list.Add(m);

        // 跨源：别的源能不能也拼出这个文件（如 ModelScope 上的同名镜像）
        foreach (var provider in MirrorProviders)
        {
            if (ReferenceEquals(provider, src)) continue;
            var u = provider.MirrorUrl(info.Org!, info.Repo!, info.Path);
            if (!string.IsNullOrEmpty(u)) list.Add(u);
        }

        return list.Distinct().ToList();
    }

    /// <summary>下载 secs 秒，返回 (字节/秒, 收到的字节数)</summary>
    public static async Task<(double Bps, long Bytes)> ProbeAsync(string url, double secs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long n = 0;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, null);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[262144];
            while (sw.Elapsed.TotalSeconds < secs)
            {
                int read = await stream.ReadAsync(buf, ct);
                if (read <= 0) break;
                n += read;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (0, 0);
        }
        var dt = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return (n / dt, n);
    }

    // ==================================================================
    // 连接数自适应：实测 1 / 4 / 16 / 32 / 64 连接，谁快用谁
    // ==================================================================
    private static readonly Dictionary<string, (int Conn, DateTime At)> ConnCache = new();
    private static readonly object ConnGate = new();

    public static async Task<int> PickConnectionsAsync(
        string url, long total, int maxConn, Action<string> log, CancellationToken ct)
    {
        maxConn = Math.Clamp(maxConn, 1, TurboSources.MaxConnections);
        if (maxConn <= 1) return 1;
        if (total > 0 && total < 8L * 1024 * 1024) return 1;   // 小文件分片没意义
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return 1;

        var host = uri.Host.ToLowerInvariant();
        lock (ConnGate)
        {
            if (ConnCache.TryGetValue(host, out var hit) &&
                DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
            {
                int cached = Math.Min(hit.Conn, maxConn);
                log($"    {host} 沿用 10 分钟内的测速结果：{cached} 连接");
                return cached;
            }
        }

        var tiers = new[] { 1, 4, 16, 32, 64 }.Where(n => n <= maxConn).Distinct().ToArray();
        log($"连接数自动测速（{string.Join(" / ", tiers)}，各 2.5 秒）...");

        int best = 1;
        double bestBps = -1;
        foreach (var n in tiers)
        {
            ct.ThrowIfCancellationRequested();
            double bps = await MeasureAsync(url, total, n, 2.5, ct);
            log(bps > 0
                ? $"    {n,2} 连接  {bps / 1048576.0,7:0.00} MB/s"
                : $"    {n,2} 连接  失败");
            if (bps > bestBps) { bestBps = bps; best = n; }
        }

        ct.ThrowIfCancellationRequested();
        if (bestBps <= 0)
        {
            log("    测速全部失败，先用单连接");
            return 1;
        }

        lock (ConnGate) ConnCache[host] = (best, DateTime.UtcNow);
        log($"    -> 采用 {best} 连接（{bestBps / 1048576.0:0.00} MB/s）");
        return best;
    }

    /// <summary>用 n 条连接同时拉不同区间，测 secs 秒内的总吞吐</summary>
    private static async Task<double> MeasureAsync(
        string url, long total, int conns, double secs, CancellationToken ct)
    {
        long received = 0;
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(secs));

        long chunk = total > 0 ? Math.Max(1, total / conns) : 0;
        var tasks = new List<Task>(conns);
        for (int i = 0; i < conns; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (chunk > 0)
                    {
                        long start = idx * chunk;
                        long end = idx == conns - 1 ? total - 1 : start + chunk - 1;
                        req.Headers.Range = new RangeHeaderValue(start, end);
                    }
                    else
                    {
                        req.Headers.Range = new RangeHeaderValue(0, null);
                    }

                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                    var buf = new byte[131072];
                    while (true)
                    {
                        int n = await s.ReadAsync(buf, cts.Token);
                        if (n <= 0) break;
                        Interlocked.Add(ref received, n);
                    }
                }
                catch (OperationCanceledException) { /* 到点收工 */ }
                catch { /* 单条连接失败不影响整体比较 */ }
            }, cts.Token));
        }

        try { await Task.WhenAll(tasks); } catch { }
        double dt = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return received / dt;
    }

    /// <summary>同一文件在多个站点测速，返回最快的那个</summary>
    public static async Task<(string Best, List<(string Host, double Bps)> Results)> RaceAsync(
        string url, UrlInfo info, Action<string> log, CancellationToken ct)
    {
        var cands = CandidatesFor(url, info);
        var results = new List<(string, double)>();
        if (cands.Count == 1) return (url, results);

        log($"并行测速选源（{cands.Count} 个候选，各 2.5 秒，同时进行）...");
        var probes = cands.Select(u => ProbeAsync(u, 2.5, ct)).ToArray();
        var bpsList = await Task.WhenAll(probes);
        for (int i = 0; i < cands.Count; i++)
        {
            double bps = bpsList[i].Bps;
            var host = Uri.TryCreate(cands[i], UriKind.Absolute, out var x) ? x.Host : "unknown";
            results.Add((host, bps));
            log(bps > 0 ? $"    {host,-26} {bps / 1048576.0,8:0.00} MB/s" : $"    {host,-26} 失败");
        }

        var ok = results.Where(r => r.Item2 > 0).OrderByDescending(r => r.Item2).ToList();
        if (ok.Count == 0)
        {
            log("    全部测速失败，用原始链接");
            return (url, results);
        }

        var bestUrl = cands.First(u => new Uri(u).Host == ok[0].Item1);
        log($"    -> 选 {ok[0].Item1}（{ok[0].Item2 / 1048576.0:0.00} MB/s）");
        return (bestUrl, results);
    }

    /// <summary>文件大小 / 是否支持断点续传</summary>
    public static async Task<(long Total, string FinalUrl, bool Ranges)> HeadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            long total = resp.Content.Headers.ContentLength ?? 0;
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.Length != null) total = cr.Length.Value;
            bool ranges = resp.Headers.AcceptRanges.Contains("bytes") || cr != null;
            if (total > 0) return (total, final, ranges);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch { /* 退化成 HEAD */ }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (resp.Content.Headers.ContentLength ?? 0,
                    resp.RequestMessage?.RequestUri?.ToString() ?? url, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (0, url, true);
        }
    }
}
