using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MDVTool;

/// <summary>非模型文件按扩展名自动归档，文件名清洗，媒体页占位名生成。</summary>
public static class CategoryRouter
{
    private static readonly Dictionary<string, string[]> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["视频"] = new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".m3u8", ".mpg", ".mpeg" },
        ["音乐"] = new[] { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".ape" },
        ["程序"] = new[] { ".exe", ".msi", ".msix", ".appx", ".dmg", ".apk", ".deb", ".rpm", ".bat", ".ps1" },
        ["压缩包"] = new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab" },
        ["文档"] = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".epub" },
        ["图片"] = new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg", ".psd" },
    };

    /// <summary>模型扩展名走 ComfyUI 归类，不参与这里的分组</summary>
    public static string CategoryOf(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        foreach (var kv in Groups)
            if (kv.Value.Contains(ext, StringComparer.OrdinalIgnoreCase)) return kv.Key;
        return "其他文件";
    }

    /// <summary>按设置把普通文件放进分类子目录；模型仍由 ComfyFolders 处理</summary>
    public static string Route(string root, string fileName, string? folder, bool isModel, bool enabled)
    {
        folder = (folder ?? "").Trim().TrimEnd('\\', '/');
        if (folder.Length > 0 && Path.IsPathRooted(folder)) return Path.GetFullPath(folder);
        if (isModel) return Path.GetFullPath(Path.Combine(root, folder.Length == 0 ? "diffusion_models" : folder));
        if (!enabled) return Path.GetFullPath(folder.Length == 0 ? root : Path.Combine(root, folder));
        var sub = folder.Length > 0 ? folder : CategoryOf(fileName);
        return Path.GetFullPath(Path.Combine(root, sub));
    }

    /// <summary>去掉 Windows 文件名非法字符，限制长度，避免空名</summary>
    public static string SanitizeFileName(string name, string fallback = "MDV-Download")
    {
        var s = (name ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace('/', '_').Replace('\\', '_').Replace(':', '_')
             .Replace('*', '_').Replace('?', '_').Replace('"', '_')
             .Replace('<', '_').Replace('>', '_').Replace('|', '_');
        s = string.Join("", s.Where(ch => !char.IsControl(ch))).Trim().TrimEnd('.', ' ');
        if (s.Length > 120) s = s[..120].TrimEnd('.', ' ');
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>视频页 / m3u8 在解析出真实标题前，先给一个稳定的占位文件名</summary>
    public static string PlaceholderFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url ?? "")))[..8];
        return $"MDV-Video-{hash}.mp4";
    }
}
