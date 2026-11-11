using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// ModelScope（魔搭）。国内直连速度通常比 HF 快一个数量级，
/// 所以它是竞速里最值得优先试的候选。
/// </summary>
public sealed class ModelScopeSource : ModelSourceBase, IRepoMirror
{
    public const string Host = "www.modelscope.cn";

    public override string Id => "modelscope";
    public override string Name => "ModelScope";

    public override bool Owns(string host) => host.Contains("modelscope.cn") || host.Contains("modelscope.com");

    public override bool TryParse(Uri uri, List<string> parts, UrlInfo info)
    {
        int i = parts.IndexOf("models");
        if (i < 0) return false;

        var rest = parts.Skip(i + 1).ToList();

        int j = rest.IndexOf("resolve");
        if (j >= 0)
        {
            info.Org = rest.Count > 0 ? rest[0] : null;
            info.Repo = rest.Count > 1 ? rest[1] : null;
            info.Rev = rest.Count > j + 1 ? rest[j + 1] : "master";
            var sub = rest.Skip(j + 2).ToList();
            info.Kind = UrlKind.File;
            info.Path = string.Join("/", sub);
            info.FileName = sub.Count > 0 ? sub[^1] : "";
            info.Folder = ComfyFolders.Detect(sub.Take(Math.Max(0, sub.Count - 1)));
            return true;
        }

        if (rest.Count >= 2)
        {
            info.Org = rest[0];
            info.Repo = rest[1];
            info.Kind = UrlKind.Repo;
            info.Rev = "master";
            return true;
        }

        return false;
    }

    public override async Task<IReadOnlyList<RepoFile>> ListFilesAsync(UrlInfo info, CancellationToken ct)
    {
        var api = $"https://{Host}/api/v1/models/{info.FullName}/repo/files" +
                  $"?Revision={RevOf(info, "master")}&Root=&Recursive=true";

        var list = new List<RepoFile>();
        try
        {
            using var doc = JsonDocument.Parse(await ComfyNet.Http.GetStringAsync(api, ct));
            if (doc.RootElement.TryGetProperty("Data", out var data) &&
                data.TryGetProperty("Files", out var files))
            {
                foreach (var e in files.EnumerateArray())
                {
                    if (e.TryGetProperty("Type", out var t) && t.GetString() == "blob")
                        list.Add(new RepoFile(e.GetProperty("Path").GetString() ?? "",
                                              e.TryGetProperty("Size", out var sz) ? sz.GetInt64() : 0));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("读取 ModelScope 文件列表失败：" + ex.Message, ex);
        }
        return list;
    }

    public override string ResolveUrl(UrlInfo info, string path)
        => $"https://{Host}/models/{info.FullName}/resolve/{RevOf(info, "main")}/{path}";

    /// <summary>魔搭上的模型多数在 hf-mirror 也有一份，作为竞速候选</summary>
    public override IEnumerable<string> Mirrors(UrlInfo info)
    {
        if (info.Kind != UrlKind.File || string.IsNullOrEmpty(info.Path) ||
            string.IsNullOrEmpty(info.Org) || string.IsNullOrEmpty(info.Repo))
            yield break;

        yield return $"https://{HuggingFaceSource.HostMirror}/{info.FullName}/resolve/master/{info.Path}";
    }

    /// <summary>任意仓库若在魔搭有镜像，往往比 HF 快，作为跨源候选</summary>
    public string? MirrorUrl(string org, string repo, string path)
        => $"https://{Host}/models/{org}/{repo}/resolve/master/{path}";
}
