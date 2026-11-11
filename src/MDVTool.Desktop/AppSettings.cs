using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDVTool;

/// <summary>应用设置，存在 %APPDATA%\MDVTool\settings.json</summary>
public sealed class AppSettings
{
    [JsonPropertyName("modelsDir")]
    public string ModelsDir { get; set; } = DefaultModelsDir;

    /// <summary>非模型文件（软件、压缩包、视频等）的保存目录</summary>
    [JsonPropertyName("downloadDir")]
    public string DownloadDir { get; set; } = DefaultDownloadDir;

    public static string DefaultDownloadDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string DefaultModelsDir
    {
        get
        {
            // 常见 ComfyUI 位置，存在就用，省得手动配
            foreach (var p in new[]
                     {
                         @"E:\Comfy-Desktop\ComfyUI-Shared\models",
                         @"D:\Comfy-Desktop\ComfyUI-Shared\models",
                         @"C:\Comfy-Desktop\ComfyUI-Shared\models",
                     })
            {
                try { if (Directory.Exists(p)) return p; } catch { }
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "ComfyUI", "models");
        }
    }

    [JsonPropertyName("raceSources")]
    public bool RaceSources { get; set; } = true;

    /// <summary>下载前自动实测 1 / 4 / 16 连接，谁快用谁；关掉才用下面的固定值</summary>
    [JsonPropertyName("autoConnections")]
    public bool AutoConnections { get; set; } = true;

    [JsonPropertyName("connections")]
    public int Connections { get; set; } = TurboSources.MaxConnections;

    [JsonPropertyName("autoVerify")]
    public bool AutoVerify { get; set; } = true;

    [JsonPropertyName("darkMode")]
    public bool DarkMode { get; set; } = true;

    /// <summary>界面配色：blue / mint / violet / coral</summary>
    public string Palette { get; set; } = "blue";

    /// <summary>新建任务时是否弹窗询问保存位置</summary>
    [JsonPropertyName("askFolderOnAdd")]
    public bool AskFolderOnAdd { get; set; } = true;

    /// <summary>默认保存位置（相对 models 的子目录名，或绝对路径）</summary>
    [JsonPropertyName("defaultFolder")]
    public string? DefaultFolder { get; set; }

    /// <summary>自定义 aria2c.exe 路径；留空 = 自动检测（程序同目录 → winget → Program Files → PATH）</summary>
    [JsonPropertyName("aria2Path")]
    public string? Aria2Path { get; set; }

    // ==================================================================
    // v2.3 · 下载行为
    // ==================================================================

    /// <summary>同时下载的任务数上限；0 = 不限</summary>
    [JsonPropertyName("maxConcurrent")]
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>全局限速（MB/s）；0 = 不限</summary>
    [JsonPropertyName("speedLimitMBps")]
    public double SpeedLimitMBps { get; set; }

    /// <summary>代理地址，例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:7890；空 = 不使用</summary>
    [JsonPropertyName("proxyUrl")]
    public string ProxyUrl { get; set; } = "";

    /// <summary>同名文件策略：ask 询问 / overwrite 覆盖 / skip 跳过 / rename 重命名</summary>
    [JsonPropertyName("conflictPolicy")]
    public string ConflictPolicy { get; set; } = "ask";

    /// <summary>下载前检查目标磁盘剩余空间</summary>
    [JsonPropertyName("checkDiskSpace")]
    public bool CheckDiskSpace { get; set; } = true;

    // ==================================================================
    // v3 · 极速引擎 / 视频与流媒体
    // ==================================================================

    /// <summary>多源并发：把同一文件的多个镜像一起交给 aria2，跨源叠加带宽</summary>
    [JsonPropertyName("turboMultiSource")]
    public bool TurboMultiSource { get; set; } = true;

    /// <summary>速度明显下滑时自动加大分段并从断点重启（慢段补线程）</summary>
    [JsonPropertyName("adaptiveBoost")]
    public bool AdaptiveBoost { get; set; } = true;

    /// <summary>HLS 分片并发数</summary>
    [JsonPropertyName("hlsConcurrency")]
    public int HlsConcurrency { get; set; } = 24;

    /// <summary>视频清晰度：best / 1080 / 720 / audio</summary>
    [JsonPropertyName("mediaQuality")]
    public string MediaQuality { get; set; } = "best";

    /// <summary>启动时开启浏览器嗅探桥（配合 BrowserExtension 扩展）</summary>
    [JsonPropertyName("browserSniff")]
    public bool BrowserSniff { get; set; } = true;

    /// <summary>非模型文件按类型自动归档到 视频/音乐/程序/压缩包/文档 子目录</summary>
    [JsonPropertyName("autoCategory")]
    public bool AutoCategory { get; set; } = false;

