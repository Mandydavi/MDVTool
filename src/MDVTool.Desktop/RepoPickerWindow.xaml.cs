using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MDVTool;

public partial class RepoPickerWindow : Window
{
    private readonly List<RepoFile> _all;

    public List<RepoFile> SelectedFiles { get; private set; } = new();

    public RepoPickerWindow(string repoName, List<RepoFile> files)
    {
        InitializeComponent();
        _all = files;
        RepoText.Text = $"{repoName}  ·  共 {files.Count} 个文件";
        FileList.ItemsSource = files;
        UpdateSelText();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelText();

    private void UpdateSelText()
    {
        var sel = FileList.SelectedItems.Cast<RepoFile>().ToList();
        long total = sel.Sum(f => f.Size);
        SelText.Text = sel.Count == 0
            ? "未选择文件"
            : $"已选 {sel.Count} 个 · 共 {Fmt.Human(total)}";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        FileList.SelectAll();
        UpdateSelText();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var sel = FileList.SelectedItems.Cast<RepoFile>().ToList();
        if (sel.Count == 0)
        {
            MessageBox.Show(this, "至少选一个文件", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedFiles = sel;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
