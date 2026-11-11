using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>aria2c 引擎：16 线程分段 + 断点续传 + 进度解析</summary>
public static class Aria2
{
    private static string? _cached;

    public static string? Find() => FindInternal(useCustom: true);

    /// <summary>忽略用户自定义路径，只做自动检测（"恢复自动检测"时用）</summary>
    public static string? AutoFind() => FindInternal(useCustom: false);

    /// <summary>改了引擎设置后必须调一下，否则 _cached 还指向旧文件</summary>
    public static void ResetCache() => _cached = null;

    /// <summary>给设置页用的一句话来源说明</summary>
    public static string Describe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return "未找到";
        if (string.Equals(Path.GetDirectoryName(exe)?.TrimEnd('\\', '/'),
                AppContext.BaseDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return "程序同目录（整个文件夹可直接拷走）";
        var custom = App.Settings?.Aria2Path;
        if (!string.IsNullOrWhiteSpace(custom) &&
            string.Equals(custom, exe, StringComparison.OrdinalIgnoreCase))
            return "自定义路径";
        if (exe.Contains(@"WinGet\Packages", StringComparison.OrdinalIgnoreCase))
            return "winget 安装（路径随版本变化）";
        if (exe.Contains(@"Program Files", StringComparison.OrdinalIgnoreCase))
            return "系统安装目录";
        return "环境变量 PATH";
    }

    private static string? FindInternal(bool useCustom)
    {
        if (useCustom && _cached != null && File.Exists(_cached)) return _cached;

        // 优先级 1：用户在设置里手动指定的引擎
        if (useCustom)
        {
            var custom = App.Settings?.Aria2Path;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            {
                _cached = custom;
                return custom;
            }
        }

        // 优先级 2：exe 同目录。命中就立刻返回，避免为探测去扫整个 WinGet 目录
        var beside = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        if (File.Exists(beside))
        {
            _cached = beside;
            return beside;
        }

        var candidates = new List<string>
        {
            @"C:\Program Files\aria2\aria2c.exe",
            @"C:\Program Files (x86)\aria2\aria2c.exe"
        };

        // winget 安装位置（版本号不固定）；只向下找两层，避免启动时递归整棵目录树
        try
        {
            var winget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WinGet\Packages");
            if (Directory.Exists(winget))
            {
                foreach (var d in Directory.GetDirectories(winget, "aria2*"))
                {
                    var direct = Path.Combine(d, "aria2c.exe");
                    if (File.Exists(direct)) { candidates.Add(direct); continue; }
                    foreach (var sub in Directory.GetDirectories(d))
                    {
                        var nested = Path.Combine(sub, "aria2c.exe");
                        if (File.Exists(nested)) candidates.Add(nested);
                        foreach (var sub2 in Directory.GetDirectories(sub))
                        {
                            var nested2 = Path.Combine(sub2, "aria2c.exe");
                            if (File.Exists(nested2)) candidates.Add(nested2);
                        }
                    }
                }
            }
        }
        catch { }

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                _cached = c;
                return c;
            }
        }

        // PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "aria2c.exe");
                if (File.Exists(exe))
                {
                    _cached = exe;
                    return exe;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// 按站点决定并发数。
    /// 实测 ModelScope 的 CDN 对同一 IP 的并发连接有惩罚性限速：
    /// 同一个 27GB 分片，16 线程只有 ~100 KB/s，4 并发 ~2.5 MB/s，单连接能跑到 13.6 MB/s。
    /// 所以手动模式走 ModelScope 时强制单连接，线程越多反而越慢。
    /// 自动模式（autoTuned）由 ComfyNet.PickConnectionsAsync 实测决定，不走这里。
    /// </summary>
    private static int ConnectionsForHost(string url, int configured, Action<string> log)
    {
        string host;
        try { host = new Uri(url).Host.ToLowerInvariant(); }
        catch { return configured; }

        if (host.Contains("modelscope"))
        {
            if (configured > 1)
                log($"    {host} 会限制并发，已把 {configured} 线程改为单连接（更快）");
            return 1;
        }
        return configured;
    }

    /// <summary>解析 aria2 的 .aria2 控制文件，得到 (已下载, 总大小)</summary>
    public static (long Done, long Total) ReadCtrl(string ctrlPath, long expectTotal)
    {
        var b = File.ReadAllBytes(ctrlPath);
        int o = 2;
        uint el = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4 + (int)el;
        uint ihl = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4 + (int)ihl;
        uint pieceLen = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4;
        ulong total = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));
        o += 16; // total + uploaded
        uint blen = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4;

