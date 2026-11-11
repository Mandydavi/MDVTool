using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// Civitai。下载地址里只有版本号（/api/download/models/{versionId}），
/// 真实文件名和归类目录要再查一次 API 才知道。
/// </summary>
public sealed class CivitaiSource : ModelSourceBase
{
    private static readonly Regex VersionInPath = new(@"/api/download/models/(\d+)", RegexOptions.Compiled);

    public override string Id => "civitai";
    public override string Name => "Civitai";

    public override bool Owns(string host) => host.Contains("civitai.com");

    public override bool TryParse(Uri uri, List<string> parts, UrlInfo info)
    {
        var m = VersionInPath.Match(uri.AbsolutePath);
        if (!m.Success) return false;

        info.Kind = UrlKind.File;
        info.CivitaiVersionId = m.Groups[1].Value;
        info.FileName = $"civitai_{m.Groups[1].Value}.safetensors";
        info.Folder = null;
        return true;
    }

    /// <summary>查版本详情，拿到真实文件名与应放目录；查不到就返回 (null, null)</summary>
    public async Task<(string? Name, string? Folder)> LookupAsync(string versionId, CancellationToken ct)
    {
        try
        {
            var api = $"https://civitai.com/api/v1/model-versions/{versionId}";
            using var doc = JsonDocument.Parse(await ComfyNet.Http.GetStringAsync(api, ct));
            var root = doc.RootElement;

            string? name = null;
            if (root.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
            {
                JsonElement? primary = null;
                foreach (var f in files.EnumerateArray())
                {
                    if (f.TryGetProperty("primary", out var p) && p.GetBoolean()) { primary = f; break; }
                }
                primary ??= files[0];
                if (primary.Value.TryGetProperty("name", out var n)) name = n.GetString();
            }

            var folder = "checkpoints";
            if (root.TryGetProperty("model", out var model) &&
                model.TryGetProperty("type", out var type))
            {
                var mt = (type.GetString() ?? "").ToUpperInvariant();
                if (mt.Contains("LORA") || mt.Contains("LYCORIS")) folder = "loras";
                else if (mt == "VAE") folder = "vae";
                else if (mt == "CONTROLNET") folder = "controlnet";
                else if (mt == "UPSCALER") folder = "upscale_models";
                else if (mt == "TEXTUALINVERSION") folder = "embeddings";
            }

            if (!string.IsNullOrEmpty(name) && !ComfyFolders.IsModelFile(name!))
                name += ".safetensors";
            return (name, folder);
        }
        catch
        {
            return (null, null);
        }
    }
}
