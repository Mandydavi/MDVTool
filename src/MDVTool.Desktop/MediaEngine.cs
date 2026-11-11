using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

public sealed class MediaInfo
{
    public string Title { get; set; } = "";
    public string VideoUrl { get; set; } = "";
    public string AudioUrl { get; set; } = "";   // 空字符串表示音视频已合并或不需要分离
    public string Ext { get; set; } = "mp4";
    public bool NeedsMerge { get; set; }
    public bool IsHls { get; set; }              // VideoUrl 是 m3u8
    public Dictionary<string, string> Headers { get; set; } = new();
    public long Duration { get; set; }
}

/// <summary>
/// yt-dlp 解析 + ffmpeg 合并/转封装。只依赖 .NET BCL 与外部的 yt-dlp.exe / ffmpeg.exe。
/// 覆盖 yt-dlp 支持的 1800+ 站点；普通直链仍交给 aria2。
/// </summary>
public static class MediaEngine
{
    // 只缓存"找到"的结果：用户按提示把 exe 放到同目录后，重试能立刻生效，不会被缓存的 null 卡住。
    private static string? _ytDlp;
    private static string? _ffmpeg;

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MergeTimeout = TimeSpan.FromMinutes(30);

    private const string FfmpegMissingMessage =
        "未找到 ffmpeg.exe，无法合并/转封装音视频。\n" +
        "请把 ffmpeg.exe 放到程序同目录，或安装到环境变量 PATH。\n" +
        "推荐用 winget 安装：winget install Gyan.FFmpeg（程序会自动检索 WinGet 安装目录）。\n" +
        "下载地址：https://www.gyan.dev/ffmpeg/builds/";

    private static string YtDlpMissingMessage() =>
        "未找到 yt-dlp.exe，无法解析该网页/媒体地址。\n" +
        "请到 https://github.com/yt-dlp/yt-dlp/releases 下载 yt-dlp.exe 并放到程序同目录：" + AppContext.BaseDirectory + "\n" +
        "可供程序自动下载的直链：https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    // ------------------------------------------------------------------ 站点判定

    private static readonly string[] MediaHosts =
    {
        "youtube.com", "youtu.be", "youtube-nocookie.com",
        "twitter.com", "x.com",
        "bilibili.com", "b23.tv",
        "tiktok.com",
        "reddit.com", "redd.it",
        "vimeo.com",
        "twitch.tv",
        "douyin.com",
        "weibo.com",
        "instagram.com",
        "facebook.com", "fb.watch",
        "nicovideo.jp", "nico.ms",
    };

    public static bool IsMediaPage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var s = url.Trim();

        // huggingface 等模型站明确排除，避免误伤 aria2 的模型直链下载。
        Uri? uri = null;
        if (Uri.TryCreate(s, UriKind.Absolute, out uri))
        {
            var h = uri.Host.ToLowerInvariant();
            if (h == "huggingface.co" || h.EndsWith(".huggingface.co", StringComparison.Ordinal))
                return false;
        }

        // HLS / DASH 清单地址（含查询串里出现的清单）。
        if (s.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf(".mpd", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // 常见视频站 host（含 www./m. 等子域）。
        if (uri != null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var host = uri.Host.ToLowerInvariant();
            foreach (var m in MediaHosts)
                if (host == m || host.EndsWith("." + m, StringComparison.Ordinal)) return true;
            if (host.IndexOf("niconico", StringComparison.Ordinal) >= 0) return true;
        }

        // 其余一律视为直链（.mp4/.mkv/.zip/.safetensors ...），交给 aria2。
        return false;
    }

    // ------------------------------------------------------------------ 查找外部工具

    public static string? FindYtDlp()
    {
        if (_ytDlp != null && File.Exists(_ytDlp)) return _ytDlp;
        var found = LocateYtDlp();
        if (found != null) _ytDlp = found;
        return found;
    }

    public static string? FindFfmpeg()
    {
        if (_ffmpeg != null && File.Exists(_ffmpeg)) return _ffmpeg;
        var found = LocateFfmpeg();
        if (found != null) _ffmpeg = found;
        return found;
    }

    private static string? LocateYtDlp()
    {
        // 1. 程序同目录
        var local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(local)) return local;

        // 2. 环境变量 PATH
        var inPath = FindInPath("yt-dlp.exe");
        if (inPath != null) return inPath;

        // 3. winget 安装目录（包名含 yt-dlp，版本号不固定，递归找）
        var inWinGet = FindInWinGet(new[] { "*yt-dlp*" }, "yt-dlp.exe");
        if (inWinGet != null) return inWinGet;

        // 4. 常见手工安装位置
        var pf = @"C:\Program Files\yt-dlp\yt-dlp.exe";
        if (File.Exists(pf)) return pf;

        return null;
    }

