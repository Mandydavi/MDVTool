using System.Collections.Generic;
using System.Linq;

namespace MDVTool;

/// <summary>仓库里的一个文件</summary>
public sealed class RepoFile
{
    public RepoFile(string path, long size)
    {
        Path = path;
        Size = size;
    }

    public string Path { get; }
    public long Size { get; }
    public string Name => Path.Split('/')[^1];
    public string SizeText => Fmt.Human(Size);
    public string DirText
    {
        get
        {
            var parts = Path.Split('/');
            return parts.Length <= 1 ? "根目录" : string.Join("/", parts.Take(parts.Length - 1));
        }
    }
}
