using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace MDVTool;

/// <summary>新建任务时询问保存到哪个文件夹</summary>
public partial class FolderPickerWindow : Window
{
    private readonly string _baseDir;

    /// <summary>用户选择的目录：相对基准目录的子目录名，或绝对路径</summary>
    public string Folder { get; private set; } = "";

    /// <summary>是否勾选了「设为默认位置」</summary>
    public bool SetAsDefault => DefaultCheck.IsChecked == true;

    public FolderPickerWindow(string fileName, string baseDir, string? detected, string host)
    {
        InitializeComponent();

        _baseDir = string.IsNullOrWhiteSpace(baseDir) ? AppSettings.DefaultDownloadDir : baseDir;
        NameText.Text = fileName;
        MetaText.Text = string.IsNullOrEmpty(host) ? "新建下载任务" : $"来源：{host}";
        PrefixText.Text = LeafName(_baseDir) + "\\";

        HintText.Text = detected == null
            ? "这个文件不属于模型类型，会存到下载目录；也可以手动指定子文件夹。"
            : $"根据链接判断，这类文件通常放在 {detected}。";

        // 已有子目录作为候选项
        foreach (var d in ComfyFolders.ExistingDirs(_baseDir))
            FolderCombo.Items.Add(d);

        FolderCombo.Text = detected ?? "";

        Loaded += (_, _) =>
        {
            FolderCombo.Focus();
            // 让光标落到输入框末尾，方便直接改
            if (FolderCombo.Template.FindName("PART_EditableTextBox", FolderCombo) is System.Windows.Controls.TextBox tb)
            {
                tb.CaretIndex = tb.Text.Length;
                tb.SelectAll();
            }
            RefreshPreview();
        };
    }

    private static string LeafName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "目录";
        var t = path.TrimEnd('\\', '/');
        int i = t.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 && i < t.Length - 1 ? t[(i + 1)..] : t;
    }

    private bool IsAbsolute => Path.IsPathRooted(FolderCombo.Text?.Trim() ?? "");

    private string ResolvedPath
    {
        get
        {
            var t = (FolderCombo.Text ?? "").Trim().TrimEnd('\\', '/');
            if (t.Length == 0) return _baseDir;
            return Path.IsPathRooted(t) ? t : Path.Combine(_baseDir, t);
        }
    }

    private void RefreshPreview()
    {
        PrefixText.Visibility = IsAbsolute ? Visibility.Collapsed : Visibility.Visible;
        PathPreview.Text = ResolvedPath;
        PathPreview.ToolTip = ResolvedPath;
    }

    private void FolderCombo_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RefreshPreview();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择保存到哪个文件夹",
            InitialDirectory = Directory.Exists(ResolvedPath) ? ResolvedPath
                : (Directory.Exists(_baseDir) ? _baseDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        };
        if (dlg.ShowDialog(this) != true) return;

        var picked = dlg.FolderName;
        // 落在基准目录里面就转成相对子目录，否则用绝对路径
        var root = _baseDir.TrimEnd('\\', '/');
        if (picked.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
            picked.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            FolderCombo.Text = picked[(root.Length + 1)..].Replace('/', '\\');
        }
        else if (string.Equals(picked.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase))
        {
            FolderCombo.Text = "";
        }
        else
        {
            FolderCombo.Text = picked;
        }
        RefreshPreview();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var t = (FolderCombo.Text ?? "").Trim().TrimEnd('\\', '/');

        // 留空 = 直接放在基准目录下
        if (t.Length > 0 && !Path.IsPathRooted(t) && t.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "文件夹名里有不能用于路径的字符。", "选择保存位置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Folder = t;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) DialogResult = false;
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
            Ok_Click(this, new RoutedEventArgs());
    }
}
