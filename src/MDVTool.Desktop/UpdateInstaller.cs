using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// 应用内更新（不打开浏览器）。
///
/// 流程：
///   1. DownloadAsync   下载 version.json 里指定的 .zip 覆盖包（支持 http(s) / 本地盘 / 共享盘），
///                      按清单里的 size 与 sha256 校验，解包到 %LOCALAPPDATA%\MDV\Update\&lt;版本&gt;\payload
///   2. ApplyAndRestart 把「自己」复制成临时 exe，用 --apply-update 参数启动它，然后主程序退出
///   3. 临时 exe 等主进程结束后，把 payload 覆盖到安装目录，再重新拉起主程序
///
/// 为什么要复制自己：正在运行的 exe 会被 Windows 锁住，无法原地覆盖；
/// 临时副本不在安装目录里，可以安心等旧进程退出后再动手。
/// </summary>
public static class UpdateInstaller
{
    public const string ApplyFlag = "--apply-update";
    private const string HealthMarker = ".mdv-health";

    /// <summary>安装目录 = 当前 exe 所在目录</summary>
    public static string InstallDir =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    public static string CurrentExeName =>
        Path.GetFileName(Environment.ProcessPath ?? "MDVTool.exe");

    private static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDV", "Update");

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MDVTool", "update.log");

    // ==================================================================
    // 1. 下载 + 解包
    // ==================================================================

