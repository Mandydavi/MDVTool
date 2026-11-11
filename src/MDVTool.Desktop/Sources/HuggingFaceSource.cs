using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// HuggingFace 与它的国内镜像 hf-mirror.com。
/// 两个 host 共用一套解析规则，只有「列目录」和「拼地址」时区分。
/// </summary>
public sealed class HuggingFaceSource : ModelSourceBase, IRepoMirror
{
    public const string HostMain = "huggingface.co";
    public const string HostMirror = "hf-mirror.com";

    public override string Id => "huggingface";
    public override string Name => "HuggingFace";

    public override bool Owns(string host) => host.Contains(HostMain) || host.Contains(HostMirror);

    private static bool IsMirror(string host) => host.Contains(HostMirror);

    public override bool TryParse(Uri uri, List<string> parts, UrlInfo info)
    {
        // /blob/ 是网页地址，/resolve/ 才是下载地址，先改写
        int blob = parts.IndexOf("blob");
        if (blob >= 0)
        {
            parts[blob] = "resolve";
            info.Url = UrlUtil.WithPath(uri, parts);
        }

        int i = parts.IndexOf("resolve");
        if (i >= 0)
        {
            info.Org = parts.Count > 0 ? parts[0] : null;
            info.Repo = parts.Count > 1 ? parts[1] : null;
            info.Rev = parts.Count > i + 1 ? parts[i + 1] : "main";
            var sub = parts.Skip(i + 2).ToList();
            info.Kind = UrlKind.File;
            info.Path = string.Join("/", sub);
            info.FileName = sub.Count > 0 ? sub[^1] : "";
            info.Folder = ComfyFolders.Detect(sub.Take(Math.Max(0, sub.Count - 1)));
            return true;
        }

        if (parts.Count >= 2)
        {
            info.Org = parts[0];
            info.Repo = parts[1];
            info.Kind = UrlKind.Repo;
            info.Rev = parts.Count >= 4 && parts[2] == "tree" ? parts[3] : "main";
        }
        return true;
    }

    public override async Task<IReadOnlyList<RepoFile>> ListFilesAsync(UrlInfo info, CancellationToken ct)
    {
        var full = info.FullName;
        var rev = RevOf(info, "main");

        // 先走官方 API；国内直连 huggingface.co 经常超时，失败后退到镜像 API。
        var apis = new[]
        {
            $"https://{HostMain}/api/models/{full}/tree/{rev}?recursive=true",
            $"https://{HostMirror}/api/models/{full}/tree/{rev}?recursive=true",
        };

        Exception? last = null;
        foreach (var api in apis)
        {
            try
            {
                using var doc = JsonDocument.Parse(await ComfyNet.Http.GetStringAsync(api, ct));
                return ReadTree(doc.RootElement);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("读取 HuggingFace 文件列表失败：" + last?.Message, last);
    }

    private static List<RepoFile> ReadTree(JsonElement root)
    {
        var list = new List<RepoFile>();
        if (root.ValueKind != JsonValueKind.Array) return list;

        foreach (var e in root.EnumerateArray())
        {
            if (e.TryGetProperty("type", out var t) && t.GetString() == "file")
                list.Add(new RepoFile(e.GetProperty("path").GetString() ?? "",
                                      e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
        }
        return list;
    }

    public override string ResolveUrl(UrlInfo info, string path)
        => RepoPath(IsMirror(info.Host) ? HostMirror : HostMain, info.FullName, RevOf(info, "main"), path);

    /// <summary>同一个仓库的镜像 host，用来竞速</summary>
    public override IEnumerable<string> Mirrors(UrlInfo info)
    {
        if (info.Kind != UrlKind.File || string.IsNullOrEmpty(info.Path) ||
            string.IsNullOrEmpty(info.Org) || string.IsNullOrEmpty(info.Repo))
            yield break;

        var full = info.FullName;
        var rev = RevOf(info, "main");
        yield return RepoPath(IsMirror(info.Host) ? HostMain : HostMirror, full, rev, info.Path);
    }

    /// <summary>别的源（ModelScope）上的同名仓库，试着去 hf-mirror 找一份</summary>
    public string? MirrorUrl(string org, string repo, string path)
        => $"https://{HostMirror}/{org}/{repo}/resolve/master/{path}";
}