    /// <summary>启动时静默检查更新</summary>
    [JsonPropertyName("autoCheckUpdate")]
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>关闭窗口时的行为：tray 收进托盘 / ask 每次询问 / exit 直接退出</summary>
    [JsonPropertyName("closeAction")]
    public string CloseAction { get; set; } = "ask";

    /// <summary>version.json 地址；留空 = 不检查更新（GitHub raw / jsDelivr / Gitee raw 均可）</summary>
    [JsonPropertyName("updateFeed")]
    public string UpdateFeed { get; set; } =
        "https://cdn.jsdelivr.net/gh/Mandydavi/MDVTool@main/version.json";

    [JsonIgnore]
    public static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MDVTool");
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            // v1.0.0 之前配置放在 %APPDATA%\ComfyDL，改名后迁移一次，避免用户设置丢失
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacy = Path.Combine(appData, "ComfyDL", "settings.json");
            if (!File.Exists(ConfigPath) && File.Exists(legacy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.Copy(legacy, ConfigPath);
            }

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null)
                {
                    if (s.Connections < 1 || s.Connections > TurboSources.MaxConnections)
                        s.Connections = TurboSources.MaxConnections;
                    if (s.HlsConcurrency < 1 || s.HlsConcurrency > 64) s.HlsConcurrency = 24;
                    if (string.IsNullOrWhiteSpace(s.MediaQuality)) s.MediaQuality = "best";
                    if (string.IsNullOrWhiteSpace(s.ModelsDir)) s.ModelsDir = DefaultModelsDir;
                    if (string.IsNullOrWhiteSpace(s.DownloadDir)) s.DownloadDir = DefaultDownloadDir;
                    return s;
                }
            }
        }
        catch
        {
            // 配置损坏就用默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 存不下也不该让界面崩
        }
    }
}

// ==================================================================
// 下载历史：任务列表持久化到程序目录下的「历史记录」文件夹
// ==================================================================
public sealed class TaskRecord
{
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string DestPath { get; set; } = "";
    public long SizeHint { get; set; }
    public long Total { get; set; }
    public long Done { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime AddedAt { get; set; }
}

public static class TaskStore
{
    private static readonly object Gate = new();
    private const int MaxRecords = 200;

    /// <summary>exe 所在目录下的「历史记录」文件夹</summary>
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "历史记录");
    public static string FilePath => Path.Combine(Dir, "history.json");

    public static List<TaskRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<TaskRecord>>(json);
                if (list != null) return list.TakeLast(MaxRecords).ToList();
            }
        }
        catch { /* 文件损坏就当没有 */ }

        // 第一次运行（或文件被删）：把磁盘上已经下载好的模型补进历史
        return ScanExisting(App.Settings);
    }

    public static void Save(IEnumerable<DownloadTask> tasks)
    {
        try
        {
            var records = tasks.TakeLast(MaxRecords).Select(t => new TaskRecord
            {
                Url = t.Url,
                Host = t.Host,
                FileName = t.FileName,
                Folder = t.Folder,
                DestPath = t.DestPath,
                SizeHint = t.SizeHint,
                Total = t.Total,
                Done = t.Done,
                Status = t.Status.ToString(),
                Message = t.Message,
                AddedAt = t.AddedAt
            }).ToList();

            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, json);
            }
        }
        catch { /* 存不下不影响下载 */ }
    }

    /// <summary>扫描已配置的模型/下载目录，把已有文件补录成「已完成」记录</summary>
    public static List<TaskRecord> ScanExisting(AppSettings s)
    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".safetensors", ".gguf", ".ckpt", ".pt", ".pth", ".bin", ".onnx"
        };
        var list = new List<TaskRecord>();

        foreach (var root in new[] { s.ModelsDir, s.DownloadDir }
                     .Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!exts.Contains(Path.GetExtension(f))) continue;
                    try
                    {
                        var fi = new FileInfo(f);
                        list.Add(new TaskRecord
                        {
                            Url = "",
                            Host = "本地文件",
                            FileName = fi.Name,
                            Folder = Path.GetDirectoryName(f) ?? "",
                            DestPath = f,
                            SizeHint = fi.Length,
                            Total = fi.Length,
                            Done = fi.Length,
                            Status = nameof(TaskStatus.Completed),
                            Message = "从本地文件补录",
                            AddedAt = fi.LastWriteTime
                        });
                    }
                    catch { /* 单个文件读不到就跳过 */ }
                }
            }
            catch { /* 目录没权限就跳过 */ }
        }

        return list.OrderByDescending(r => r.AddedAt).Take(MaxRecords).ToList();
    }
}
