using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// 一个下载源（HuggingFace / ModelScope / Civitai …）。
/// 新增源只需实现本接口并在 <see cref="ComfyNet"/> 的注册表里加一行，
/// 调度、测速、aria2 那一侧完全不用改。
/// </summary>
public interface IModelSource
{
    /// <summary>稳定标识，写日志和排查问题用</summary>
    string Id { get; }

    /// <summary>界面上显示的名字</summary>
    string Name { get; }

    /// <summary>这个 host 归不归我管</summary>
    bool Owns(string host);

    /// <summary>
    /// 解析路径并填充 <paramref name="info"/>。
    /// 返回 false 表示「我管这个 host，但认不出这条路径」，交由后面的源兜底。
    /// </summary>
    bool TryParse(Uri uri, List<string> parts, UrlInfo info);

    /// <summary>列出仓库文件；不支持的源返回空集合</summary>
    Task<IReadOnlyList<RepoFile>> ListFilesAsync(UrlInfo info, CancellationToken ct);

    /// <summary>拼出仓库内某个文件的下载地址</summary>
    string ResolveUrl(UrlInfo info, string path);

    /// <summary>同一个文件在本源的其他镜像地址（参与竞速）</summary>
    IEnumerable<string> Mirrors(UrlInfo info);
}

/// <summary>
/// 能把「任意仓库的 org/repo/path」映射成本源地址的源。
/// 竞速候选靠它拼，不必在调度层写 if (host == ...)。
/// </summary>
public interface IRepoMirror
{
    /// <summary>返回 null 表示本源没有这个仓库的映射</summary>
    string? MirrorUrl(string org, string repo, string path);
}

/// <summary>可选能力的默认实现，避免每个源写一堆空方法</summary>
public abstract class ModelSourceBase : IModelSource
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract bool Owns(string host);

    public virtual bool TryParse(Uri uri, List<string> parts, UrlInfo info) => false;

    public virtual Task<IReadOnlyList<RepoFile>> ListFilesAsync(UrlInfo info, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RepoFile>>(Array.Empty<RepoFile>());

    public virtual string ResolveUrl(UrlInfo info, string path) => info.Url;

    public virtual IEnumerable<string> Mirrors(UrlInfo info) => Array.Empty<string>();

    /// <summary>HF 与 ModelScope 的仓库文件地址形状一致，抽出来复用</summary>
    protected static string RepoPath(string host, string full, string rev, string path)
        => $"https://{host}/{full}/resolve/{rev}/{path}";

    /// <summary>rev 为空时给出该源的默认分支</summary>
    protected static string RevOf(UrlInfo info, string fallback)
        => string.IsNullOrEmpty(info.Rev) ? fallback : info.Rev;
}

/// <summary>URL 小工具</summary>
public static class UrlUtil
{
    /// <summary>换掉路径部分，其余（scheme / host / query）保持不变</summary>
    public static string WithPath(Uri uri, IEnumerable<string> parts)
    {
        var b = new UriBuilder(uri) { Path = "/" + string.Join("/", parts) };
        return b.Uri.ToString();
    }
}
