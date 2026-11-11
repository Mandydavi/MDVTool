using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MDVTool;

/// <summary>ComfyUI models 目录的别名表 + 自动归类</summary>
public static class ComfyFolders
{
    public static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unet"] = "diffusion_models",
        ["diffusion_models"] = "diffusion_models",
        ["diffusion"] = "diffusion_models",
        ["clip"] = "text_encoders",
        ["text_encoders"] = "text_encoders",
        ["text_encoder"] = "text_encoders",
        ["t5"] = "text_encoders",
        ["vae"] = "vae",
        ["vae_approx"] = "vae_approx",
        ["loras"] = "loras",
        ["lora"] = "loras",
        ["controlnet"] = "controlnet",
        ["control_net"] = "controlnet",
        ["upscale_models"] = "upscale_models",
        ["upscalers"] = "upscale_models",
        ["clip_vision"] = "clip_vision",
        ["embeddings"] = "embeddings",
        ["checkpoints"] = "checkpoints",
        ["checkpoint"] = "checkpoints",
        ["model_patches"] = "model_patches",
        ["audio_encoders"] = "audio_encoders",
        ["style_models"] = "style_models",
        ["gligen"] = "gligen",
        ["photomaker"] = "photomaker",
        ["hypernetworks"] = "hypernetworks",
        ["diffusers"] = "diffusers",
        ["detection"] = "detection",
        ["latent_upscale_models"] = "latent_upscale_models",
        ["frame_interpolation"] = "frame_interpolation",
        ["geometry_estimation"] = "geometry_estimation",
        ["optical_flow"] = "optical_flow",
        ["background_removal"] = "background_removal",
        ["classifiers"] = "classifiers",
        ["configs"] = "configs",
    };

    public static readonly string[] ModelExts =
    {
        ".safetensors", ".gguf", ".ckpt", ".pt", ".pth", ".bin", ".onnx", ".sft"
    };

    public static bool IsModelFile(string name) =>
        ModelExts.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>已知目录名（去重排序），用于下拉框</summary>
    public static List<string> KnownFolders() =>
        Aliases.Values.Distinct().OrderBy(x => x).ToList();

    /// <summary>从 URL 路径段（不含文件名）推断 ComfyUI 目录</summary>
    public static string? Detect(IEnumerable<string> segments)
    {
        var low = segments.Select(s => s.ToLowerInvariant()).ToList();
        int i = low.IndexOf("split_files");
        if (i >= 0 && i + 1 < low.Count && Aliases.TryGetValue(low[i + 1], out var f))
            return f;

        for (int k = low.Count - 1; k >= 0; k--)
            if (Aliases.TryGetValue(low[k], out var g))
                return g;
        return null;
    }

    /// <summary>从文件名猜目录</summary>
    public static string? GuessFromName(string fileName)
    {
        var low = (fileName ?? "").ToLowerInvariant();
        (string pat, string folder)[] hints =
        {
            (@"upscal|(?:^|[^0-9])[248]x", "upscale_models"),
            (@"controlnet|control[-_]?net", "controlnet"),
            (@"lora|lycoris|locon", "loras"),
            (@"vae", "vae"),
            (@"clip[-_]?vision", "clip_vision"),
            (@"clip|t5|text[-_]?enc", "text_encoders"),
            (@"embedding|textual", "embeddings"),
        };
        foreach (var (pat, folder) in hints)
            if (System.Text.RegularExpressions.Regex.IsMatch(low, pat))
                return folder;
        return null;
    }

    /// <summary>磁盘上真实存在的 models 子目录</summary>
    public static List<string> ExistingDirs(string modelsDir)
    {
        try
        {
            if (!Directory.Exists(modelsDir)) return new List<string>();
            return Directory.GetDirectories(modelsDir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith("."))
                .Select(n => n!)
                .OrderBy(n => n)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
