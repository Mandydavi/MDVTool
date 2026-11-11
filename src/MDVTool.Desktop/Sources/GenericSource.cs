using System;
using System.Collections.Generic;
using System.Linq;

namespace MDVTool;

/// <summary>
/// 兜底源：任意直链。
/// 认不出域名时用它，靠 URL 路径猜文件名和应放目录。
/// </summary>
public sealed class GenericSource : ModelSourceBase
{
    public override string Id => "direct";
    public override string Name => "直链";

    public override bool Owns(string host) => true;

    public override bool TryParse(Uri uri, List<string> parts, UrlInfo info)
    {
        var fn = parts.Count > 0 ? Uri.UnescapeDataString(parts[^1]) : "";
        info.FileName = fn;
        info.Kind = fn.Length > 0 ? UrlKind.File : UrlKind.Unknown;
        info.Path = string.Join("/", parts);
        info.Folder = ComfyFolders.Detect(parts.Take(Math.Max(0, parts.Count - 1)))
                      ?? ComfyFolders.GuessFromName(fn);
        return true;
    }
}
