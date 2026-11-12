using System;
using System.IO;
using System.Windows.Media;

namespace MDVTool;

/// <summary>文件类型徽标（颜色 + 文字），给任务列表左侧的小方块用</summary>
public static class Badge
{
    private static Brush Make(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static readonly Brush Blue = Make("#2F6BFF");
    private static readonly Brush Purple = Make("#7C3AED");
    private static readonly Brush Violet = Make("#8B5CF6");
    private static readonly Brush Slate = Make("#475569");
    private static readonly Brush Cyan = Make("#0E7490");
    private static readonly Brush Green = Make("#1F9D55");
    private static readonly Brush Teal = Make("#0F766E");

    public static string Text(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "safetensors" => "SFT",
            "gguf" => "GGUF",
            "ckpt" => "CKPT",
            "pt" or "pth" => "PT",
            "bin" => "BIN",
            "onnx" => "ONNX",
            "zip" => "ZIP",
            "7z" or "rar" => "7Z",
            "torrent" => "BT",
            "exe" or "msi" => "EXE",
            "json" => "JSON",
            "yaml" or "yml" => "YML",
            "" => "FILE",
            _ => ext.Length <= 4 ? ext.ToUpperInvariant() : ext[..4].ToUpperInvariant()
        };
    }

    /// <summary>按扩展名给一个语义化的矢量图标名（对应 Themes/Icons.xaml 的 Ico.*）</summary>
    public static string Icon(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            // 模型权重：立方体
            "safetensors" or "ckpt" or "pt" or "pth" or "bin" or "onnx" or "gguf" => "cube",
            // 压缩包
            "zip" or "7z" or "rar" or "tar" or "gz" => "archive",
            // 种子
            "torrent" => "magnet",
            // 安装包 / 可执行
            "exe" or "msi" => "package",
            // 文本配置
            "json" or "yaml" or "yml" or "txt" or "md" or "csv" => "file-text",
            // 视频
            "mp4" or "mkv" or "mov" or "webm" or "avi" => "film",
            // 音频
            "mp3" or "wav" or "flac" or "m4a" or "ogg" => "music",
            // 图片
            "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" => "image",
            _ => "file"
        };
    }

    public static Brush BrushFor(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "safetensors" or "exe" or "msi" => Blue,
            "gguf" => Purple,
            "zip" or "7z" or "rar" => Violet,
            "ckpt" or "pt" or "pth" => Slate,
            "onnx" => Cyan,
            "torrent" => Green,
            "bin" => Teal,
            _ => Blue
        };
    }
}

/// <summary>文件管理页里的一行</summary>
public sealed class ModelFolder
{
    public ModelFolder(string name, string fullPath, int fileCount, long size)
    {
        Name = name;
        FullPath = fullPath;
        FileCount = fileCount;
        Size = size;
    }

    public string Name { get; }
    public string FullPath { get; }
    public int FileCount { get; }
    public long Size { get; }
    public string SizeText => Fmt.Human(Size);
    public string SubText => $"{FileCount} 个文件 · {FullPath}";
    public string CountText => $"{FileCount} · {SizeText}";
}

/// <summary>模型库右侧文件列表的一行</summary>
public sealed class ModelFile
{
    public ModelFile(string name, string fullPath, long size)
    {
        Name = name;
        FullPath = fullPath;
        Size = size;
    }

    public string Name { get; }
    public string FullPath { get; }
    public long Size { get; }
    public string SizeText => Fmt.Human(Size);
    public string BadgeText => Badge.Text(Name);
    public string BadgeIcon => Badge.Icon(Name);
}
