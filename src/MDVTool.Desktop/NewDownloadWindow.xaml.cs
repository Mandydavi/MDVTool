using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MDVTool;

/// <summary>IDM 风格的「新建下载」窗口：地址 + 分类 + 存放位置 + 线程 → 开始下载</summary>
public partial class NewDownloadWindow : Window
{
    public string ResultUrl { get; private set; } = "";
    public string ResultFolder { get; private set; } = "";

    private static readonly string[] Categories =
        { "视频", "音乐", "程序", "压缩包", "文档", "图片", "其他文件" };

    private static readonly HashSet<string> ModelExts = new(StringComparer.OrdinalIgnoreCase)
        { ".safetensors", ".ckpt", ".pt", ".pth", ".gguf", ".onnx", ".bin" };

    public NewDownloadWindow(string initialUrl)
    {
        InitializeComponent();

        foreach (var c in Categories) CatCombo.Items.Add(new ComboBoxItem { Content = c });

        DirBox.Text = App.Settings.DownloadDir ?? "";
        var conns = new[] { 4, 8, 16, 32, 64 };
        int idx = Array.IndexOf(conns, App.Settings.Connections);
        ConnCombo.SelectedIndex = App.Settings.AutoConnections ? 0 : (idx >= 0 ? idx + 1 : 5);

        UrlBox.Text = initialUrl;
        UpdatePreview();

        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.CaretIndex = UrlBox.Text.Length;
        };
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        string url = (UrlBox.Text ?? "").Trim();
        string proto = "HTTP";
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) proto = "磁力";
        else if (url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) proto = "BT 种子";
        else if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) proto = "HLS";
        else if (url.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) proto = "DASH";
        else if (url.StartsWith("ftp", StringComparison.OrdinalIgnoreCase)) proto = "FTP";

        ProtoPill.Text = proto;

        string name = "";
        string host = "";
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            name = "磁力任务";
            host = "BitTorrent · DHT";
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(name)) name = "index.html";
            try { name = Uri.UnescapeDataString(name); } catch { }
        }

        if (name.Length == 0)
        {
            NameText.Text = "等待输入链接";
            HostText.Text = "—";
            ExtText.Text = "—";
            DetectText.Text = "等待输入链接…";
            return;
        }

        string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        ExtText.Text = ext.Length is > 0 and <= 4 ? ext : "FILE";
        NameText.Text = name;
        HostText.Text = host;

        // 模型扩展名不参与普通分类，直接标成「模型」，下载时由 ComfyFolders 归类
        string cat = ModelExts.Contains(Path.GetExtension(name).ToLowerInvariant())
            ? "模型"
            : CategoryRouter.CategoryOf(name);
        DetectText.Text = $"{proto} · {cat} · {name}";

        int i = Array.IndexOf(Categories, cat);
        if (i >= 0 && CatCombo.SelectedIndex != i) CatCombo.SelectedIndex = i;
    }

    private void CatCombo_Changed(object sender, SelectionChangedEventArgs e) { }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择保存目录",
            SelectedPath = Directory.Exists(DirBox.Text) ? DirBox.Text : (App.Settings.DownloadDir ?? "")
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DirBox.Text = dlg.SelectedPath;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        string url = (UrlBox.Text ?? "").Trim();
        if (url.Length == 0)
        {
            DetectText.Text = "先填下载地址";
            UrlBox.Focus();
            return;
        }

        // 线程选择写回默认设置，下次新建也是这个值
        int ci = ConnCombo.SelectedIndex;
        App.Settings.AutoConnections = ci <= 0;
        App.Settings.Connections = ci switch { 1 => 4, 2 => 8, 3 => 16, 4 => 32, 5 => 64, _ => 64 };

        string dir = (DirBox.Text ?? "").Trim();
        if (dir.Length > 0 && Directory.Exists(dir)) App.Settings.DownloadDir = dir;
        App.Settings.Save();

        ResultUrl = url;
        ResultFolder = Directory.Exists(dir) ? dir : (App.Settings.DownloadDir ?? "");
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