    private static string? LocateFfmpeg()
    {
        // 1. 程序同目录
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        // 2. 环境变量 PATH
        var inPath = FindInPath("ffmpeg.exe");
        if (inPath != null) return inPath;

        // 3. winget：Gyan.FFmpeg_* / *ffmpeg* 包，递归覆盖
        //    ...\Gyan.FFmpeg_*\ffmpeg-*\bin\ffmpeg.exe 这种形态。
        var inWinGet = FindInWinGet(new[] { "Gyan.FFmpeg_*", "*ffmpeg*" }, "ffmpeg.exe");
        if (inWinGet != null) return inWinGet;

        // 4. 常见手工安装位置
        foreach (var p in new[]
                 {
                     @"C:\ffmpeg\bin\ffmpeg.exe",
                     @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                 })
            if (File.Exists(p)) return p;

        return null;
    }

    private static string? FindInPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                var exe = Path.Combine(dir, exeName);
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    private static string? FindInWinGet(string[] namePatterns, string exeName)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(root)) return null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in namePatterns)
            {
                foreach (var pkg in Directory.EnumerateDirectories(root, pattern))
                {
                    if (!seen.Add(pkg)) continue;
                    var hit = FindRecursive(pkg, exeName, 0);
                    if (hit != null) return hit;
                }
            }
        }
        catch { }
        return null;
    }

    // 逐目录递归，单个目录无权限时跳过而不是整体失败；限制深度防止异常目录树。
    private static string? FindRecursive(string dir, string exeName, int depth)
    {
        if (depth > 8) return null;
        try
        {
            var direct = Path.Combine(dir, exeName);
            if (File.Exists(direct)) return direct;
        }
        catch { }
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var hit = FindRecursive(sub, exeName, depth + 1);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ 解析

    public static async Task<MediaInfo> ResolveAsync(string url, string quality, Action<string> log, CancellationToken ct)
    {
        var exe = FindYtDlp();
        if (exe == null) throw new InvalidOperationException(YtDlpMissingMessage());

        var q = (quality ?? "").Trim();
        bool audioMode = q.Equals("audio", StringComparison.OrdinalIgnoreCase);
        var fmt = FormatFor(q);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try { psi.Environment["PYTHONIOENCODING"] = "utf-8"; } catch { }

        foreach (var a in new[]
                 {
                     "--dump-single-json", "--no-playlist", "--no-warnings", "--no-part",
                     "--skip-download", "-f", fmt, url
                 })
            psi.ArgumentList.Add(a);

        SafeLog(log, "[yt-dlp] 解析中: " + url + "  (-f " + fmt + ")");
        var (exit, stdout, stderr) = await RunAsync(psi, log, "yt-dlp", ResolveTimeout, ct).ConfigureAwait(false);

        if (exit != 0)
            throw new InvalidOperationException("yt-dlp 解析失败（退出码 " + exit + "）：" + Brief(stderr));
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("yt-dlp 未返回任何元数据：" + Brief(stderr));

        using var doc = ParseJson(stdout);
        var root = doc.RootElement;
        var info = new MediaInfo
        {
            Title = CleanTitle(Str(root, "title")),
        };
        if (Num(root, "duration") is double dur) info.Duration = (long)dur;
        var topExt = Str(root, "ext");
        if (!string.IsNullOrEmpty(topExt)) info.Ext = topExt!;

        // 优先用 yt-dlp 选中的格式；没有时回退到顶层 url / formats。
        if (root.TryGetProperty("requested_downloads", out var reqs) &&
            reqs.ValueKind == JsonValueKind.Array && reqs.GetArrayLength() > 0)
            ParseRequestedDownloads(root, reqs, info);
        else
            ParseFallback(root, info, q, log);

        if (audioMode)
        {
            // 纯音频：只返回一个 URL，放进 VideoUrl 作为主下载地址，Ext 按约定置 m4a。
            var primary = !string.IsNullOrEmpty(info.VideoUrl) ? info.VideoUrl : info.AudioUrl;
            info.VideoUrl = primary;
            info.AudioUrl = "";
            info.NeedsMerge = false;
            info.Ext = "m4a";
        }
        else
        {
            info.NeedsMerge = !string.IsNullOrEmpty(info.VideoUrl) && !string.IsNullOrEmpty(info.AudioUrl);
            // 合并/转封装的输出统一交给 ffmpeg 写 mp4。
            if (info.NeedsMerge || info.IsHls) info.Ext = "mp4";
        }

        if (ContainsM3u8(info.VideoUrl) || ContainsM3u8(info.AudioUrl)) info.IsHls = true;
        if (string.IsNullOrEmpty(info.VideoUrl))
            throw new InvalidOperationException("未能从 yt-dlp 结果中取得可用的下载地址：" + Brief(stderr));

        SafeLog(log, "[yt-dlp] 解析完成: " + info.Title + " | Ext=" + info.Ext +
                     " | 需合并=" + info.NeedsMerge + " | HLS=" + info.IsHls);
        return info;
    }

    private static string FormatFor(string quality)
    {
        var q = (quality ?? "").Trim();
        if (q.Length == 0 || q.Equals("best", StringComparison.OrdinalIgnoreCase))
            return "bv*+ba/b";
        if (q.Equals("audio", StringComparison.OrdinalIgnoreCase))
            return "ba";
        if (IsAllDigits(q))
            return "bv*[height<=" + q + "]+ba/b[height<=" + q + "]";
        // 允许调用方直接传一个 yt-dlp 的 -f 表达式。
        return q;
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    // 解析 requested_downloads：vcodec=="none" 为音频流，acodec=="none" 为视频流，两者都在则是已合并流。
    private static void ParseRequestedDownloads(JsonElement root, JsonElement reqs, MediaInfo info)
    {
        CollectHeaders(root, info.Headers);   // 顶层头作为基础
        foreach (var e in reqs.EnumerateArray())
        {
            var url = Str(e, "url");
            if (string.IsNullOrEmpty(url)) continue;

            bool audioOnly = IsNone(Str(e, "vcodec"));
            CollectHeaders(e, info.Headers); // 每个流的头覆盖/补充
            var protocol = Str(e, "protocol");
            if (!string.IsNullOrEmpty(protocol) &&
                protocol!.IndexOf("m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
                info.IsHls = true;

            if (audioOnly)
            {
                if (string.IsNullOrEmpty(info.AudioUrl)) info.AudioUrl = url!;
            }
            else
            {
                // 视频流或已合并流都作为主下载地址
                if (string.IsNullOrEmpty(info.VideoUrl))
                {
                    info.VideoUrl = url!;
                    var ext = Str(e, "ext");
                    if (!string.IsNullOrEmpty(ext)) info.Ext = ext!;
                }
                else if (string.IsNullOrEmpty(info.AudioUrl))
                {
                    info.AudioUrl = url!;   // 极端情况：多条视频流，退当第二路
                }
            }
        }
    }

    // 回退：读顶层 url / title / ext / http_headers，并从 formats 里挑最优。
    private static void ParseFallback(JsonElement root, MediaInfo info, string quality, Action<string> log)
    {
        CollectHeaders(root, info.Headers);
        var topUrl = Str(root, "url");

        int? maxHeight = null;
        if (IsAllDigits(quality) && int.TryParse(quality, out var mh)) maxHeight = mh;

        if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
        {
            JsonElement? bestCombined = null, bestVideo = null, bestAudio = null;
            double sC = -1, sV = -1, sA = -1;
            foreach (var f in fmts.EnumerateArray())
            {
                var url = Str(f, "url");
                if (string.IsNullOrEmpty(url)) continue;
                bool audioOnly = IsNone(Str(f, "vcodec"));
                bool videoOnly = IsNone(Str(f, "acodec"));
                if (maxHeight.HasValue && !audioOnly && (Num(f, "height") ?? 0) > maxHeight.Value) continue;

                double sc = Score(f);
                if (!audioOnly && !videoOnly) { if (sc > sC) { sC = sc; bestCombined = f; } }
                else if (videoOnly) { if (sc > sV) { sV = sc; bestVideo = f; } }
                else { if (sc > sA) { sA = sc; bestAudio = f; } }
            }

            if (bestCombined.HasValue)
                ApplyFormat(bestCombined.Value, info, true);
            else if (bestVideo.HasValue && bestAudio.HasValue)
            {
                ApplyFormat(bestVideo.Value, info, true);
                ApplyFormat(bestAudio.Value, info, false);
            }
            else if (bestVideo.HasValue)
                ApplyFormat(bestVideo.Value, info, true);
            else if (bestAudio.HasValue)
                ApplyFormat(bestAudio.Value, info, true);   // 仅音频
            else if (!string.IsNullOrEmpty(topUrl))
                info.VideoUrl = topUrl!;
        }
        else if (!string.IsNullOrEmpty(topUrl))
        {
            info.VideoUrl = topUrl!;
        }

        if (string.IsNullOrEmpty(info.VideoUrl) && string.IsNullOrEmpty(info.AudioUrl) &&
            !string.IsNullOrEmpty(topUrl))
            info.VideoUrl = topUrl!;

        if (string.IsNullOrEmpty(info.VideoUrl) && string.IsNullOrEmpty(info.AudioUrl))
            SafeLog(log, "[yt-dlp] 警告：formats 里没有可用 url，已回退到顶层 url。");
    }

    private static double Score(JsonElement f) =>
        (Num(f, "height") ?? 0) * 1000 +
        (Num(f, "width") ?? 0) +
        (Num(f, "tbr") ?? 0) +
        (Num(f, "fps") ?? 0);

    private static void ApplyFormat(JsonElement f, MediaInfo info, bool asVideo)
    {
        var url = Str(f, "url") ?? "";
        var ext = Str(f, "ext");
        var protocol = Str(f, "protocol");
        CollectHeaders(f, info.Headers);
        if (!string.IsNullOrEmpty(protocol) &&
            protocol!.IndexOf("m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
            info.IsHls = true;
        if (asVideo)
        {
            info.VideoUrl = url;
            if (!string.IsNullOrEmpty(ext)) info.Ext = ext!;
        }
        else
        {
            info.AudioUrl = url;
        }
    }

    // ------------------------------------------------------------------ 合并 / 转封装

    public static async Task<bool> MergeAsync(string videoPath, string audioPath, string outputPath,
                                             Action<string> log, CancellationToken ct)
    {
        var exe = FindFfmpeg();
        if (exe == null) throw new InvalidOperationException(FfmpegMissingMessage);
        EnsureOutputDir(outputPath, log);

        var psi = FfmpegPsi(exe);
        foreach (var a in new[]
                 {
                     "-y", "-i", videoPath, "-i", audioPath,
                     "-c", "copy", "-movflags", "+faststart", outputPath
                 })
            psi.ArgumentList.Add(a);

        SafeLog(log, "[ffmpeg] 合并: " + Path.GetFileName(videoPath) + " + " + Path.GetFileName(audioPath));
        var (exit, _, _) = await RunAsync(psi, log, "ffmpeg", MergeTimeout, ct).ConfigureAwait(false);
        if (exit != 0) SafeLog(log, "[ffmpeg] 合并失败，退出码 " + exit);
        else SafeLog(log, "[ffmpeg] 合并完成: " + Path.GetFileName(outputPath));
        return exit == 0;
    }

    public static async Task<bool> RemuxAsync(string inputPath, string outputPath,
                                             Action<string> log, CancellationToken ct)
    {
        var exe = FindFfmpeg();
        if (exe == null) throw new InvalidOperationException(FfmpegMissingMessage);
        EnsureOutputDir(outputPath, log);

        var psi = FfmpegPsi(exe);
        foreach (var a in new[]
                 {
                     "-y", "-i", inputPath,
                     "-c", "copy", "-movflags", "+faststart", outputPath
                 })
            psi.ArgumentList.Add(a);

        SafeLog(log, "[ffmpeg] 转封装: " + Path.GetFileName(inputPath) + " -> " + Path.GetFileName(outputPath));
        var (exit, _, _) = await RunAsync(psi, log, "ffmpeg", MergeTimeout, ct).ConfigureAwait(false);
        if (exit != 0) SafeLog(log, "[ffmpeg] 转封装失败，退出码 " + exit);
        else SafeLog(log, "[ffmpeg] 转封装完成: " + Path.GetFileName(outputPath));
        return exit == 0;
    }

    private static ProcessStartInfo FfmpegPsi(string exe) => new(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static void EnsureOutputDir(string outputPath, Action<string> log)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            SafeLog(log, "[ffmpeg] 创建输出目录失败: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 内核自举 / DASH 兜底

    private const string YtDlpReleaseUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    /// <summary>首次用到视频解析时自动获取 yt-dlp.exe（约 11 MB），之后一直复用。</summary>
    public static async Task<string> EnsureYtDlpAsync(Action<string> log, CancellationToken ct)
    {
        var found = FindYtDlp();
        if (found != null) return found;

        string target = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        string staging = target + ".new";
        SafeLog(log, "首次使用视频解析，正在自动获取 yt-dlp ...");
        try
        {
            // 共享的 ComfyNet.Http 只有 40 秒超时，下内核可能不够；这里单独给 5 分钟
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MDVTool/3.0");
            using var resp = await client.GetAsync(
                YtDlpReleaseUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using (var fs = File.Create(staging))
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);

            long size = new FileInfo(staging).Length;
            if (size < 1024 * 1024)
                throw new InvalidOperationException($"下载的 yt-dlp.exe 只有 {size} 字节，可能不是可执行文件");

            if (File.Exists(target)) File.Delete(target);
            File.Move(staging, target);
            _ytDlp = target;
            SafeLog(log, "yt-dlp 已就绪：" + target);
            return target;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { }
            throw new InvalidOperationException(
                "自动获取 yt-dlp 失败：" + ex.Message + "\n" + YtDlpMissingMessage(), ex);
        }
    }

    /// <summary>
    /// DASH(.mpd) 这类 aria2 不能直接吃的清单，交给 yt-dlp 按分片并发下载。
    /// 传入原始页面/清单地址，由 yt-dlp 自己完成解析、分片与合并。
    /// </summary>
    public static async Task<bool> DownloadWithYtDlpAsync(
        string pageUrl, string outputPath, string quality, Action<string> log, CancellationToken ct)
    {
        string exe = await EnsureYtDlpAsync(log, ct).ConfigureAwait(false);
        EnsureOutputDir(outputPath, log);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try { psi.Environment["PYTHONIOENCODING"] = "utf-8"; } catch { }

        foreach (var a in new[]
                 {
                     "--no-playlist", "--no-part", "--no-warnings", "--force-overwrites",
                     "--concurrent-fragments", "16",
                     "--retries", "10", "--fragment-retries", "10",
                     "-f", FormatFor(quality),
                     "-o", outputPath, pageUrl
                 })
            psi.ArgumentList.Add(a);

        SafeLog(log, "[yt-dlp] DASH / 流媒体分片下载中：" + pageUrl);
        // 整片视频可能很大，给足 6 小时；取消仍然由 ct 立即生效
        var (exit, _, stderr) = await RunAsync(
            psi, log, "yt-dlp", TimeSpan.FromHours(6), ct).ConfigureAwait(false);
        if (exit != 0) SafeLog(log, "[yt-dlp] 下载失败（退出码 " + exit + "）：" + Brief(stderr));
        return exit == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    // ------------------------------------------------------------------ 进程执行

    // 同时抽取 stdout/stderr，避免管道写满造成死锁；带超时与取消，超时/取消时 kill 整棵进程树。
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        ProcessStartInfo psi, Action<string> log, string tag, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var token = linked.Token;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException(tag + " 启动失败");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(tag + " 启动失败：" + ex.Message, ex);
        }

        using (proc)
        {
            var outTask = proc.StandardOutput.ReadToEndAsync(token);
            var errTask = proc.StandardError.ReadToEndAsync(token);
            using var reg = token.Register(() => KillProcess(proc));

            string stdout, stderr;
            try
            {
                await proc.WaitForExitAsync(token).ConfigureAwait(false);
                stdout = await outTask.ConfigureAwait(false);
                stderr = await errTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 进程已被 kill，管道关闭；把两个读任务收尾，避免 Dispose 时仍有挂起的异步读。
                stdout = await Quiet(outTask).ConfigureAwait(false);
                stderr = await Quiet(errTask).ConfigureAwait(false);
                LogStream(log, stderr, tag);
                if (ct.IsCancellationRequested) throw;   // 用户主动取消
                throw new TimeoutException(tag + " 执行超时（" + (int)timeout.TotalSeconds + " 秒），已终止进程。");
            }

            LogStream(log, stderr, tag);
            return (proc.ExitCode, stdout, stderr);
        }
    }

    private static async Task<string> Quiet(Task<string> t)
    {
        try { return await t.ConfigureAwait(false); }
        catch { return ""; }
    }

    private static void KillProcess(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { }
    }

    private static void LogStream(Action<string> log, string? text, string tag)
    {
        if (string.IsNullOrEmpty(text)) return;
        int written = 0;
        foreach (var raw in text.Replace("\r", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // ffmpeg 的进度行刷屏，跳过，保留真正的日志/报错。
            if (line.StartsWith("frame=", StringComparison.Ordinal) && line.Contains("time=")) continue;
            if (written >= 60) { SafeLog(log, "[" + tag + "] ...（日志已截断）"); break; }
            SafeLog(log, "[" + tag + "] " + line);
            written++;
        }
    }

    private static void SafeLog(Action<string>? log, string msg)
    {
        try { log?.Invoke(msg); } catch { }
    }

    // ------------------------------------------------------------------ JSON / 文本小工具

    private static JsonDocument ParseJson(string stdout)
    {
        var s = stdout.Trim();
        int i = s.IndexOf('{');
        if (i > 0) s = s.Substring(i);   // 跳过误打到 stdout 的前导行
        try
        {
            return JsonDocument.Parse(s);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("yt-dlp 返回的 JSON 无法解析：" + ex.Message + "\n" + Brief(s));
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetDouble(out var d)
            ? d
            : (double?)null;

    private static bool IsNone(string? s) =>
        s != null && s.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static void CollectHeaders(JsonElement obj, Dictionary<string, string> target)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        if (!obj.TryGetProperty("http_headers", out var h) || h.ValueKind != JsonValueKind.Object) return;
        foreach (var p in h.EnumerateObject())
        {
            var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
            if (!string.IsNullOrEmpty(v)) target[p.Name] = v!;
        }
    }

    private static bool ContainsM3u8(string? s) =>
        !string.IsNullOrEmpty(s) && s.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

    private static readonly char[] BadFileNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private static string CleanTitle(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "video";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsControl(ch) || Array.IndexOf(BadFileNameChars, ch) >= 0) sb.Append('_');
            else sb.Append(ch);
        }
        var r = sb.ToString().Trim().TrimEnd(' ', '.');
        if (r.Length > 120) r = r.Substring(0, 120);
        return r.Length == 0 ? "video" : r;
    }

    private static string Brief(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(无输出)";
        var t = s.Trim();
        return t.Length > 800 ? t.Substring(0, 800) + "..." : t;
    }
}