    /// <summary>下载并解包更新包，返回 payload 目录</summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info,
        IProgress<(double Pct, string Text)>? progress,
        CancellationToken ct)
    {
        string url = (info.Url ?? "").Trim();
        if (url.Length == 0) throw new InvalidOperationException("更新清单里没有下载地址");

        string version = Safe(info.Version, "unknown");
        string dir = Path.Combine(StateRoot, version);
        Directory.CreateDirectory(dir);

        string fileName = FileNameOf(url);
        if (fileName.Length == 0) fileName = "update.zip";
        string pkg = Path.Combine(dir, fileName);

        // 之前下过且校验通过就直接用，省一次流量
        if (!AlreadyGood(pkg, info))
        {
            if (File.Exists(pkg)) TryDelete(pkg);
            progress?.Report((0, "正在连接更新服务器…"));

            if (IsHttp(url))
                await DownloadHttpAsync(url, pkg, info, progress, ct);
            else
            {
                progress?.Report((0.05, "正在从本地更新源复制…"));
                await Task.Run(() => File.Copy(LocalPath(url), pkg, true), ct);
            }

            if (info.Size > 0)
            {
                long len = new FileInfo(pkg).Length;
                if (len != info.Size)
                {
                    TryDelete(pkg);
                    throw new InvalidOperationException($"更新包大小不符（{len} ≠ {info.Size}）");
                }
            }
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                progress?.Report((0.98, "正在校验更新包…"));
                string hex = await Task.Run(() => Sha256Of(pkg), ct);
                if (!string.Equals(hex, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(pkg);
                    throw new InvalidOperationException("更新包校验失败（SHA-256 不一致），已删除");
                }
            }
        }

        if (!pkg.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新包不是 .zip 覆盖包，无法在应用内替换文件");

        string payload = Path.Combine(dir, "payload");
        progress?.Report((0.99, "正在解包…"));
        if (Directory.Exists(payload)) Directory.Delete(payload, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(pkg, payload, true), ct);
        progress?.Report((1, "更新包已就绪"));
        return payload;
    }

    private static async Task DownloadHttpAsync(
        string url, string dest, UpdateInfo info,
        IProgress<(double Pct, string Text)>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MDVTool-Updater/1.0");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? info.Size;
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);

        var buf = new byte[81920];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            double pct = total > 0 ? Math.Min(0.97, (double)done / total * 0.97) : 0;
            progress?.Report((pct, total > 0
                ? $"正在下载 {Fmt.Human(done)} / {Fmt.Human(total)}"
                : $"正在下载 {Fmt.Human(done)}"));
        }
    }

    // ==================================================================
    // 2. 交给临时副本去覆盖
    // ==================================================================

    /// <summary>把临时副本拉起来等自己退出，调用后主程序应当马上 Close</summary>
    public static void ApplyAndRestart(string payloadDir)
    {
        string self = Environment.ProcessPath
            ?? throw new InvalidOperationException("拿不到当前程序路径");

        string tmpRoot = Path.Combine(Path.GetTempPath(), "mdvupd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpRoot);
        string updater = Path.Combine(tmpRoot, "MDVToolUpdater.exe");
        File.Copy(self, updater, true);

        var psi = new ProcessStartInfo(updater)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tmpRoot,
            Arguments = $"{ApplyFlag} \"{payloadDir}\" \"{InstallDir}\" {Environment.ProcessId} \"{CurrentExeName}\"",
        };
        Process.Start(psi);
        Log($"已启动更新器：{updater}");
    }

    // ==================================================================
    // 3. 更新器模式
    // ==================================================================

    /// <summary>启动成功时由新版本自己写下这个标记，更新器据此判断"装好了"</summary>
    public static void MarkHealthy()
    {
        try
        {
            string marker = Path.Combine(InstallDir, HealthMarker);
            File.WriteAllText(marker, UpdateService.CurrentText + " " + DateTime.Now.ToString("s"));
        }
        catch { }
    }

    /// <summary>启动参数里带 --apply-update 时走这里；返回 true 表示本次是更新器，不该再开主窗口</summary>
    public static bool TryRunApplyMode(string[] args)
    {
        int i = Array.IndexOf(args, ApplyFlag);
        if (i < 0 || args.Length < i + 5) return false;

        RunApply(args[i + 1], args[i + 2],
                 int.TryParse(args[i + 3], out var pid) ? pid : 0,
                 args[i + 4]);
        return true;
    }

    private static void RunApply(string payload, string install, int pid, string exeName)
    {
        string backup = Path.Combine(StateRoot, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        bool installed = false;

        try
        {
            Log($"==== 更新开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
            Log($"payload = {payload}");
            Log($"install = {install}");
            Log($"逐出进程 {pid}，主程序名 {exeName}");

            WaitForExit(pid);
            KillEngine(install);

            if (!Directory.Exists(payload))
                throw new DirectoryNotFoundException("更新包目录不存在：" + payload);

            // 覆盖前先整目录备份，失败要能退回去
            Log("备份当前版本到 " + backup);
            BackupTree(install, backup);

            CopyTree(payload, install, exeName, Log);
            installed = true;

            string target = Path.Combine(install, exeName);
            if (!File.Exists(target))
            {
                string? any = Directory.GetFiles(install, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (any == null) throw new FileNotFoundException("覆盖后找不到主程序：" + target);
                target = any;
            }

            Log("覆盖完成，启动新版本等待健康检查：" + target);
            var proc = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = install,
            });

            if (Healthy(proc, install, exeName))
            {
                Log("新版本启动正常，删除备份");
                TryDeleteDir(backup);
                Log("==== 更新完成 ====");
            }
            else
            {
                Log("[回滚] 新版本没有通过健康检查，恢复旧版本");
                Rollback(backup, install);
                Log("已回滚到旧版本");
            }
        }
        catch (Exception ex)
        {
            Log("[失败] " + ex);
            try
            {
                if (installed && Directory.Exists(backup))
                {
                    Log("[回滚] 覆盖过程中出错，恢复旧版本");
                    Rollback(backup, install);
                }
            }
            catch (Exception rex) { Log("[回滚失败] " + rex.Message); }

            try
            {
                // 无论如何都要把软件拉起来，不能把用户晾在空目录
                string target = Path.Combine(install, exeName);
                if (File.Exists(target))
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = install });
            }
            catch { }
        }
    }

    /// <summary>等新版本写出「启动正常」标记；进程提前退出或超时没标记都算失败</summary>
    private static bool Healthy(Process? proc, string install, string exeName)
    {
        string marker = Path.Combine(install, HealthMarker);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }

        DateTime started = DateTime.Now;
        for (int i = 0; i < 100; i++)          // 最多等 30 秒
        {
            Thread.Sleep(300);

            bool alive = proc != null && !proc.HasExited;
            if (!alive && proc != null) { Log("新进程已退出（可能启动失败）"); return false; }

            try
            {
                if (File.Exists(marker) && File.GetLastWriteTime(marker) >= started.AddSeconds(-1))
                {
                    Log("收到健康标记");
                    return true;
                }
            }
            catch { }

            if (i > 20 && alive) return true;  // 进程活着但没写标记（如被托盘/权限影响），放行
        }
        return proc != null && !proc.HasExited;
    }

    private static void BackupTree(string install, string backup)
    {
        Directory.CreateDirectory(backup);
        foreach (string file in Directory.GetFiles(install, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(install, file);
            if (rel.StartsWith(".mdv-backup", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith(HealthMarker, StringComparison.OrdinalIgnoreCase)) continue;

            string dest = Path.Combine(backup, rel);
            string? parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            try { File.Copy(file, dest, true); } catch { /* 被占用的文件留空，回滚时跳过 */ }
        }
    }

    private static void Rollback(string backup, string install)
    {
        if (!Directory.Exists(backup)) return;
        foreach (string file in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(backup, file);
            string dest = Path.Combine(install, rel);
            string? parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            CopyWithRetry(file, dest);
        }

        try
        {
            string target = Directory.GetFiles(install, "*.exe", SearchOption.TopDirectoryOnly)
                                    .OrderByDescending(x => new FileInfo(x).Length).FirstOrDefault() ?? "";
            if (target.Length > 0)
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = install });
        }
        catch (Exception ex) { Log("[回滚启动失败] " + ex.Message); }
    }

    private static void WaitForExit(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(120000)) Log("等待旧进程退出超时，继续覆盖");
        }
        catch (ArgumentException) { /* 已经退了 */ }
        catch (Exception ex) { Log("等待旧进程时出错：" + ex.Message); }
        Thread.Sleep(400);   // 让文件句柄彻底释放
    }

    /// <summary>旧进程被杀掉后，引擎子进程可能还活着，先清掉，否则 aria2c.exe 覆盖不了</summary>
    private static void KillEngine(string install)
    {
        foreach (var name in new[] { "aria2c", "ffmpeg", "yt-dlp" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (path != null && path.StartsWith(install, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(true);
                        p.WaitForExit(3000);
                        Log("已结束占用文件的进程：" + path);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }

    private static void CopyTree(string payload, string install, string exeName, Action<string> log)
    {
        string? main = FindMainExe(payload);
        var files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories);
        foreach (string file in files)
        {
            string rel = Path.GetRelativePath(payload, file);
            string dest = Path.Combine(install, rel);

            // 包里的主程序统一改成当前 exe 的名字，避免安装目录里留下两份 exe
            if (main != null && string.Equals(file, main, StringComparison.OrdinalIgnoreCase))
                dest = Path.Combine(install, exeName);

            string? parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (CopyWithRetry(file, dest)) log("  覆盖 " + rel);
            else log("  跳过（被占用）" + rel);
        }
    }

    private static string? FindMainExe(string payload)
    {
        var exes = Directory.GetFiles(payload, "*.exe", SearchOption.AllDirectories);
        if (exes.Length == 0) return null;
        foreach (var name in new[] { "MDV Tool.exe", "MDVTool.exe" })
        {
            var hit = exes.FirstOrDefault(x =>
                string.Equals(Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return exes.OrderByDescending(x => new FileInfo(x).Length).First();
    }

    private static bool CopyWithRetry(string src, string dest)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                File.Copy(src, dest, true);
                return true;
            }
            catch (IOException) { Thread.Sleep(350); }
            catch (UnauthorizedAccessException) { Thread.Sleep(350); }
        }
        return false;
    }

    // ==================================================================
    // 工具
    // ==================================================================

    public static bool IsHttp(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string LocalPath(string url)
    {
        string s = Environment.ExpandEnvironmentVariables(url.Trim().Trim('"'));
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(s).LocalPath; } catch { return s; }
        }
        return s;
    }

    private static string FileNameOf(string url)
    {
        string s = IsHttp(url) ? url.Split('?')[0].Split('#')[0] : LocalPath(url);
        s = s.Replace('\\', '/');
        int i = s.LastIndexOf('/');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    private static bool AlreadyGood(string path, UpdateInfo info)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (info.Size > 0 && fi.Length != info.Size) return false;
            if (!string.IsNullOrWhiteSpace(info.Sha256) &&
                !string.Equals(Sha256Of(path), info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch { return false; }
    }

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string Safe(string s, string fallback)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') sb.Append(c);
        return sb.Length == 0 ? fallback : sb.ToString();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}