        if (total == 0) total = (ulong)Math.Max(0, expectTotal);

        int avail = Math.Max(0, Math.Min((int)blen, b.Length - o));
        long done = 0;
        for (int i = 0; i < avail; i++) done += System.Numerics.BitOperations.PopCount(b[o + i]);

        if (pieceLen > 0)
        {
            long npieces = (long)Math.Ceiling(total / (double)pieceLen);
            if (npieces > 0) done = Math.Min(done, npieces);
            return (Math.Min(done * pieceLen, (long)total), (long)total);
        }
        return (0, (long)total);
    }

    /// <summary>限速与代理：所有下载（含 BT）共用，读当前设置</summary>
    private static List<string> CommonArgs()
    {
        var list = new List<string>();
        var s = App.Settings;

        if (s.SpeedLimitMBps > 0)
        {
            long bytes = (long)Math.Round(s.SpeedLimitMBps * 1024 * 1024);
            list.Add($"--max-overall-download-limit={bytes}");
        }

        var proxy = (s.ProxyUrl ?? "").Trim();
        if (proxy.Length > 0)
        {
            list.Add($"--all-proxy={proxy}");
        }
        return list;
    }

    /// <summary>
    /// 极速下载：同一文件的多个镜像一起交给 aria2，由它把分段摊到不同 URL，
    /// 实现带宽跨源叠加；连接数上限 64，并在速度明显掉下来时自动加线程重启续传。
    /// 取消时抛 OperationCanceledException。
    /// </summary>
    public static async Task<bool> DownloadAsync(
        DownloadTask task, string url, long totalHint, int connections, Action<string> log, CancellationToken ct,
        bool autoTuned = false,
        IReadOnlyList<string>? extraUrls = null,
        int perServer = 0,
        IReadOnlyDictionary<string, string>? headers = null,
        bool adaptiveBoost = true)
    {
        var aria = Find() ?? throw new InvalidOperationException(
            "下载组件还没就绪，程序正在后台自动获取，请稍等几秒后重试（或重启程序）");

        var destDir = Path.GetDirectoryName(task.DestPath)!;
        Directory.CreateDirectory(destDir);
        var ctrl = task.DestPath + ".aria2";

        // 多源去重复：主 URL 永远排第一
        var urls = new List<string> { url };
        if (extraUrls != null)
            foreach (var u in extraUrls)
                if (!string.IsNullOrWhiteSpace(u) &&
                    !urls.Contains(u, StringComparer.Ordinal)) urls.Add(u);

        connections = Math.Clamp(connections, 1, TurboSources.MaxConnections);
        // autoTuned：调用方已实测过各并发档位，直接采用，不再套用站点硬规则
        if (!autoTuned && urls.Count == 1)
            connections = ConnectionsForHost(url, connections, log);

        int attempts = 0, boosts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            task.Status = attempts == 1 ? TaskStatus.Connecting : TaskStatus.Downloading;
            task.SpeedBps = 0;

            var psi = BuildPsi(aria, destDir, task.FileName, urls, connections, perServer, headers);

            Process proc;
            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("aria2c 启动失败");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("aria2c 启动失败：" + ex.Message, ex);
            }

            task.Proc = proc;
            long lastDone = 0, prev = -1;
            var lastT = DateTime.UtcNow;
            var started = DateTime.UtcNow;
            double spd = 0, peak = 0;
            int slowTicks = 0;
            bool boostRequested = false;

            try
            {
                while (!proc.HasExited)
                {
                    await Task.Delay(400, ct);
                    ct.ThrowIfCancellationRequested();

                    if (!File.Exists(ctrl)) continue;

                    long done, total;
                    try
                    {
                        (done, total) = ReadCtrl(ctrl, totalHint);
                    }
                    catch
                    {
                        continue; // 正在重写控制文件
                    }

                    if (total <= 0) continue;
                    task.Total = total;
                    if (done == prev) continue;
                    prev = done;

                    var now = DateTime.UtcNow;
                    var dt = (now - lastT).TotalSeconds;
                    if (dt > 0.2)
                    {
                        var inst = (done - lastDone) / dt;
                        spd = spd <= 0 ? inst : spd * 0.55 + inst * 0.45;
                        lastDone = done;
                        lastT = now;
                    }
                    if (spd > peak) peak = spd;

                    task.Status = TaskStatus.Downloading;
                    task.Done = done;
                    task.SpeedBps = spd;
                    task.PushSample(spd);

                    // 慢段补线程：速度持续低于峰值 35% 且还剩不少没下完，
                    // 就从断点重启并加大分段，让 aria2 重新分配未完成的分片。
                    // 最多触发 2 次，避免在网络抖动时来回重启反而更慢。
                    if (adaptiveBoost && boosts < 2 && connections < TurboSources.MaxConnections &&
                        (now - started).TotalSeconds > 12 && peak > 256 * 1024 &&
                        done < total * 0.9 && spd < peak * 0.35)
                    {
                        slowTicks++;
                        if (slowTicks >= 12)
                        {
                            boostRequested = true;
                            Kill(proc);
                            break;
                        }
                    }
                    else slowTicks = 0;
                }
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
                throw;
            }

            await Task.Delay(300, ct);

            if (!File.Exists(ctrl))
            {
                long size = File.Exists(task.DestPath) ? new FileInfo(task.DestPath).Length : 0;
                if (totalHint > 0 && size < totalHint * 0.999)
                {
                    log($"[FAIL] 只拿到 {Fmt.Human(size)} / {Fmt.Human(totalHint)}，aria2 提前退出");
                    task.Done = size;
                    return false;
                }
                task.Done = size > 0 ? size : task.Done;
                task.Total = task.Total > 0 ? task.Total : size;
                task.SpeedBps = 0;
                task.PushSample(0);
                return true;
            }

            if (boostRequested)
            {
                boosts++;
                int next = Math.Min(TurboSources.MaxConnections,
                    Math.Max(connections + 8, (int)(connections * 1.5)));
                log($"[加速] 速度掉到峰值的 1/3 以下，从断点重启并把分段 {connections} → {next}");
                connections = next;
                perServer = 0;
                continue;
            }

            if (attempts >= 8)
            {
                log($"[FAIL] 连续 {attempts} 次未完成，下次运行会续传");
                return false;
            }

            log($"[重试 {attempts}] 连接中断，正在续传 ...");
            await Task.Delay(2000, ct);
        }
    }

    /// <summary>组装 aria2 参数。所有选项在前、所有 URL 在后，避免参数解析歧义。</summary>
    private static ProcessStartInfo BuildPsi(
        string aria, string destDir, string fileName, IReadOnlyList<string> urls,
        int connections, int perServer, IReadOnlyDictionary<string, string>? headers)
    {
        int per = perServer > 0 ? Math.Clamp(perServer, 1, connections) : connections;
        var psi = new ProcessStartInfo(aria)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = destDir,
        };

        var args = new List<string>
        {
            $"-x{connections}", $"-s{connections}", "-k1M",
            $"--max-connection-per-server={per}", "--min-split-size=1M",
            "--reuse-uri=true",             // 允许复用同一 URL 补足连接，多源时仍优先用没用过的
            "--disable-ipv6=true",
            "--check-certificate=false",
            "--file-allocation=none",       // 立刻开下，不为大文件预分配
            "-c",                           // 断点续传
            "--timeout=15", "--connect-timeout=10",
            "--max-tries=0", "--retry-wait=1",
            "--auto-save-interval=2",
            "--console-log-level=warn", "--summary-interval=0",
            "-d", destDir, "-o", fileName,
        };

        if (headers != null)
        {
            foreach (var kv in headers)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                var key = kv.Key.Replace("\r", "").Replace("\n", "");
                var val = (kv.Value ?? "").Replace("\r", "").Replace("\n", "");
                args.Add($"--header={key}: {val}");
            }
        }

        args.AddRange(CommonArgs());
        args.AddRange(urls);   // 同一任务的多个 URI，aria2 会把不同分段分散过去
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // ==================================================================
    // BT / 磁力：aria2c 原生 BitTorrent 模式（DHT + tracker）
    // ==================================================================
    private static readonly Regex BtProgress = new(
        @"\[#\w+\s+([\d.]+)([KMGT]?i?B)/([\d.]+)([KMGT]?i?B)\((\d+)%\)(?:.*?DL:([\d.]+)([KMGT]?i?B))?",
        RegexOptions.Compiled);

    private static long ToBytes(string num, string unit)
    {
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return 0;
        return unit switch
        {
            "KiB" or "KB" => (long)(v * 1024),
            "MiB" or "MB" => (long)(v * 1048576),
            "GiB" or "GB" => (long)(v * 1073741824),
            "TiB" or "TB" => (long)(v * 1099511627776),
            _ => (long)v
        };
    }

    /// <summary>磁力 / .torrent 下载。aria2 每秒打印一行进度，解析后回填任务。</summary>
    public static async Task<bool> DownloadTorrentAsync(
        DownloadTask task, string source, string saveDir, Action<string> log, CancellationToken ct)
    {
        var aria = Find() ?? throw new InvalidOperationException(
            "下载组件还没就绪，程序正在后台自动获取，请稍等几秒后重试（或重启程序）");

        Directory.CreateDirectory(saveDir);

        var psi = new ProcessStartInfo(aria)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = saveDir,
        };
        foreach (var a in new[]
                 {
                     "-d", saveDir,
                     "--seed-time=0",              // 下完不做种
                     "--bt-stop-timeout=0",
                     "--summary-interval=1",       // 每秒一行进度
                     "--console-log-level=notice",
                     "--file-allocation=none",
                     "--max-concurrent-downloads=1",
                     "--bt-max-peers=64",
                     "--enable-dht=true",
                     "--dht-listen-port=6881-6999",
                     "--listen-port=6881-6999",
                     "--disable-ipv6=true",
                     "--check-certificate=false",
                     "--bt-save-metadata=true",
                     source
                 }.Concat(CommonArgs()))
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };

        proc.OutputDataReceived += (_, ev) =>
        {
            var line = ev.Data;
            if (string.IsNullOrEmpty(line)) return;
            var m = BtProgress.Match(line);
            if (!m.Success) return;

            long done = ToBytes(m.Groups[1].Value, m.Groups[2].Value);
            long total = ToBytes(m.Groups[3].Value, m.Groups[4].Value);
            double speed = m.Groups[6].Success ? ToBytes(m.Groups[6].Value, m.Groups[7].Value) : 0;

            if (total > 0) task.Total = total;
            task.Done = done;
            if (speed > 0)
            {
                task.SpeedBps = speed;
                task.PushSample(speed);
            }
        };

        proc.ErrorDataReceived += (_, ev) =>
        {
            var line = ev.Data;
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                log("    " + line.Trim());
        };

        task.Status = TaskStatus.Downloading;
        if (!proc.Start()) throw new InvalidOperationException("aria2c 启动失败");
        task.Proc = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 停止时立刻杀进程，不在这里等退出（避免卡 UI）
        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });
        await proc.WaitForExitAsync(ct);

        task.SpeedBps = 0;
        task.PushSample(0);
        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
        {
            log($"    aria2c 退出码 {proc.ExitCode}");
            return false;
        }
        return true;
    }

    public static void Kill(Process? p)
    {
        try
        {
            if (p != null && !p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch { }
    }
}
