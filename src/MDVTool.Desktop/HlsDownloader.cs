using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// m3u8 / HLS VOD 高并发分片下载器。
/// 独立 HttpClient，不复用 ComfyNet.Http，避免其 40s 超时与 32 连接上限拖累吞吐。
/// </summary>
public static class HlsDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 64,
            // 保留原始字节，别对媒体分片做自动解压
            AutomaticDecompression = DecompressionMethods.None,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MDVTool/2.0");
        return c;
    }

    // ==================================================================
    // 公开 API
    // ==================================================================

    /// <summary>URL 路径 / 查询含 .m3u8，或显式 MIME 标记（application/vnd.apple.mpegurl 等）时返回 true。</summary>
    public static bool LooksLikeHls(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string s = url.Trim().Trim('"');
        if (s.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // 接受 application/vnd.apple.mpegurl、application/x-mpegURL、audio/mpegurl 等显式标记
        if (s.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// 下载 m3u8 并合并写出到 destPath（.ts）。返回 true 表示完整落盘；
    /// 取消抛 OperationCanceledException；网络/解析异常抛带中文信息的异常。
    /// </summary>
    public static async Task<bool> DownloadAsync(
        string m3u8Url,
        string destPath,
        int concurrency,
        Action<long, long, double> progress,
        Action<string> log,
        CancellationToken ct)
    {
        progress ??= static (_, _, _) => { };
        log ??= static _ => { };
        concurrency = Math.Clamp(concurrency, 1, 64);

        Uri startUri;
        try { startUri = new Uri(m3u8Url); }
        catch (Exception ex)
        {
            throw new ArgumentException($"HLS 地址不合法：{m3u8Url}", nameof(m3u8Url), ex);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            MediaPlaylist playlist = await ResolvePlaylistAsync(startUri, 0, log, ct).ConfigureAwait(false);
            int count = playlist.Segments.Count;
            if (count == 0)
                throw new InvalidOperationException("media playlist 中没有任何分片（找不到 #EXTINF 后面的分片 URL）");

            bool encrypted = playlist.Segments.Any(s => s.Key != null);
            log($"[HLS] 共 {count} 个分片，加密：{(encrypted ? "是" : "否")}，并发 {concurrency}");

            await PrepareKeysAsync(playlist, log, ct).ConfigureAwait(false);
            await PrepareInitsAsync(playlist, log, ct).ConfigureAwait(false);

            string fullDest = Path.GetFullPath(destPath);
            string? destDir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            var state = new ProgressState { TotalCount = count };
            var buffers = new byte[count][];

            if (count <= 2000)
            {
                // 全部分片先下载到数组对应下标，完成后用单个 FileStream 顺序落盘
                await DownloadBatchAsync(playlist.Segments, buffers, 0, count,
                    concurrency, state, progress, ct).ConfigureAwait(false);

                using var fs = new FileStream(fullDest, FileMode.Create, FileAccess.Write, FileShare.None);
                WriteInits(fs, playlist);
                for (int i = 0; i < count; i++)
                    fs.Write(buffers[i], 0, buffers[i].Length);
            }
            else
            {
                // 分片过多时分批下载、分批顺序落盘，保证顺序正确且降低内存峰值
                const int batchSize = 256;
                using var fs = new FileStream(fullDest, FileMode.Create, FileAccess.Write, FileShare.None);
                WriteInits(fs, playlist);
                for (int b = 0; b < count; b += batchSize)
                {
                    int end = Math.Min(count, b + batchSize);
                    await DownloadBatchAsync(playlist.Segments, buffers, b, end,
                        concurrency, state, progress, ct).ConfigureAwait(false);
                    for (int i = b; i < end; i++)
                    {
                        fs.Write(buffers[i], 0, buffers[i].Length);
                        buffers[i] = null!; // 释放该批字节
                    }
                }
            }

            log($"[HLS] 完成，共写入 {Fmt.Human(state.DoneBytes)}（{count} 个分片）到 {fullDest}");
            progress(state.DoneBytes, state.DoneBytes, state.Speed);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log("[HLS] 下载已取消");
            throw;
        }
        catch (Exception ex)
        {
            log($"[HLS] 下载失败：{ex.Message}");
            throw new IOException($"HLS 下载失败：{ex.Message}", ex);
        }
    }

    /// <summary>init segment（#EXT-X-MAP）放在所有分片之前，且不解密。</summary>
    private static void WriteInits(FileStream fs, MediaPlaylist pl)
    {
        foreach (InitSpec init in pl.Inits)
            if (init.Data != null) fs.Write(init.Data, 0, init.Data.Length);
    }

    // ==================================================================
    // Playlist 解析（master / media，最多 3 层递归）
    // ==================================================================

    private static async Task<MediaPlaylist> ResolvePlaylistAsync(
        Uri uri, int depth, Action<string> log, CancellationToken ct)
    {
        if (depth >= 3)
            throw new InvalidOperationException("HLS master playlist 递归超过 3 层，疑似死循环，已终止解析");

        log($"[HLS] 解析 playlist（第 {depth + 1} 层）：{uri.AbsoluteUri}");
        string text = await FetchPlaylistAsync(uri, ct).ConfigureAwait(false);
        ParsedPlaylist parsed = ParsePlaylist(text, uri);

        if (parsed.IsMaster)
        {
            Variant? best = parsed.Variants
                .OrderByDescending(v => v.Bandwidth)
                .ThenByDescending(v => v.ResolutionArea)
                .FirstOrDefault();
            if (best == null)
                throw new InvalidOperationException("master playlist 中没有可用的 variant（#EXT-X-STREAM-INF）");
            log($"[HLS] 选中 variant：带宽 {best.Bandwidth}，分辨率 {best.Resolution ?? "未知"} -> {best.Uri}");
            return await ResolvePlaylistAsync(new Uri(best.Uri), depth + 1, log, ct).ConfigureAwait(false);
        }

        return parsed.Media;
    }

    private static async Task<string> FetchPlaylistAsync(Uri uri, CancellationToken ct)
    {
        byte[] bytes = await FetchWithRetryAsync(uri, null, null, ct).ConfigureAwait(false);
        string text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1); // 去 BOM
        return text;
    }

    private static ParsedPlaylist ParsePlaylist(string text, Uri baseUri)
    {
        var result = new ParsedPlaylist();
        MediaPlaylist media = result.Media;
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        KeySpec? curKey = null;
        InitSpec? curInit = null;
        long seq = 0;                 // 分片 media sequence number，默认从 0 开始
        long lastRangeEnd = 0;        // 上一段 BYTERANGE 结束位置（用于无 offset 的连续区间）
        long? pendingLen = null, pendingOff = null;
        Dictionary<string, string>? pendingStream = null;
        bool isMaster = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '#')
            {
                if (IsTag(line, "#EXT-X-STREAM-INF"))
                {
                    isMaster = true;
                    int colon = line.IndexOf(':');
                    pendingStream = ParseAttrs(colon >= 0 ? line.Substring(colon + 1) : "");
                }
                else if (IsTag(line, "#EXT-X-KEY"))
                {
                    int colon = line.IndexOf(':');
                    var a = ParseAttrs(colon >= 0 ? line.Substring(colon + 1) : "");
                    string method = Attr(a, "METHOD");
                    if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        curKey = null; // 后续分片不加密
                    }
                    else
                    {
                        var ks = new KeySpec { Method = method };
                        string ku = Attr(a, "URI");
                        if (ku.Length > 0) ks.Uri = new Uri(baseUri, ku).AbsoluteUri; // key URI 按 base 解析
                        string ivs = Attr(a, "IV");
                        if (ivs.Length > 0) ks.Iv = ParseHexIv(ivs);
                        curKey = ks;
                    }
                }
                else if (IsTag(line, "#EXT-X-MAP"))
                {
                    int colon = line.IndexOf(':');
                    var a = ParseAttrs(colon >= 0 ? line.Substring(colon + 1) : "");
                    string mu = Attr(a, "URI");
                    if (mu.Length > 0)
                    {
                        var init = new InitSpec { Url = new Uri(baseUri, mu).AbsoluteUri };
                        string br = Attr(a, "BYTERANGE");
                        if (br.Length > 0)
                        {
                            var (bl, bo) = ParseByteRange(br);
                            init.Length = bl;
                            init.Offset = bo ?? 0;
                        }
                        // 按 (URL, offset, length) 去重，保留首次出现顺序
                        InitSpec? existing = media.Inits.FirstOrDefault(x => x.Id == init.Id);
                        if (existing != null) curInit = existing;
                        else { media.Inits.Add(init); curInit = init; }
                    }
                }
                else if (IsTag(line, "#EXT-X-BYTERANGE"))
                {
                    int colon = line.IndexOf(':');
                    string v = colon >= 0 ? line.Substring(colon + 1).Trim() : "";
                    var (bl, bo) = ParseByteRange(v);
                    pendingLen = bl;
                    pendingOff = bo; // null 表示起始 = 上一段结束
                }
                else if (IsTag(line, "#EXT-X-MEDIA-SEQUENCE"))
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0 && long.TryParse(line.Substring(colon + 1).Trim(),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                        seq = ms;
                }
                else if (IsTag(line, "#EXT-X-ENDLIST"))
                {
                    // VOD 结束标记：识别即可，循环读完所有行，不影响已有分片
                }
                // 其它标签（#EXTINF、#EXT-X-VERSION、#EXT-X-TARGETDURATION、
                // #EXT-X-DISCONTINUITY、#EXT-X-MEDIA、注释行等）一律忽略
                continue;
            }

            // 非注释行 = URI
            if (pendingStream != null)
            {
                // master 的 variant URI
                var v = new Variant { Uri = new Uri(baseUri, line).AbsoluteUri };
                string bw = Attr(pendingStream, "BANDWIDTH");
                if (long.TryParse(bw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b))
                    v.Bandwidth = b;
                string res = Attr(pendingStream, "RESOLUTION");
                if (res.Length > 0)
                {
                    v.Resolution = res;
                    v.ResolutionArea = ParseResolutionArea(res);
                }
                result.Variants.Add(v);
                pendingStream = null;
                continue;
            }

            // media 分片
            var seg = new Segment
            {
                Url = new Uri(baseUri, line).AbsoluteUri, // 支持相对 URL、绝对 URL、带 query
                Key = curKey,
                Init = curInit,
                Sequence = seq,
            };
            if (pendingLen.HasValue)
            {
                long off = pendingOff ?? lastRangeEnd;
                seg.Offset = off;
                seg.Length = pendingLen;
                lastRangeEnd = off + pendingLen.Value;
            }
            else
            {
                lastRangeEnd = 0;
            }
            media.Segments.Add(seg);
            seq++;
            pendingLen = null;
            pendingOff = null;
            if (curKey != null) media.Encrypted = true;
        }

        result.IsMaster = isMaster && result.Variants.Count > 0;
        return result;
    }

    // ==================================================================
    // 密钥 / init segment 准备
    // ==================================================================

    private static async Task PrepareKeysAsync(MediaPlaylist pl, Action<string> log, CancellationToken ct)
    {
        // 按 URL 去重，同一个 key 最多取一次内容
        var cache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (Segment seg in pl.Segments)
        {
            KeySpec? k = seg.Key;
            if (!IsAes128(k) || string.IsNullOrEmpty(k!.Uri)) continue;
            if (k.KeyBytes != null) continue; // 多个分片共享同一个 KeySpec 引用
            string url = k.Uri;
            if (!cache.TryGetValue(url, out byte[]? keyBytes))
            {
                byte[] raw = await FetchWithRetryAsync(new Uri(url), null, null, ct).ConfigureAwait(false);
                if (raw.Length < 16)
                    throw new InvalidOperationException(
                        $"HLS 密钥长度不足（{raw.Length} 字节，AES-128 需要 16 字节）：{url}");
                keyBytes = raw.Length == 16 ? raw : raw.AsSpan(0, 16).ToArray();
                cache[url] = keyBytes!;
            }
            k.KeyBytes = keyBytes;
        }
        log($"[HLS] AES-128 解密：{(pl.Encrypted ? $"启用，密钥 {cache.Count} 个（按 URL 去重，各取一次）" : "未启用")}");
    }

    private static async Task PrepareInitsAsync(MediaPlaylist pl, Action<string> log, CancellationToken ct)
    {
        foreach (InitSpec init in pl.Inits)
            init.Data = await FetchWithRetryAsync(new Uri(init.Url), init.Offset, init.Length, ct).ConfigureAwait(false);
        if (pl.Inits.Count > 0)
            log($"[HLS] init segment（#EXT-X-MAP）{pl.Inits.Count} 个，已下载且不解密");
    }

    // ==================================================================
    // 并发分片下载
    // ==================================================================

    private static async Task DownloadBatchAsync(
        List<Segment> segs, byte[][] buffers, int start, int end, int concurrency,
        ProgressState state, Action<long, long, double> progress, CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(concurrency);

        async Task DownloadOneAsync(int idx)
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Segment seg = segs[idx];
                byte[] raw = await FetchWithRetryAsync(new Uri(seg.Url), seg.Offset, seg.Length, ct).ConfigureAwait(false);
                byte[] output = DecryptIfNeeded(seg, raw);
                buffers[idx] = output; // 存到原始顺序对应的下标，避免并发 seek 竞争
                ReportProgress(state, output.Length, progress);
            }
            finally
            {
                sem.Release();
            }
        }

        var tasks = new Task[end - start];
        for (int i = start; i < end; i++) tasks[i - start] = DownloadOneAsync(i);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<byte[]> FetchWithRetryAsync(Uri url, long? offset, long? length, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await FetchOnceAsync(url, offset, length, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 真正的取消，直接上抛
            }
            catch (Exception ex)
            {
                last = ex; // 超时（TaskCanceledException）等也在此重试
                if (attempt < 3)
                    await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        throw new IOException($"下载失败（已重试 3 次）：{url.AbsoluteUri}，原因：{last?.Message}", last);
    }

    private static async Task<byte[]> FetchOnceAsync(Uri url, long? offset, long? length, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        long? rangeStart = null, rangeLen = null;
        if (offset is long off && length is long len && len > 0)
        {
            rangeStart = off;
            rangeLen = len;
            req.Headers.Range = new RangeHeaderValue(off, off + len - 1);
        }

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        byte[] data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // 服务器忽略了 Range（返回 200 而非 206）时，手动按区间切片
        if (rangeStart is long startOff && rangeLen is long wantLen &&
            resp.StatusCode != HttpStatusCode.PartialContent)
        {
            if (startOff >= data.Length) return Array.Empty<byte>();
            long take = Math.Min(wantLen, data.Length - startOff);
            var slice = new byte[(int)take];
            Array.Copy(data, startOff, slice, 0, take);
            return slice;
        }
        return data;
    }

    // ==================================================================
    // AES-128 解密
    // ==================================================================

    private static byte[] DecryptIfNeeded(Segment seg, byte[] raw)
    {
        KeySpec? k = seg.Key;
        if (k == null || string.Equals(k.Method, "NONE", StringComparison.OrdinalIgnoreCase))
            return raw; // 未加密分片写原始字节
        if (!IsAes128(k))
            throw new NotSupportedException($"不支持的 HLS 加密方式 {k.Method}（仅支持 AES-128）：{seg.Url}");
        if (k.KeyBytes == null)
            throw new InvalidOperationException($"分片缺少解密密钥：{seg.Url}");

        // 显式 IV 优先；否则按该分片 media sequence number 生成大端 IV
        byte[] iv = k.Iv ?? ComputeIv(seg.Sequence);
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = k.KeyBytes;
        aes.IV = iv;
        using ICryptoTransform dec = aes.CreateDecryptor();
        try
        {
            return dec.TransformFinalBlock(raw, 0, raw.Length);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"分片 AES-128 解密失败（密钥或 IV 不匹配，或数据非 16 字节对齐）：{seg.Url}，{ex.Message}", ex);
        }
    }

    /// <summary>16 字节大端 IV：前 8 字节为 0，低 8 字节为该分片序号。</summary>
    private static byte[] ComputeIv(long sequence)
    {
        var iv = new byte[16];
        ulong v = (ulong)sequence;
        for (int i = 0; i < 8; i++)
            iv[15 - i] = (byte)((v >> (8 * i)) & 0xFF);
        return iv;
    }

    // ==================================================================
    // 进度
    // ==================================================================

    private static void ReportProgress(ProgressState s, int segLen, Action<long, long, double> progress)
    {
        lock (s.Gate) // 串行化回调，避免 progress 被并发调用
        {
            s.DoneBytes += segLen;
            s.DoneCount++;
            double now = s.Watch.Elapsed.TotalSeconds;
            double dt = now - s.LastSeconds;
            if (dt >= 0.1)
            {
                double inst = (s.DoneBytes - s.LastBytes) / dt;
                s.Speed = s.Speed <= 0 ? inst : s.Speed * 0.6 + inst * 0.4; // 简单 (增量/耗时) 平滑
                s.LastBytes = s.DoneBytes;
                s.LastSeconds = now;
            }
            long avg = s.DoneCount > 0 ? s.DoneBytes / s.DoneCount : 0;
            long estimatedTotal = s.DoneBytes + (long)(s.TotalCount - s.DoneCount) * avg;
            try { progress(s.DoneBytes, estimatedTotal, s.Speed); }
            catch { /* 进度回调异常不应中断下载 */ }
        }
    }

    // ==================================================================
    // 小工具
    // ==================================================================

    private static bool IsTag(string line, string tag)
    {
        if (!line.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) return false;
        return line.Length == tag.Length || line[tag.Length] == ':' || char.IsWhiteSpace(line[tag.Length]);
    }

    /// <summary>解析 KEY=VALUE 属性列表，支持带引号（内部逗号不切断）的值。</summary>
    private static Dictionary<string, string> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == ',' || char.IsWhiteSpace(s[i]))) i++;
            if (i >= s.Length) break;
            int eq = s.IndexOf('=', i);
            int comma = s.IndexOf(',', i);
            if (eq < 0) break;
            if (comma >= 0 && comma < eq) { i = comma + 1; continue; } // 无等号属性，跳过
            string key = s.Substring(i, eq - i).Trim();
            i = eq + 1;
            string val;
            if (i < s.Length && s[i] == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) { val = s.Substring(i + 1); i = s.Length; }
                else { val = s.Substring(i + 1, end - i - 1); i = end + 1; }
            }
            else
            {
                int c = s.IndexOf(',', i);
                if (c < 0) { val = s.Substring(i).Trim(); i = s.Length; }
                else { val = s.Substring(i, c - i).Trim(); i = c + 1; }
            }
            if (key.Length > 0) d[key] = val;
        }
        return d;
    }

    private static string Attr(Dictionary<string, string> a, string key)
        => a.TryGetValue(key, out string? v) ? (v ?? "") : "";

    /// <summary>解析 BYTERANGE：length[@offset]，返回 (长度, 可空起始偏移)。</summary>
    private static (long Length, long? Offset) ParseByteRange(string s)
    {
        s = s.Trim().Trim('"');
        string[] parts = s.Split('@');
        long len = 0;
        if (parts.Length > 0)
            long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out len);
        long? off = null;
        if (parts.Length > 1 &&
            long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long o))
            off = o;
        return (len, off);
    }

    private static long ParseResolutionArea(string s)
    {
        string[] p = s.Split('x', 'X');
        if (p.Length == 2 &&
            long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long w) &&
            long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long h))
            return w * h;
        return 0;
    }

    /// <summary>解析 0x 开头的十六进制 IV，失败返回 null（回退到按序号计算）。</summary>
    private static byte[]? ParseHexIv(string s)
    {
        try
        {
            string h = s.Trim();
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            h = h.Replace(" ", "");
            if (h.Length % 2 != 0) h = "0" + h;
            if (h.Length < 32) h = h.PadLeft(32, '0');
            byte[] iv = Convert.FromHexString(h);
            if (iv.Length > 16) return iv.AsSpan(iv.Length - 16, 16).ToArray();
            return iv;
        }
        catch { return null; }
    }

    private static bool IsAes128(KeySpec? k)
        => k != null && string.Equals(k.Method, "AES-128", StringComparison.OrdinalIgnoreCase);

    // ==================================================================
    // 内部数据结构
    // ==================================================================

    private sealed class KeySpec
    {
        public string Method = "";
        public string? Uri;        // 绝对 URL
        public byte[]? Iv;         // playlist 显式 IV；null 表示按序号计算
        public byte[]? KeyBytes;   // 16 字节密钥内容
    }

    private sealed class InitSpec
    {
        public string Url = "";
        public long? Offset;
        public long? Length;
        public byte[]? Data;
        public string Id => $"{Url}|{Offset}|{Length}";
    }

    private sealed class Segment
    {
        public string Url = "";
        public long? Offset;
        public long? Length;
        public KeySpec? Key;
        public long Sequence;
        public InitSpec? Init; // 关联的 init segment
    }

    private sealed class Variant
    {
        public string Uri = "";
        public long Bandwidth;
        public string? Resolution;
        public long ResolutionArea;
    }

    private sealed class MediaPlaylist
    {
        public readonly List<Segment> Segments = new();
        public readonly List<InitSpec> Inits = new(); // 去重后按出现顺序
        public bool Encrypted;
    }

    private sealed class ParsedPlaylist
    {
        public bool IsMaster;
        public readonly List<Variant> Variants = new();
        public readonly MediaPlaylist Media = new();
    }

    private sealed class ProgressState
    {
        public long DoneBytes;
        public int DoneCount;
        public int TotalCount;
        public long LastBytes;
        public double LastSeconds;
        public double Speed;
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public readonly object Gate = new();
    }
}
