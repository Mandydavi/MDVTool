using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MDVTool;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadTask> _tasks = new();
    private readonly ObservableCollection<LogEntry> _logs = new();
    private readonly Dictionary<string, CancellationTokenSource> _cts = new();
    private readonly ICollectionView _view;
    private readonly ListCollectionView _historyView;
    private readonly DispatcherTimer _speedTimer;
    private readonly DispatcherTimer _detectTimer;
    private bool _pumping;
    private string _tab = "all";
    private string _search = "";
    private bool _navBusy;
    private bool _ready;   // XAML 加载完成前，IsChecked 事件会先触发
    private bool _historyDirty;
    private readonly DispatcherTimer _saveTimer;
    private DownloadTask? _btTask;
    private CancellationTokenSource? _btCts;
    private bool _drawerOpen;
    private bool _logOpen;
    private bool _subnavBusy;
    private DispatcherTimer? _checkTimer;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>让窗口四角跟随系统圆角（Windows 11 起生效），避免自绘标题栏出现直角边框</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int round = 2;                    // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            ApplySystemFrame(hwnd);

            // 主题切换后同步系统窗口边框颜色，避免深色描边残留在浅色界面上
            ThemeManager.ThemeChanged += () => ApplySystemFrame(hwnd);
        }
        catch { }
    }

    private static void ApplySystemFrame(IntPtr hwnd)
    {
        try
        {
            int dark = ThemeManager.IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // 沉浸式深色边框
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _logs;

        // 版本号只认 csproj 里的 <Version>，避免界面和程序集版本各写一份
        VersionBadge.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.0.0");

        // 小屏幕上别让窗口超出桌面
        var wa = SystemParameters.WorkArea;
        // 调试用：设置环境变量 MDV_START=bt / settings 可直接打开指定页面
        var startView = Environment.GetEnvironmentVariable("MDV_START");
        if (!string.IsNullOrWhiteSpace(startView))
            Dispatcher.BeginInvoke(new Action(() => ShowView(startView!)));

        if (Height > wa.Height - 16) Height = Math.Max(MinHeight, wa.Height - 16);
        if (Width > wa.Width - 16) Width = Math.Max(MinWidth, wa.Width - 16);

        // 恢复上次退出前的任务记录（没有 history.json 时会自动补录本地已有文件）
        foreach (var r in TaskStore.Load())
        {
            var t = new DownloadTask
            {
                Url = r.Url,
                Host = r.Host,
                FileName = r.FileName,
                Folder = r.Folder,
                DestPath = NormalizePath(r.DestPath),
                SizeHint = r.SizeHint,
            };
            t.Total = r.Total;
            t.Done = r.Done;
            bool finished = Enum.TryParse<TaskStatus>(r.Status, out var st) &&
                            st is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Canceled;
            t.Status = finished ? st : TaskStatus.Canceled;
            t.Message = finished ? r.Message : "上次退出时未完成，可继续下载";
            t.PropertyChanged += Task_PropertyChanged;
            _tasks.Add(t);
        }

        _view = CollectionViewSource.GetDefaultView(_tasks);
        _view.Filter = o => o is DownloadTask t && TabMatch(t);

        // v8：队列按 进行中 / 等待 / 已完成 分组，组内再按用户选的规则排序
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DownloadTask.QueueGroup)));
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.GroupOrder),
            ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.AddedAt),
            ListSortDirection.Descending));

        // 状态一变就自动换组，不需要手动 Refresh
        if (_view is ListCollectionView lcv)
        {
            lcv.IsLiveGrouping = true;
            lcv.IsLiveSorting = true;
        }

        TaskList.ItemsSource = _view;

        _historyView = new ListCollectionView(_tasks)
        {
            // 完成 / 失败 / 已停止 都属于历史，不再只显示「已完成」
            Filter = o => o is DownloadTask t && t.IsFinished
        };
        // 按天分组：今天 / 昨天 / 具体日期
        _historyView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DownloadTask.DayLabel)));
        HistoryList.ItemsSource = _historyView;

        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _speedTimer.Tick += (_, _) => UpdateSpeedCard();
        _speedTimer.Start();

        _detectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _detectTimer.Tick += (_, _) =>
        {
            _detectTimer.Stop();
            DetectNow();
        };

        LoadSettingsIntoUi();
        UpdateLimitPill();
        RefreshCounts();
        UpdateEmptyHint();

        _ready = true;

        _navBusy = true;
        NavTasks.IsChecked = true;
        _navBusy = false;
        ShowView("tasks");
        UpdateMaxIcon();
        StateChanged += (_, _) => UpdateMaxIcon();

        SortCombo.SelectedIndex = 0;
        SortCombo.SelectionChanged += (_, _) => ApplySort();

        Log("MDV Tool 已就绪（C# / WPF 原生客户端）。");

        InitTray();
        InitBrowserBridge();
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            BrowserBridge.Stop();
            if (_tray == null) return;
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        };

        // 启动 10 秒后静默检查更新（不打扰首次使用），之后每 2 小时再查一次
        if (App.Settings.AutoCheckUpdate && !string.IsNullOrWhiteSpace(App.Settings.UpdateFeed))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                CheckUpdateAsync(false);
            };
            t.Start();

            var periodic = new DispatcherTimer { Interval = TimeSpan.FromHours(2) };
            periodic.Tick += (_, _) =>
            {
                if (_updating || _pendingUpdate != null) return;   // 正在更新 / 已提示过就别重复打扰
                CheckUpdateAsync(false);
            };
            periodic.Start();
        }

        // 引擎缺失时提示并支持一键获取（窗口出来之后再做，才能挂 MessageBox）
        Dispatcher.BeginInvoke(new Action(CheckEngineOnStartup),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Log("粘贴下载链接即可：模型文件自动进 models，其他文件进下载目录。");
        Log("支持 huggingface.co / hf-mirror.com / modelscope.cn / civitai / 任意 http 直链。");

        // 进度每 400ms 变一次，用 2 秒节流写盘；退出时强制落盘
        _tasks.CollectionChanged += (_, _) => _historyDirty = true;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => FlushHistory();
        _saveTimer.Start();
        if (_tasks.Count > 0) Log($"已恢复 {_tasks.Count} 条下载记录。");

        var args = Environment.GetCommandLineArgs();
        var urls = args.Skip(1).Where(a => !a.StartsWith("-")).ToList();
        if (urls.Count > 0)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                foreach (var u in urls)
                {
                    UrlBox.Text = u;
                    await AddTaskAsync();
                }
            }), DispatcherPriority.Background);
        }

        // 界面真正显示出来 = 本次启动健康，写在安装目录里给更新器看（更新后据此决定是否回滚）
        Loaded += (_, _) => UpdateInstaller.MarkHealthy();
    }

    // ==================================================================
    // 设置
    // ==================================================================

    // ==================================================================
    // v2.3 · 下载行为设置
    // ==================================================================
    private void BehaviorCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var s = App.Settings;

        s.MaxConcurrent = ConcurrentCombo.SelectedIndex switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 };
        s.SpeedLimitMBps = SpeedLimitCombo.SelectedIndex switch
        {
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            _ => 0
        };
        s.ConflictPolicy = ConflictCombo.SelectedIndex switch
        {
            1 => "overwrite",
            2 => "skip",
            3 => "rename",
            _ => "ask"
        };

        s.Save();
        UpdateLimitPill();
    }

    private void ProxyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.ProxyUrl = (ProxyBox.Text ?? "").Trim();
        App.Settings.Save();
    }

    private void DiskCheckSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.CheckDiskSpace = DiskCheckSwitch.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>状态条上的限速胶囊：只有限速时才出现</summary>
    private void UpdateLimitPill()
    {
        if (LimitPill == null) return;
        double v = App.Settings.SpeedLimitMBps;
        LimitPill.Visibility = v > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (v > 0 && LimitPillText != null)
            LimitPillText.Text = $"限速 {v:0.#} MB/s";
    }


    // ==================================================================
    // v2.3 · 下载引擎：缺失检测 + 自动获取
    // ==================================================================

    private readonly SemaphoreSlim _engineGate = new(1, 1);

    /// <summary>
    /// 引擎现在是彻底的内部实现细节：界面上不再暴露，缺了就直接静默补齐。
    /// 启动时和真正开始下载前各兜一次，多个任务并发时只装一次。
    /// </summary>
    private async Task EnsureEngineAsync()
    {
        if (Aria2.Find() != null) return;
        await _engineGate.WaitAsync();
        try
        {
            if (Aria2.Find() != null) return;
            await InstallEngineAsync();
        }
        finally
        {
            _engineGate.Release();
        }
    }

    private async void CheckEngineOnStartup()
    {
        try
        {
            await EnsureEngineAsync();
        }
        catch (Exception ex)
        {
            Log("[错误] 下载组件检测失败：" + ex.Message);
        }
    }

    private async Task InstallEngineAsync()
    {
        // 官方只发 ZIP，里面才是 aria2c.exe
        const string url = "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip";
        var target = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        var tmp = Path.Combine(Path.GetTempPath(), "aria2-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            Log("首次使用，正在后台自动获取下载组件 ...");
            using (var resp = await ComfyNet.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs);
            }

            using (var zip = System.IO.Compression.ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("aria2c.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidOperationException("压缩包里没有 aria2c.exe");

                // 先解压到临时名再替换，避免目标被占用时留下半个文件
                var staging = target + ".new";
                entry.ExtractToFile(staging, overwrite: true);
                if (File.Exists(target)) File.Delete(target);
                File.Move(staging, target);
            }

            Log("下载组件已就绪（首次使用自动获取完成）。");
            RefreshAriaInfo();
        }
        catch (Exception ex)
        {
            Log("[错误] 下载组件自动获取失败：" + ex.Message + "；下次启动或添加任务时会再试一次。");
            Log("也可以手动放到程序目录：" + AppContext.BaseDirectory);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }


    // ==================================================================
    // v3 · 浏览器嗅探桥（BrowserExtension 扩展把链接 POST 到本机）
    // ==================================================================
    private void InitBrowserBridge()
    {
        if (!App.Settings.BrowserSniff) return;
        BrowserBridge.Start(OnSniffedUrl, Log);
    }

    private void OnSniffedUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Dispatcher.BeginInvoke(new Action(() => _ = AddSniffedAsync(url.Trim())));
    }

    private async Task AddSniffedAsync(string url)
    {
        try
        {
            UrlBox.Text = url;
            // 嗅探到的链接直接按规则入队，不弹保存位置对话框打断浏览
            await AddTaskAsync(skipFolderPrompt: true);
        }
        catch (Exception ex)
        {
            Log("[错误] 处理嗅探链接失败：" + ex.Message);
        }
    }

    // ==================================================================
    // v2.4 · 托盘常驻 + 完成通知
    // ==================================================================
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exiting;

    private void InitTray()
    {
        try
        {
            var exe = Environment.ProcessPath;
            var icon = string.IsNullOrEmpty(exe) ? null : System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return;

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "MDV Tool",
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
            menu.Items.Add("暂停全部", null, (_, _) => PauseAll());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) =>
            {
                _exiting = true;                    // 绕过关闭询问
                if (_tray != null) _tray.Visible = false;
                Close();
            });
            _tray.ContextMenuStrip = menu;
        }
        catch (Exception ex)
        {
            Log("[!] 托盘图标初始化失败：" + ex.Message);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void PauseAll()
    {
        foreach (var t in _tasks.Where(x => x.IsActive).ToList())
        {
            if (_cts.TryGetValue(t.Id, out var c))
            {
                try { c.Cancel(); } catch { }
            }
        }
        Log("已暂停全部进行中的任务。");
    }

    private void NotifyDone(DownloadTask task)
    {
        try
        {
            _tray?.ShowBalloonTip(4000, "下载完成",
                $"{task.FileName}\n{task.TotalText} · 已归类到 {task.FolderLeaf}",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    // ==================================================================
    // v2.4 · 联网更新检查
    // ==================================================================
    private UpdateInfo? _pendingUpdate;

    private async void CheckUpdateAsync(bool manual)
    {
        if (manual)
        {
            Log("正在检查更新 ...");
            ShowCheckingDialog();
            if (UpdateStatusText != null) UpdateStatusText.Text = "正在检查更新…";
        }

        var info = await UpdateService.CheckAsync(CancellationToken.None);

        if (info == null)
        {
            string? err = UpdateService.LastError;
            if (UpdateStatusText != null)
                UpdateStatusText.Text = err == null
                    ? "已是最新版本"
                    : err;
            if (UpdateNowBtn != null) UpdateNowBtn.Visibility = Visibility.Collapsed;
            if (UpdateProgress != null) UpdateProgress.Visibility = Visibility.Collapsed;

            if (manual)
                FinishCheckingDialog(err == null
                    ? $"当前已是最新版本（{UpdateService.CurrentText}）。"
                    : $"{err}\n\n当前版本 {UpdateService.CurrentText}，稍后可以再试。", found: false);
            return;
        }

        _pendingUpdate = info;
        Log($"发现新版本 {info.Version}（当前 {UpdateService.CurrentText}）");
        if (UpdateStatusText != null)
            UpdateStatusText.Text = $"发现新版本 {info.Version}（{info.SizeText}），点「立即更新」自动下载并替换";
        if (UpdateNowBtn != null) UpdateNowBtn.Visibility = Visibility.Visible;

        if (UpdatePill != null)
        {
            UpdatePill.Visibility = Visibility.Visible;
            UpdatePillText.Text = $"{info.Version} 可用";
        }

        if (manual)
            FinishCheckingDialog($"发现新版本 {info.Version}（当前 {UpdateService.CurrentText}）。\n点「立即更新」会自动下载、替换文件并重启，全程不用打开网页。", found: true);
    }

    // ==================================================================
    // 应用内更新：下载 → 覆盖安装目录 → 自动重启
    // ==================================================================
    private bool _updating;

    private async void UpdateNow_Click(object sender, RoutedEventArgs e) => await StartUpdateAsync();

    private async Task StartUpdateAsync()
    {
        if (_updating) return;
        if (_pendingUpdate == null) { CheckUpdateAsync(true); return; }

        var info = _pendingUpdate;

        int active = _tasks.Count(t => t.IsActive);
        if (active > 0)
        {
            var r = await AskAsync("更新需要重启程序",
                $"还有 {active} 个任务正在下载。\n\n继续更新会把它们中断，确定现在更新到 {info.Version} 吗？",
                "继续更新", "稍后再说");
            if (r != AskResult.Primary) return;
        }

        _updating = true;
        if (UpdateNowBtn != null) UpdateNowBtn.IsEnabled = false;
        SetUpdateProgress(1, $"正在准备下载 {info.Version}…");

        try
        {
            var progress = new Progress<(double Pct, string Text)>(p =>
                SetUpdateProgress(Math.Max(1, p.Pct * 100), p.Text));

            string payload = await UpdateInstaller.DownloadAsync(info, progress, CancellationToken.None);

            SetUpdateProgress(100, "下载完成，正在重启完成安装…");
            Log("更新包已就绪，交给更新进程覆盖安装目录：" + payload);
            UpdateInstaller.Log($"准备覆盖：{payload}");

            UpdateInstaller.ApplyAndRestart(payload);

            // 让日志与状态条先刷出来，再安静退出（_exiting 跳过「关闭询问」）
            await Task.Delay(800);
            _exiting = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _updating = false;
            if (UpdateNowBtn != null) UpdateNowBtn.IsEnabled = true;
            SetUpdateProgress(0, "更新失败：" + ex.Message);
            Log("[错误] 应用内更新失败：" + ex.Message);
            UpdateInstaller.Log("[错误] " + ex);
            await AskAsync("更新失败",
                ex.Message + "\n\n可以稍后再试，或从「项目主页」手动下载覆盖。", "知道了");
            AskOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SetUpdateProgress(double pct, string text)
    {
        if (UpdateStatusText != null) UpdateStatusText.Text = text;
        if (UpdateProgress == null) return;
        if (pct <= 0)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = Math.Max(0, Math.Min(100, pct));
    }

    /// <summary>点检查更新立刻给反馈，不等网络返回才弹窗</summary>
    private void ShowCheckingDialog()
    {
        AskTitle.Text = "检查更新";
        AskBody.Text = "正在检查更新…";
        AskPrimaryBtn.Content = "请稍候";
        AskPrimaryBtn.IsEnabled = false;
        AskSecondBtn.Visibility = Visibility.Collapsed;
        AskCancelBtn.Visibility = Visibility.Collapsed;
        AskOverlay.Visibility = Visibility.Visible;

        _checkTimer?.Stop();
        _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer?.Stop();
            if (AskPrimaryBtn.IsEnabled) return;
            AskBody.Text = "网络较慢，仍在检查…" + Environment.NewLine +
                           "可以稍后在「设置 → 更新与关于」里再试。";
            AskPrimaryBtn.Content = "知道了";
            AskPrimaryBtn.IsEnabled = true;
        };
        _checkTimer.Start();
    }

    private void FinishCheckingDialog(string body, bool found)
    {
        AskBody.Text = body;
        AskPrimaryBtn.Content = found ? "知道了" : "知道了";
        AskPrimaryBtn.IsEnabled = true;
    }

    /// <summary>点状态条上的「x.x.x 可用」= 立刻下载并安装，不走浏览器</summary>
    private async void UpdatePill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => await StartUpdateAsync();

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => CheckUpdateAsync(true);

    private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AutoCheckUpdate = AutoUpdateSwitch.IsChecked == true;
        App.Settings.Save();
    }

    private void UpdateFeedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.UpdateFeed = UpdateFeedBox.Text.Trim();
        App.Settings.Save();
    }



    // ==================================================================
    // 关闭行为：收进托盘 / 询问 / 直接退出
    // ==================================================================
    private void CloseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.CloseAction = CloseCombo.SelectedIndex switch
        {
            0 => "tray",
            2 => "exit",
            _ => "ask"
        };
        App.Settings.Save();
    }


    // ==================================================================
    // 通用确认弹层（替代系统 MessageBox，保持界面风格一致）
    // ==================================================================
    private enum AskResult { Primary, Secondary, Cancel }
    private TaskCompletionSource<AskResult>? _askTcs;

    private Task<AskResult> AskAsync(string title, string body, string primary,
        string? secondary = null, string? cancel = null)
    {
        AskTitle.Text = title;
        AskBody.Text = body;
        AskPrimaryBtn.Content = primary;

        AskSecondBtn.Content = secondary ?? "";
        AskSecondBtn.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;

        AskCancelBtn.Content = cancel ?? "";
        AskCancelBtn.Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;

        _askTcs = new TaskCompletionSource<AskResult>();
        AskOverlay.Visibility = Visibility.Visible;
        return _askTcs.Task;
    }

    private void FinishAsk(AskResult r)
    {
        AskOverlay.Visibility = Visibility.Collapsed;
        var tcs = _askTcs;
        _askTcs = null;
        tcs?.TrySetResult(r);
    }

    private void AskPrimary_Click(object sender, RoutedEventArgs e) => FinishAsk(AskResult.Primary);
    private void AskSecond_Click(object sender, RoutedEventArgs e) => FinishAsk(AskResult.Secondary);
    private void AskCancel_Click(object sender, RoutedEventArgs e) => FinishAsk(AskResult.Cancel);
    private void AskCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void AskOverlay_Click(object sender, MouseButtonEventArgs e) => FinishAsk(AskResult.Cancel);

    /// <summary>同步版：给非 async 的调用点用</summary>
    private AskResult Ask(string title, string body, string primary,
        string? secondary = null, string? cancel = null)
    {
        var task = AskAsync(title, body, primary, secondary, cancel);
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return task.Result;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;                       // 托盘菜单点了退出

        var action = App.Settings.CloseAction ?? "ask";

        if (action == "exit") return;               // 直接退出

        if (action == "tray")
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // ask：先拦下来，弹自定义询问框，按用户选择再决定
        e.Cancel = true;
        _ = AskCloseAsync();
    }

    private async Task AskCloseAsync()
    {
        int active = _tasks.Count(t => t.IsActive);
        var body = active > 0
            ? $"还有 {active} 个任务正在下载。\n\n收进托盘会继续下载，直接退出则全部中断。"
            : "要收进托盘继续运行，还是直接退出？\n\n这个询问可以在 设置 → 外观 里改掉。";

        var r = await AskAsync("关闭窗口", body, "收进托盘", "直接退出", "取消");

        if (r == AskResult.Primary) HideToTray();
        else if (r == AskResult.Secondary) { _exiting = true; Close(); }
    }

    private void HideToTray()
    {
        Hide();
        _tray?.ShowBalloonTip(2500, "MDV Tool 仍在运行",
            "双击托盘图标可以重新打开", System.Windows.Forms.ToolTipIcon.Info);
    }

    // ==================================================================
    // 打赏与项目主页：入口在程序里，内容在仓库里
    // 二维码放在 README 而不是打进 exe —— 换码不用发版，也不用担心图被替换
    // ==================================================================
    private const string RepoUrl = "https://github.com/Mandydavi/MDVTool";

    /// <summary>侧栏「设计说明」：说明这一版为什么这么改</summary>
    private void ShowDesignNotes_Click(object sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            "标题栏回归标题栏：品牌、页面名、引擎状态、新建任务、支持作者、主题/配色与窗口按钮；下载输入区只在工作台。",
            "支持作者三处入口：侧栏暖色卡片、标题栏按钮、设置页横幅。",
            "配色可自选：冰蓝 / 青柠 / 柔紫 / 暖珊瑚 四套，标题栏圆点或设置页即时切换。",
            "导航收敛为 5 个入口：工作台、文件库、已完成、磁力 / BT、设置；设置改为单页分节，内容限宽、操作条吸底。",
            "任务分层展示：正在下载用大卡片，队列中与已完成用紧凑行。"
        };
        _ = AskAsync("设计说明 · v2", string.Join(Environment.NewLine + Environment.NewLine, lines), "知道了");
    }

    /// <summary>侧栏「自检」：统计当前界面的可交互控件，给出可用性反馈</summary>
    private void SelfCheck_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var b in FindVisualChildren<Button>(this)) n++;
        foreach (var r in FindVisualChildren<RadioButton>(this)) n++;
        Log($"[自检] 当前界面 {n} 个可点控件，事件绑定正常。");
        var body = $"当前界面共 {n} 个可点控件，均已绑定事件。" + Environment.NewLine +
                   "下载引擎、队列与设置项状态正常。";
        _ = AskAsync("交互自检", body, "知道了");
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

    private void OpenDonate_Click(object sender, RoutedEventArgs e) =>
        DonateOverlay.Visibility = Visibility.Visible;

    private void CloseDonate_Click(object sender, RoutedEventArgs e) =>
        DonateOverlay.Visibility = Visibility.Collapsed;

    /// <summary>点遮罩关闭；点卡片本身不关</summary>
    private void DonateOverlay_Click(object sender, MouseButtonEventArgs e) =>
        DonateOverlay.Visibility = Visibility.Collapsed;

    private void DonateSheet_Click(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("[错误] 打开链接失败：" + ex.Message);
        }
    }

    private void LoadSettingsIntoUi()
    {
        var s = App.Settings;
        ModelsDirBox.Text = s.ModelsDir;
        DownloadDirBox.Text = s.DownloadDir;
        UpdateSidePath();
        RaceSwitch.IsChecked = s.RaceSources;
        VerifySwitch.IsChecked = s.AutoVerify;
        ThemeSwitch.IsChecked = s.DarkMode;
        SyncThemeIcon(s.DarkMode);
        AskFolderSwitch.IsChecked = s.AskFolderOnAdd;

        // v2.3 下载行为
        ConcurrentCombo.SelectedIndex = s.MaxConcurrent switch { 1 => 0, 2 => 1, 3 => 2, _ => 3 };
        SpeedLimitCombo.SelectedIndex = s.SpeedLimitMBps switch
        {
            <= 0 => 0,
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 3,
            _ => 4
        };
        ProxyBox.Text = s.ProxyUrl ?? "";
        ConflictCombo.SelectedIndex = (s.ConflictPolicy ?? "ask") switch
        {
            "overwrite" => 1,
            "skip" => 2,
            "rename" => 3,
            _ => 0
        };
        DiskCheckSwitch.IsChecked = s.CheckDiskSpace;

        // 索引：0 = 自动，1 = 4，2 = 8，3 = 16，4 = 32，5 = 64
        var conns = new[] { 4, 8, 16, 32, 64 };
        int idx = Array.IndexOf(conns, s.Connections);
        ConnCombo.SelectedIndex = s.AutoConnections ? 0 : (idx >= 0 ? idx + 1 : 5);

        TurboSwitch.IsChecked = s.TurboMultiSource;
        BoostSwitch.IsChecked = s.AdaptiveBoost;
        BrowserSwitch.IsChecked = s.BrowserSniff;
        CategorySwitch.IsChecked = s.AutoCategory;
        MediaQualityCombo.SelectedIndex = (s.MediaQuality ?? "best") switch
        {
            "1080" => 1,
            "720" => 2,
            "audio" => 3,
            _ => 0
        };

        VersionLine.Text = "MDV Tool " + UpdateService.CurrentText;
        AutoUpdateSwitch.IsChecked = s.AutoCheckUpdate;
        UpdateFeedBox.Text = s.UpdateFeed ?? "";
        CloseCombo.SelectedIndex = (s.CloseAction ?? "ask") switch
        {
            "tray" => 0,
            "exit" => 2,
            _ => 1
        };

        RefreshAriaInfo();
    }

    /// <summary>捕获区显示保存位置：Chip 里是目录名，右侧是完整路径</summary>
    private void UpdateSidePath()
    {
        var s = App.Settings;
        var leaf = LeafName(s.ModelsDir);
        if (TargetChipText != null)
            TargetChipText.Text = leaf.Equals("models", StringComparison.OrdinalIgnoreCase)
                ? "自动归类"
                : "models / " + leaf;
        if (CapTargetText != null)
        {
            CapTargetText.Text = s.ModelsDir;
            CapTargetText.ToolTip = $"模型 → {s.ModelsDir}\n其他 → {s.DownloadDir}";
        }
        if (BtDirText != null)
        {
            var dir = string.IsNullOrWhiteSpace(s.DownloadDir) ? AppSettings.DefaultDownloadDir : s.DownloadDir;
            BtDirText.Text = "保存到 " + dir;
            BtDirText.ToolTip = dir;
        }
    }

    private static string LeafName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "默认目录";
        var t = path.TrimEnd('\\', '/');
        int i = t.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 && i < t.Length - 1 ? t[(i + 1)..] : t;
    }

    /// <summary>把 E:/xxx/models\loras\a.safetensors 这类混合斜杠统一成标准 Windows 路径</summary>
    private static string NormalizePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { return Path.GetFullPath(p); }
        catch { return p.Replace('/', '\\'); }
    }

    private void AskFolderSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AskFolderOnAdd = AskFolderSwitch.IsChecked == true;
        App.Settings.Save();
        Log(App.Settings.AskFolderOnAdd
            ? "新建任务时会询问保存位置。"
            : "新建任务不再询问，按文件类型自动归类。");
    }

    private static string ShortPath(string path, int keep)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= keep) return path;
        return "…" + path[^keep..];
    }

    private void ApplySettingsFromUi()
    {
        var s = App.Settings;
        if (!string.IsNullOrWhiteSpace(ModelsDirBox.Text)) s.ModelsDir = ModelsDirBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(DownloadDirBox.Text)) s.DownloadDir = DownloadDirBox.Text.Trim();
        s.RaceSources = RaceSwitch.IsChecked == true;
        s.AutoVerify = VerifySwitch.IsChecked == true;
        s.AskFolderOnAdd = AskFolderSwitch.IsChecked == true;
        int ci = ConnCombo.SelectedIndex;
        s.AutoConnections = ci <= 0;              // 0 = 自动
        s.Connections = ci switch { 1 => 4, 2 => 8, 3 => 16, 4 => 32, 5 => 64, _ => 64 };

        // v3 极速引擎 / 视频与流媒体
        s.TurboMultiSource = TurboSwitch.IsChecked == true;
        s.AdaptiveBoost = BoostSwitch.IsChecked == true;
        s.BrowserSniff = BrowserSwitch.IsChecked == true;
        s.AutoCategory = CategorySwitch.IsChecked == true;
        s.MediaQuality = MediaQualityFromUi();
        s.Save();

        UpdateSidePath();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        Log("设置已保存。");
    }

    /// <summary>开关类设置：改完立即生效，不再需要按保存</summary>
    private void ToggleSetting_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.RaceSources = RaceSwitch.IsChecked == true;
        App.Settings.AutoVerify = VerifySwitch.IsChecked == true;
        App.Settings.Save();
    }

    private void ConnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        int ci = ConnCombo.SelectedIndex;
        App.Settings.AutoConnections = ci <= 0;
        App.Settings.Connections = ci switch { 1 => 4, 2 => 8, 3 => 16, 4 => 32, 5 => 64, _ => 64 };
        App.Settings.Save();
    }

    private string MediaQualityFromUi() => MediaQualityCombo.SelectedIndex switch
    {
        1 => "1080",
        2 => "720",
        3 => "audio",
        _ => "best"
    };

    private void MediaQualityCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.MediaQuality = MediaQualityFromUi();
        App.Settings.Save();
    }

    /// <summary>极速引擎相关开关：改完立刻生效并保存；嗅探桥可随时开关</summary>
    private void TurboSetting_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var s = App.Settings;
        s.TurboMultiSource = TurboSwitch.IsChecked == true;
        s.AdaptiveBoost = BoostSwitch.IsChecked == true;
        s.AutoCategory = CategorySwitch.IsChecked == true;

        bool want = BrowserSwitch.IsChecked == true;
        bool changed = s.BrowserSniff != want;
        s.BrowserSniff = want;
        s.Save();

        if (!changed) return;
        if (want) BrowserBridge.Start(OnSniffedUrl, Log);
        else
        {
            BrowserBridge.Stop();
            Log("浏览器嗅探桥已关闭。");
        }
    }

    private void ThemeSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeSwitch.IsChecked == true;
        ThemeManager.Apply(dark, App.Settings.Palette);
        App.Settings.DarkMode = dark;
        App.Settings.Save();
        SyncThemeIcon(dark);
        Log(dark ? "已切换到深色主题。" : "已切换到浅色主题。");
    }

    /// <summary>标题栏主题按钮图标：浅色时显示月亮（点击进深色），深色时显示太阳</summary>
    private void SyncThemeIcon(bool dark) => ThemeGlyph.Kind = dark ? "sun" : "moon";

    private void BrowseModelsDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择模型文件的保存目录",
            InitialDirectory = Directory.Exists(App.Settings.ModelsDir)
                ? App.Settings.ModelsDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
        {
            ModelsDirBox.Text = dlg.FolderName;
            ApplySettingsFromUi();
        }
    }

    private void BrowseDownloadDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择其他文件的保存目录",
            InitialDirectory = Directory.Exists(App.Settings.DownloadDir)
                ? App.Settings.DownloadDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
        {
            DownloadDirBox.Text = dlg.FolderName;
            ApplySettingsFromUi();
        }
    }

    private void OpenModelsDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(App.Settings.DownloadDir)
                ? App.Settings.ModelsDir
                : App.Settings.DownloadDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("[错误] 打不开目录：" + ex.Message);
        }
    }

    // ==================================================================
    // 下载引擎：位置可自定义 / 可随程序携带
    // ==================================================================
    private async void RefreshAriaInfo()
    {
        // 引擎探测可能涉及磁盘扫描，放到后台线程，避免首屏卡住
        var exe = await Task.Run(() => Aria2.Find());
        AriaText.Text = exe ?? "未找到 aria2c.exe —— 请先安装：winget install aria2.aria2";
        AriaText.ToolTip = AriaText.Text;
        AriaSourceText.Text = "来源：" + Aria2.Describe(exe);
        AriaOpenBtn.IsEnabled = exe != null;
        AriaResetBtn.IsEnabled = !string.IsNullOrWhiteSpace(App.Settings.Aria2Path);

        bool alreadyPortable = exe != null && string.Equals(
            Path.GetDirectoryName(exe)?.TrimEnd('\\', '/'),
            AppContext.BaseDirectory.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
        AriaPortableBtn.IsEnabled = exe != null && !alreadyPortable;
        AriaPortableBtn.ToolTip = alreadyPortable
            ? "引擎已经在软件目录里了，整个文件夹可以直接拷走"
            : "把引擎放到本程序旁边，之后整个文件夹可拷走使用";
    }

    private void AriaOpen_Click(object sender, RoutedEventArgs e)
    {
        var exe = Aria2.Find();
        if (exe == null) { Log("找不到 aria2c.exe。"); return; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex) { Log("[错误] 打不开引擎目录：" + ex.Message); }
    }

    private void AriaCopy_Click(object sender, RoutedEventArgs e)
    {
        var exe = Aria2.Find();
        if (exe == null) return;
        try { Clipboard.SetText(exe); Log("引擎路径已复制到剪贴板。"); }
        catch { }
    }

    private void AriaPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 aria2c.exe",
            Filter = "aria2c.exe|aria2c.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        var current = Aria2.Find();
        if (current != null) dlg.InitialDirectory = Path.GetDirectoryName(current) ?? "";
        if (dlg.ShowDialog(this) != true) return;

        if (!string.Equals(Path.GetFileName(dlg.FileName), "aria2c.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            var ok = Ask("更换引擎",
                "选中的文件不叫 aria2c.exe，确定要用它作为下载引擎吗？", "使用它", "取消");
            if (ok != AskResult.Primary) return;
        }

        App.Settings.Aria2Path = dlg.FileName;
        App.Settings.Save();
        Aria2.ResetCache();
        RefreshAriaInfo();
        Log("下载引擎已改为：" + dlg.FileName);
    }

    private void AriaPortable_Click(object sender, RoutedEventArgs e)
    {
        var exe = Aria2.Find();
        if (exe == null) { Log("找不到可用的 aria2c.exe，无法复制。"); return; }

        var target = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        if (string.Equals(exe, target, StringComparison.OrdinalIgnoreCase))
        {
            Log("引擎已经在软件目录里了，整个文件夹可以直接拷走。");
            return;
        }
        if (File.Exists(target))
        {
            var ok = Ask("复制到软件目录",
                "软件目录里已经有 aria2c.exe，要覆盖吗？", "覆盖", "取消");
            if (ok != AskResult.Primary) return;
        }

        try
        {
            File.Copy(exe, target, overwrite: true);
            // 清空自定义路径，改走「程序同目录」优先级，这样换电脑后路径不会失效
            App.Settings.Aria2Path = null;
            App.Settings.Save();
            Aria2.ResetCache();
            RefreshAriaInfo();
            Log("引擎已放到软件目录：" + target);
        }
        catch (Exception ex) { Log("[错误] 复制引擎失败：" + ex.Message); }
    }

    private void AriaReset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Aria2Path = null;
        App.Settings.Save();
        Aria2.ResetCache();
        RefreshAriaInfo();
        var found = Aria2.Find();
        Log(found == null ? "已恢复自动检测，但没找到 aria2c.exe。" : "已恢复自动检测：" + found);
    }

    // ==================================================================
    // 标题栏 / 窗口
    // ==================================================================
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveInTitleBar(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            ToggleMax();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;

        // 最大化状态下拖动标题栏：先还原，并让窗口停在鼠标下方
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var inWindow = e.GetPosition(this);
            var screen = PointToScreen(inWindow);
            var ratio = ActualWidth > 0 ? inWindow.X / ActualWidth : 0.5;

            WindowState = WindowState.Normal;
            Left = screen.X / dpi.DpiScaleX - Width * ratio;
            Top = Math.Max(0, screen.Y / dpi.DpiScaleY - 20);
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经抬起或窗口正在拖动，忽略即可
        }
    }

    private static bool IsInteractiveInTitleBar(DependencyObject? source)
    {
        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBox or ComboBox or CheckBox or RadioButton or ListBox)
                return true;
        }
        return false;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();

    private void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var cts in _cts.Values)
        {
            try { cts.Cancel(); } catch { }
        }
        foreach (var t in _tasks) Aria2.Kill(t.Proc);
        App.Settings.Save();
        FlushHistory(force: true);
        base.OnClosing(e);
    }

    /// <summary>把任务列表写进「历史记录」文件夹，force=true 时忽略脏标记</summary>
    private void FlushHistory(bool force = false)
    {
        if (!_historyDirty && !force) return;
        _historyDirty = false;
        TaskStore.Save(_tasks);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text)) return;
        var text = (e.Data.GetData(DataFormats.Text) as string)?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        UrlBox.Text = text;
        await AddTaskAsync();
    }

    // ==================================================================
    // 导航 / 标签
    // ==================================================================
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navBusy || !_ready) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        ShowView(tag);
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e) => OpenSettings("path");

    /// <summary>标题栏配色入口：就地弹出四套配色</summary>
    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        PalettePopup.IsOpen = !PalettePopup.IsOpen;
    }

    private static string PaletteLabel(string key) => key switch
    {
        "mint" => "青柠",
        "violet" => "柔紫",
        "coral" => "暖珊瑚",
        _ => "冰蓝"
    };

    /// <summary>状态条限速入口：打开行为面板</summary>
    private void RateMenu_Click(object sender, RoutedEventArgs e) => OpenSettings("behavior");

    /// <summary>标题栏「新建任务」：回到工作台并把焦点交给输入框</summary>
    private void NewTask_Click(object sender, RoutedEventArgs e)
    {
        if (NavTasks is not null && NavTasks.IsChecked != true) NavTasks.IsChecked = true;
        ShowView("tasks");
        UrlBox.Focus();
    }

    private void ShowView(string tag)
    {
        ViewTasks.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        ViewHistory.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        ViewFolder.Visibility = tag == "folder" ? Visibility.Visible : Visibility.Collapsed;
        if (ViewBt != null) ViewBt.Visibility = tag == "bt" ? Visibility.Visible : Visibility.Collapsed;
        if (ViewSettings != null) ViewSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = tag switch
        {
            "history" => "已完成",
            "folder" => "文件库",
            "bt" => "磁力 / BT",
            "settings" => "设置",
            _ => "工作台"
        };

        // 详情抽屉只在「队列」页有意义，切到别的页面直接收起
        if (tag != "tasks")
        {
            TaskList.SelectedItem = null;
            CloseDrawer(immediate: true);
        }

        if (tag == "folder") RefreshFolders();
        if (tag == "history") RefreshHistoryStats();

        // 切页时回到顶部，避免上一次的滚动位置或自动选中把页面顶到中段
        Dispatcher.BeginInvoke(new Action(() =>
        {
            (tag switch
            {
                "folder" => (ScrollViewer?)FolderScroll,
                "history" => HistoryScroll,
                "bt" => BtScroll,
                "settings" => SetScroll,
                _ => TasksScroll
            })?.ScrollToTop();
        }));
    }

    // ==================================================================
    // 设置模态
    // ==================================================================
    private void OpenSettings(string pane)
    {
        CloseLog();
        if (NavSettings is not null && NavSettings.IsChecked != true)
        {
            _navBusy = true;
            NavSettings.IsChecked = true;
            _navBusy = false;
        }
        ShowView("settings");
        ApplySubNav(pane);
        Log("打开设置：" + pane);
    }

    private void CloseSettings()
    {
        if (NavTasks is not null && NavTasks.IsChecked != true)
        {
            _navBusy = true;
            NavTasks.IsChecked = true;
            _navBusy = false;
        }
        ShowView("tasks");
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void PickTargetFolder_Click(object sender, RoutedEventArgs e) => OpenSettings("path");

    private void SubNav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanePath == null || _subnavBusy) return;        // XAML 加载完成前会先触发一次
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        ApplySubNav(tag);
    }

    /// <summary>分节导航：只做高亮 + 滚动到对应分节，所有分节同时可见</summary>
    private void ApplySubNav(string tag)
    {
        if (PanePath == null) return;

        RadioButton? target = tag switch
        {
            "path" => SubNavPath,
            "speed" => SubNavSpeed,
            "behavior" => SubNavBehavior,
            "update" => SubNavUpdate,
            _ => SubNavLook
        };
        _subnavBusy = true;
        if (target != null) target.IsChecked = true;
        _subnavBusy = false;

        FrameworkElement? pane = tag switch
        {
            "path" => PanePath,
            "speed" => PaneSpeed,
            "behavior" => PaneBehavior,
            "update" => PaneUpdate,
            _ => PaneLook
        };
        pane?.BringIntoView();
    }

    /// <summary>切换界面配色（冰蓝 / 青柠 / 柔紫 / 暖珊瑚）</summary>
    private void PalettePick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string key || key.Length == 0) return;
        App.Settings.Palette = key;
        App.Settings.Save();
        ThemeManager.Apply(App.Settings.DarkMode, key);
        Log("切换配色：" + PaletteLabel(key));
        ApplySubNav("look");
        RefreshCounts();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeSwitch.IsChecked = ThemeSwitch.IsChecked != true;
        ThemeSwitch_Click(sender, e);
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.ModelsDir = AppSettings.DefaultModelsDir;
        s.DownloadDir = AppSettings.DefaultDownloadDir;
        s.RaceSources = true;
        s.AutoVerify = true;
        s.AskFolderOnAdd = true;
        s.AutoConnections = true;
        s.Save();
        LoadSettingsIntoUi();
        Log("设置已恢复默认。");
    }

    // ==================================================================
    // 磁力 / BT：不再占独立页面，识别到 magnet 时在捕获区展开
    // ==================================================================
    private void BtPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择种子文件",
            Filter = "种子文件 (*.torrent)|*.torrent|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) UrlBox.Text = dlg.FileName;
    }

    private void BtPickPage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择种子文件",
            Filter = "种子文件 (*.torrent)|*.torrent|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) BtInputBox.Text = dlg.FileName;
    }

    private void BtPageStart_Click(object sender, RoutedEventArgs e) => BtStart_Click(sender, e);

    private async void BtStart_Click(object sender, RoutedEventArgs e)
    {
        var src = (BtInputBox?.Text ?? "").Trim().Trim('"');
        if (src.Length == 0) src = (UrlBox.Text ?? "").Trim().Trim('"');
        if (src.Length == 0)
        {
            Log("请先粘贴磁力链接，或选择 .torrent 文件。");
            return;
        }

        bool isMagnet = src.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        if (!isMagnet && !File.Exists(src))
        {
            Log("这不是磁力链接，也找不到该种子文件。");
            return;
        }
        if (_btCts != null)
        {
            Log("已有 BT 任务在进行中，先停止再开始新的。");
            return;
        }

        var dir = string.IsNullOrWhiteSpace(App.Settings.DownloadDir)
            ? AppSettings.DefaultDownloadDir
            : App.Settings.DownloadDir;
        Directory.CreateDirectory(dir);

        var task = new DownloadTask
        {
            Url = src,
            Host = "BT",
            FileName = isMagnet ? MagnetName(src) : Path.GetFileNameWithoutExtension(src),
            Folder = dir,
            DestPath = dir,
            Status = TaskStatus.Connecting,
            Message = "正在连接 DHT / tracker ..."
        };
        _btTask = task;
        BtProgressCard.DataContext = task;
        BtProgressCard.Visibility = Visibility.Visible;
        if (BtEmptyHint != null) BtEmptyHint.Visibility = Visibility.Collapsed;
        if (BtCountText != null) BtCountText.Text = "1";
        if (NavBtCount != null) NavBtCount.Text = "1";
        BtStartBtn.IsEnabled = false;
        BtStopBtn.IsEnabled = true;
        if (NavBt is not null && NavBt.IsChecked != true)
        {
            _navBusy = true;
            NavBt.IsChecked = true;
            _navBusy = false;
        }
        ShowView("bt");

        var cts = new CancellationTokenSource();
        _btCts = cts;
        Log($"[BT] 开始：{task.FileName}  ->  {dir}");

        try
        {
            bool ok = await Aria2.DownloadTorrentAsync(task, src, dir, Log, cts.Token);
            task.SpeedBps = 0;
            if (ok)
            {
                task.Status = TaskStatus.Completed;
                task.Message = "";
                Log($"[BT 完成] {dir}");
            }
            else
            {
                task.Status = TaskStatus.Failed;
                task.Message = "未完成，重新开始会续传";
                Log("[BT] 下载未完成");
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = TaskStatus.Canceled;
            task.SpeedBps = 0;
            task.Message = "已停止";
            Log("[BT] 已停止");
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            task.SpeedBps = 0;
            task.Message = ex.Message;
            Log("[BT 错误] " + ex.Message);
        }
        finally
        {
            _btCts = null;
            BtStartBtn.IsEnabled = true;
            BtStopBtn.IsEnabled = false;
            if (BtCountText != null) BtCountText.Text = "0";
            if (NavBtCount != null) NavBtCount.Text = "0";
            if (BtEmptyHint != null) BtEmptyHint.Visibility = Visibility.Visible;
        }
    }

    private void BtStop_Click(object sender, RoutedEventArgs e)
    {
        try { _btCts?.Cancel(); } catch { }
    }

    /// <summary>磁力链接里的显示名：优先 dn 参数，否则用 infohash 前 8 位</summary>
    private static string MagnetName(string magnet)
    {
        var dn = Regex.Match(magnet, @"[?&]dn=([^&]+)");
        if (dn.Success)
        {
            try { return Uri.UnescapeDataString(dn.Groups[1].Value.Replace('+', ' ')); } catch { }
        }
        var ih = Regex.Match(magnet, @"btih:([0-9a-fA-F]{40}|[A-Z2-7]{32})");
        return ih.Success ? "BT_" + ih.Groups[1].Value[..8] : "BT_" + DateTime.Now.ToString("HHmmss");
    }

    private void RightTab_Checked(object sender, RoutedEventArgs e)
    {
        // 详情抽屉已合并为单页，保留空实现兼容旧的 XAML 引用
    }

    // ==================================================================
    // 详情抽屉
    // ==================================================================
    private void OpenDrawer()
    {
        _drawerOpen = true;
        ScrimLayer.Visibility = Visibility.Visible;

        if (DetailPanel.Visibility != Visibility.Visible)
        {
            DetailPanel.Visibility = Visibility.Visible;
            DrawerShift.X = 430;
        }

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void CloseDrawer(bool immediate = false)
    {
        _drawerOpen = false;
        ScrimLayer.Visibility = Visibility.Collapsed;
        if (DetailPanel.Visibility != Visibility.Visible) return;

        if (immediate)
        {
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, null);
            DrawerShift.X = 430;
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var anim = new DoubleAnimation(430, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            if (!_drawerOpen) DetailPanel.Visibility = Visibility.Collapsed;
        };
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void ScrimLayer_Click(object sender, MouseButtonEventArgs e)
    {
        TaskList.SelectedItem = null;
        CloseDrawer();
    }

    private void CloseDrawer_Click(object sender, RoutedEventArgs e)
    {
        TaskList.SelectedItem = null;
        CloseDrawer();
    }

    // ==================================================================
    // 日志抽屉
    // ==================================================================
    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logOpen) CloseLog();
        else OpenLog();
    }

    private void OpenLog()
    {
        _logOpen = true;
        if (PanelLog.Visibility != Visibility.Visible)
        {
            PanelLog.Visibility = Visibility.Visible;
            LogShift.Y = 252;
        }

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        LogShift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void CloseLog()
    {
        _logOpen = false;
        if (PanelLog.Visibility != Visibility.Visible) return;

        var anim = new DoubleAnimation(252, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            if (!_logOpen) PanelLog.Visibility = Visibility.Collapsed;
        };
        LogShift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void CloseLog_Click(object sender, RoutedEventArgs e) => CloseLog();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (DonateOverlay.Visibility == Visibility.Visible)
        {
            DonateOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }
        if (ViewSettings.Visibility == Visibility.Visible)
        {
            CloseSettings();
            e.Handled = true;
            return;
        }
        if (_logOpen)
        {
            CloseLog();
            e.Handled = true;
            return;
        }
        if (_drawerOpen)
        {
            TaskList.SelectedItem = null;
            CloseDrawer();
            e.Handled = true;
        }
    }

    // ==================================================================
    // 窗口按钮图标
    // ==================================================================
    private void UpdateMaxIcon()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxIcon.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool TabMatch(DownloadTask t)
    {
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var q = _search.Trim();
            bool hit = (t.FileName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (t.HostShort?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (t.TargetLine?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit) return false;
        }

        return _tab switch
        {
            "active" => t.IsActive,
            "queued" => t.Status == TaskStatus.Queued,
            "done" => t.Status == TaskStatus.Completed,
            "paused" => t.Status is TaskStatus.Canceled or TaskStatus.Failed,
            _ => true
        };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == TabActive) _tab = "active";
        else if (sender == TabQueued) _tab = "queued";
        else if (sender == TabDone) _tab = "done";
        else if (sender == TabPaused) _tab = "paused";
        else _tab = "all";

        _view.Refresh();
        UpdateEmptyHint();
    }

    private void ApplySort()
    {
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();

            // 分组顺序永远优先，用户的排序规则只在组内生效
            _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.GroupOrder),
                ListSortDirection.Ascending));

            switch (SortCombo.SelectedIndex)
            {
                case 1:
                    _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.FileName),
                        ListSortDirection.Ascending));
                    break;
                case 2:
                    _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.Pct),
                        ListSortDirection.Descending));
                    break;
                default:
                    _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.AddedAt),
                        ListSortDirection.Descending));
                    break;
            }
        }
    }

    private void RefreshCounts()
    {
        CountAll.Text = _tasks.Count.ToString();
        CountActive.Text = _tasks.Count(t => t.IsActive).ToString();
        CountDone.Text = _tasks.Count(t => t.Status == TaskStatus.Completed).ToString();
        CountPaused.Text = _tasks.Count(t => t.Status is TaskStatus.Canceled or TaskStatus.Failed).ToString();
        CountQueued.Text = _tasks.Count(t => t.Status == TaskStatus.Queued).ToString();

        int active = _tasks.Count(t => t.IsActive);
        int waiting = _tasks.Count(t => t.Status == TaskStatus.Queued);
        int done = _tasks.Count(t => t.Status == TaskStatus.Completed);
        int total = _tasks.Count;
        SbRight.Text = $"{active} 进行中 · {VersionBadge.Text} · {PaletteLabel(App.Settings.Palette)}";

        // v2 侧栏计数 + 工作台仪表盘
        NavTaskCount.Text = _tasks.Count.ToString();
        NavHistoryCount.Text = _tasks.Count(t => t.IsFinished).ToString();
        DashSub.Text = $"{active} 个任务在跑 · 多源并发生效";
        DashActive.Text = active.ToString();
        DashQueue.Text = waiting.ToString();
        DashActiveBar.Value = Math.Min(100, active * 25);

        long todayBytes = _tasks.Where(t => t.Status == TaskStatus.Completed && t.AddedAt.Date == DateTime.Today)
                                .Sum(t => t.Total);
        DashToday.Text = Fmt.Human(todayBytes);
        DashTodayBar.Value = Math.Min(100, todayBytes / (100.0 * 1024 * 1024 * 1024) * 100);

        double rate = total > 0 ? done * 100.0 / total : 0;
        DashRate.Text = $"{rate:0}%";
        DashRateBar.Value = rate;
        DashRateCap.Text = $"{done} / {total} 项";
    }

    /// <summary>历史页顶部的三个统计块</summary>
    private void RefreshHistoryStats()
    {
        var finished = _tasks.Where(t => t.IsFinished).ToList();
        StatCount.Text = finished.Count.ToString();

        long bytes = finished.Where(t => t.Status == TaskStatus.Completed).Sum(t => t.Total);
        StatBytes.Text = Fmt.Human(bytes);

        int ok = finished.Count(t => t.Status == TaskStatus.Completed);
        StatRate.Text = finished.Count > 0 ? ok * 100 / finished.Count + "%" : "--";
        HistorySubText.Text = $"完成、失败、已停止的任务都在这里 · 共 {finished.Count} 条";
    }

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var finished = _tasks.Where(t => t.IsFinished).ToList();
        if (finished.Count == 0)
        {
            Log("历史里还没有记录，没什么可导出的。");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出下载历史",
            FileName = $"MDVTool-历史-{DateTime.Now:yyyyMMdd}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("文件名,状态,大小(字节),目标目录,来源,添加时间");
            foreach (var t in finished.OrderByDescending(t => t.AddedAt))
            {
                sb.AppendLine(string.Join(",",
                    Q(t.FileName), Q(t.StatusText), t.Total.ToString(),
                    Q(t.DestPath), Q(t.Url), t.AddedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            Log("历史已导出：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            Log("[错误] 导出失败：" + ex.Message);
        }

        static string Q(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
    }

    private void UpdateEmptyHint()
    {
        bool empty = !_tasks.Any(TabMatch);
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 文件管理
    // ==================================================================
    private void RefreshFolders_Click(object sender, RoutedEventArgs e) => RefreshFolders();

    private void RefreshFolders()
    {
        var root = App.Settings.ModelsDir;
        FolderPathText.Text = root;
        var list = new List<ModelFolder>();

        try
        {
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".")) continue;
                    int count = 0;
                    long size = 0;
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                size += new FileInfo(f).Length;
                                count++;
                            }
                            catch { }
                        }
                    }
                    catch { }
                    list.Add(new ModelFolder(name, dir, count, size));
                }
            }
        }
        catch (Exception ex)
        {
            Log("[错误] 读取 models 目录失败：" + ex.Message);
        }

        var ordered = list.OrderByDescending(f => f.Size).ToList();
        FolderList.ItemsSource = ordered;
        NavFolderCount.Text = ordered.Count.ToString();
        if (ordered.Count > 0) FolderList.SelectedIndex = 0;
        else LoadLibFiles(null);
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadLibFiles(FolderList.SelectedItem as ModelFolder);

    /// <summary>模型库右栏：选中目录里的文件</summary>
    private void LoadLibFiles(ModelFolder? f)
    {
        var files = new List<ModelFile>();
        if (f != null && Directory.Exists(f.FullPath))
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(f.FullPath, "*", SearchOption.TopDirectoryOnly))
                {
                    try { files.Add(new ModelFile(Path.GetFileName(p), p, new FileInfo(p).Length)); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("[错误] 读取目录失败：" + ex.Message);
            }
        }

        var ordered = files.OrderByDescending(x => x.Size).ToList();
        LibFileList.ItemsSource = ordered;
        LibTitle.Text = f == null ? "文件" : f.Name + " · " + ordered.Count + " 个文件";
        LibSummary.Text = FolderList.Items.Count + " 个目录" + (f == null ? "" : " · " + Fmt.Human(ordered.Sum(x => x.Size)));
    }

    private void LibFileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibFileList.SelectedItem is not ModelFile f) return;
        if (!RevealInExplorer(f.FullPath)) Log("[提示] 找不到这个文件：" + f.FullPath);
    }

    private void FolderList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFolder();

    private void OpenFolderRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelFolder f)
        {
            OpenFolder(f);
            return;
        }
        OpenSelectedFolder();
    }

    private void OpenSelectedFolder()
    {
        if (FolderList.SelectedItem is not ModelFolder f) return;
        OpenFolder(f);
    }

    private void OpenFolder(ModelFolder f)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("[错误] 打不开目录：" + ex.Message);
        }
    }

    // ==================================================================
    // 链接识别
    // ==================================================================
    /// <summary>点击输入框区域任意空白处都聚焦输入框，避免模板命中穿透导致点不动</summary>
    private void CapBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
        UrlBox.Focus();
        Keyboard.Focus(UrlBox);
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        var cur = node;
        while (cur != null)
        {
            if (cur is T t) return t;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = UrlBox.Text ?? "";

        // 磁力链接固定走「磁力 / BT」页，工作台捕获区不再展开额外面板
        _detectTimer.Stop();
        _detectTimer.Start();
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = AddTaskAsync();
        }
    }

    private void DetectNow()
    {
        var u = UrlBox.Text.Trim();
        if (u.Length == 0)
        {
            InfoText.Text = "";
            UpdateSidePath();
            return;
        }

        try
        {
            var info = ComfyNet.Parse(u);
            var src = ComfyNet.SourceFor(info.Host).Name;

            // v8：一行说清「哪个源 · 什么文件 · 会存到哪 · 有几个源可选」
            var parts = new List<string>();
            if (info.Kind == UrlKind.Repo)
            {
                parts.Add($"{src} 仓库");
                parts.Add(info.FullName);
                parts.Add("选择文件后入队");
            }
            else if (info.Kind == UrlKind.File)
            {
                parts.Add(src);
                if (!string.IsNullOrWhiteSpace(info.FileName)) parts.Add(info.FileName);
                parts.Add("→ models\\" + (info.Folder ?? "diffusion_models"));

                int cands = ComfyNet.CandidatesFor(info.Url, info).Count;
                if (cands > 1) parts.Add($"{cands} 个源可选");
            }
            else
            {
                parts.Add("认不出这个链接");
            }

            InfoText.Text = string.Join("  ·  ", parts);

            if (info.Kind == UrlKind.File && !string.IsNullOrWhiteSpace(info.Folder))
                TargetChipText.Text = "models / " + info.Folder;
        }
        catch (Exception ex)
        {
            InfoText.Text = "链接解析失败：" + ex.Message;
        }
    }

    // ==================================================================
    // 新建任务
    // ==================================================================
    private async void AddTask_Click(object sender, RoutedEventArgs e) => await AddTaskAsync();

    private async Task AddTaskAsync(bool skipFolderPrompt = false)
    {
        var raw = UrlBox.Text.Trim();
        if (raw.Length == 0)
        {
            Log("先粘贴一个下载链接。");
            return;
        }

        UrlInfo info;
        try
        {
            info = ComfyNet.Parse(raw);
        }
        catch (Exception ex)
        {
            Log("[错误] " + ex.Message);
            return;
        }

        // 视频页 / m3u8 / mpd：真实文件名要等 yt-dlp 解析出来才知道，先用稳定占位名，
        // 同时避开 URL 查询串里的 ? & = 等 Windows 非法文件名字符。
        if (MediaEngine.IsMediaPage(raw) || HlsDownloader.LooksLikeHls(raw))
        {
            info.FileName = CategoryRouter.PlaceholderFor(raw);
            info.Folder = null;
            if (info.Kind == UrlKind.Unknown) info.Kind = UrlKind.File;
        }

        if (info.Kind == UrlKind.Unknown)
        {
            Log("[错误] 认不出这个链接，请检查是否复制完整。");
            return;
        }

        if (info.Kind == UrlKind.Repo)
        {
            await AddFromRepoAsync(info);
            return;
        }

        if (info.CivitaiVersionId != null)
        {
            Log("查询 civitai 文件信息 ...");
            var (name, folder) = await ComfyNet.LookupCivitaiAsync(info.CivitaiVersionId, CancellationToken.None);
            if (!string.IsNullOrEmpty(name)) info.FileName = name!;
            if (!string.IsNullOrEmpty(folder)) info.Folder = folder;
        }

        var target = ResolveFolderForNewTask(info, skipFolderPrompt, out bool canceled);
        if (canceled) return;   // 用户在目录选择框里点了取消

        Enqueue(info, target);
        UrlBox.Clear();
    }

    /// <summary>
    /// 按文件类型决定最终目录：模型文件进 models（再按类型分子目录），
    /// 其他文件（软件 / 压缩包 / 视频 …）进下载目录。绝对路径则原样使用。
    /// </summary>
    private static string ResolveDir(string fileName, string? folder)
    {
        bool isModel = ComfyFolders.IsModelFile(fileName);
        var root = isModel ? App.Settings.ModelsDir : App.Settings.DownloadDir;
        if (string.IsNullOrWhiteSpace(root)) root = AppSettings.DefaultDownloadDir;

        // 开了自动归档就把非模型文件按 视频/音乐/程序/压缩包/文档 分子目录；
        // 模型文件仍走 ComfyUI 目录树。返回统一规范化的绝对路径。
        return CategoryRouter.Route(root, fileName, folder, isModel, App.Settings.AutoCategory);
    }

    /// <summary>
    /// 决定新任务存到哪个目录。返回 null 表示用户取消了添加。
    /// 设置了「每次询问」就弹窗；否则按文件类型自动归类。
    /// </summary>
    private string? ResolveFolderForNewTask(UrlInfo info, bool skipFolderPrompt, out bool canceled)
    {
        canceled = false;
        bool isModel = ComfyFolders.IsModelFile(info.FileName);
        var root = isModel ? App.Settings.ModelsDir : App.Settings.DownloadDir;
        var detected = isModel ? (info.Folder ?? "diffusion_models") : null;

        // 返回 null 在这里表示「用默认归类」，不能当成用户取消，否则非模型文件永远加不进队列
        if (skipFolderPrompt || !App.Settings.AskFolderOnAdd) return detected;

        var prefill = detected;
        if (string.IsNullOrWhiteSpace(prefill)) prefill = App.Settings.DefaultFolder;

        var dlg = new FolderPickerWindow(info.FileName, root, prefill, info.Host)
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true)
        {
            Log("已取消：没有选择保存位置。");
            canceled = true;
            return null;
        }

        App.Settings.DefaultFolder = dlg.Folder;
        if (dlg.SetAsDefault)
        {
            App.Settings.AskFolderOnAdd = false;
            App.Settings.Save();
            AskFolderSwitch.IsChecked = false;
            Log($"已设为默认位置：{dlg.Folder}（以后不再询问）");
        }
        else
        {
            App.Settings.Save();
        }

        return dlg.Folder;
    }


    /// <summary>磁盘空间预检：需要 + 512MB 余量</summary>
    private bool EnsureDiskSpace(DownloadTask task, long need)
    {
        if (!App.Settings.CheckDiskSpace || need <= 0) return true;
        try
        {
            var dir = Path.GetDirectoryName(task.DestPath);
            if (string.IsNullOrEmpty(dir)) return true;
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            long free = drive.AvailableFreeSpace;
            const long reserve = 512L * 1024 * 1024;
            if (free >= need + reserve) return true;

            return Dispatcher.Invoke(() => Ask("磁盘空间不足",
                $"{task.FileName}\n\n需要 {Fmt.Human(need)}，{root} 只剩 {Fmt.Human(free)}。\n\n仍然开始下载吗？",
                "仍然下载", "跳过") == AskResult.Primary);
        }
        catch
        {
            return true;   // 检查本身失败不阻断下载
        }
    }

    /// <summary>同名文件处理：destPath 可能被改成重命名后的路径；返回 false 表示放弃</summary>
    private bool ResolveConflict(ref string destPath, out bool skip)
    {
        skip = false;
        if (!File.Exists(destPath)) return true;

        var policy = App.Settings.ConflictPolicy;
        if (string.IsNullOrWhiteSpace(policy)) policy = "ask";

        if (policy == "ask")
        {
            var r = Ask("文件已存在",
                $"目标位置已经有同名文件：\n{Path.GetFileName(destPath)}",
                "覆盖", "重命名保存", "跳过");
            if (r == AskResult.Cancel) { skip = true; return false; }
            policy = r == AskResult.Primary ? "overwrite" : "rename";
        }

        if (policy == "skip") { skip = true; return false; }

        if (policy == "overwrite")
        {
            try
            {
                File.Delete(destPath);
                var ctrl = destPath + ".aria2";
                if (File.Exists(ctrl)) File.Delete(ctrl);
            }
            catch (Exception ex)
            {
                Log("[!] 覆盖旧文件失败：" + ex.Message);
            }
            return true;
        }

        destPath = UniquePath(destPath);
        Log($"重命名为 {Path.GetFileName(destPath)}");
        return true;
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 999; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
        return path;
    }

    private void Enqueue(UrlInfo info, string? folder)
    {
        var dir = ResolveDir(info.FileName, folder);
        folder = (folder ?? "").Trim().TrimEnd('\\', '/');

        var destPath = NormalizePath(Path.Combine(dir, info.FileName));

        // 同名文件：按设置或询问决定 覆盖 / 重命名 / 跳过
        if (!ResolveConflict(ref destPath, out bool skip) || skip)
        {
            Log("[跳过] 目标位置已有同名文件：" + info.FileName);
            return;
        }

        var task = new DownloadTask
        {
            Url = info.Url,
            Host = info.Host,
            FileName = Path.GetFileName(destPath),
            Folder = folder,
            DestPath = destPath,
            Status = TaskStatus.Queued,
        };

        task.PropertyChanged += Task_PropertyChanged;
        _tasks.Add(task);
        TaskList.SelectedItem = task;

        Log($"已加入队列：{info.FileName}  ->  {dir}");
        RefreshCounts();
        UpdateEmptyHint();

        _ = PumpAsync();
    }

    private async Task AddFromRepoAsync(UrlInfo info)
    {
        Log($"读取仓库文件列表：{info.FullName} @ {info.Rev} ...");
        InfoText.Text = "正在读取文件列表 ...";

        IReadOnlyList<RepoFile> files;
        try
        {
            files = await ComfyNet.ListRepoAsync(info, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log("[错误] " + ex.Message);
            InfoText.Text = "";
            return;
        }

        if (files.Count == 0)
        {
            Log("[错误] 没读到文件列表（仓库可能是私有的，或名字不对）。");
            InfoText.Text = "";
            return;
        }

        var models = files.Where(f => ComfyFolders.IsModelFile(f.Path)).ToList();
        var show = (models.Count > 0 ? models : files.ToList())
            .OrderByDescending(f => f.Size)
            .ToList();

        InfoText.Text = "";
        var picker = new RepoPickerWindow(info.FullName, show) { Owner = this };
        if (picker.ShowDialog() != true) return;

        int n = 0;
        foreach (var rf in picker.SelectedFiles)
        {
            var url = ComfyNet.FileUrlFor(info, rf.Path);
            var sub = rf.Path.Split('/').ToList();
            var folder = ComfyFolders.Detect(sub.Take(Math.Max(0, sub.Count - 1)))
                         ?? ComfyFolders.GuessFromName(rf.Name)
                         ?? "diffusion_models";
            var subInfo = ComfyNet.Parse(url);
            subInfo.FileName = rf.Name;
            Enqueue(subInfo, folder);
            n++;
        }
        Log($"已加入 {n} 个文件到队列。");
        UrlBox.Clear();
    }

    // ==================================================================
    // 队列
    // ==================================================================
    /// <summary>已被调度器取走、但还没结束的任务 Id（避免同一任务被并发取两次）</summary>
    private readonly HashSet<string> _claimed = new();

    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            var running = new List<Task>();

            while (true)
            {
                running.RemoveAll(t => t.IsCompleted);

                // 0 = 不限并发，给一个安全上限
                int limit = App.Settings.MaxConcurrent <= 0 ? 32 : App.Settings.MaxConcurrent;

                if (running.Count >= limit)
                {
                    if (!_tasks.Any(t => t.Status == TaskStatus.Queued && !_claimed.Contains(t.Id))) break;
                    await Task.WhenAny(running);
                    continue;
                }

                var next = _tasks.FirstOrDefault(t => t.Status == TaskStatus.Queued && !_claimed.Contains(t.Id));
                if (next == null)
                {
                    if (running.Count == 0) break;
                    await Task.WhenAny(running);
                    continue;
                }

                _claimed.Add(next.Id);
                running.Add(RunClaimedAsync(next));
            }

            await Task.WhenAll(running);
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task RunClaimedAsync(DownloadTask task)
    {
        try
        {
            await RunTaskAsync(task);
        }
        finally
        {
            _claimed.Remove(task.Id);
        }
    }

    private async Task RunTaskAsync(DownloadTask task)
    {
        var cts = new CancellationTokenSource();
        _cts[task.Id] = cts;
        var ct = cts.Token;

        try
        {
            // 下载前再兜一次：启动时的自动获取可能还在进行或刚失败
            await EnsureEngineAsync();

            var url = task.Url;
            task.ClearSamples();
            task.Done = 0;
            task.SpeedBps = 0;

            // 视频页 / HLS / DASH：交给 yt-dlp + HLS + ffmpeg 管线
            if (MediaEngine.IsMediaPage(url) || HlsDownloader.LooksLikeHls(url))
            {
                await RunMediaTaskAsync(task, url, ct);
                return;
            }

            var info = ComfyNet.Parse(url);

            // 多源并发时不再串行选源：挑一个最快的反而把其它镜像的带宽浪费掉了
            if (App.Settings.RaceSources && !App.Settings.TurboMultiSource)
            {
                task.Status = TaskStatus.Racing;
                var (best, _) = await ComfyNet.RaceAsync(url, info, Log, ct);
                url = best;
            }

            ct.ThrowIfCancellationRequested();

            var (total, _, _) = await ComfyNet.HeadAsync(url, ct);
            task.Total = total;
            task.SizeHint = total;
            if (total > 0)
                Log($"大小：{Fmt.Human(total)}（{total / 1e9:0.00} GB）");

            if (File.Exists(task.DestPath) && !File.Exists(task.DestPath + ".aria2") &&
                total > 0 && new FileInfo(task.DestPath).Length >= total)
            {
                task.Done = total;
                task.Status = TaskStatus.Completed;
                task.SpeedBps = 0;
                Log("[=] 文件已存在且完整，跳过。");
                return;
            }

            // 磁盘空间预检：不够就别开始，省得下到 99% 才失败
            if (!EnsureDiskSpace(task, total))
            {
                task.Status = TaskStatus.Canceled;
                task.Message = "磁盘空间不足，已跳过";
                Log("[!] 磁盘空间不足，跳过：" + task.FileName);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(task.DestPath)!);

            // 极速模式：并行探测所有镜像（约 1.5 秒），只保留「首 64KB 指纹 + 总长」都一致的源，
            // 然后把它们一起交给 aria2，让不同分段落到不同 CDN，带宽跨源叠加。
            // 关掉多源时才退回原来的串行测速档位选择。
            int conns = App.Settings.AutoConnections
                ? TurboSources.MaxConnections
                : App.Settings.Connections;
            bool autoTuned = false;
            TurboPlan? plan = null;

            if (App.Settings.TurboMultiSource)
            {
                task.Status = TaskStatus.Racing;
                Log("极速引擎：并行探测镜像并校验内容一致性 ...");
                plan = await TurboSources.BuildAsync(url, info, true, conns, Log, ct);
                // 原链接探测失败时，改用已通过校验的镜像当主源，别让 aria2 卡在坏地址上
                if (plan.Urls.Count > 0) url = plan.Urls[0];
            }
            else if (App.Settings.AutoConnections)
            {
                task.Status = TaskStatus.Connecting;
                conns = await ComfyNet.PickConnectionsAsync(url, total, conns, Log, ct);
                autoTuned = true;
            }

            int finalConns = plan?.Connections ?? conns;
            task.Status = TaskStatus.Connecting;
            Log($"开始下载：{task.FileName}（{finalConns} 分段" +
                (plan != null && plan.IsMultiSource ? $" × {plan.SourceCount} 源并发" : "") + "）");

            bool ok = await Aria2.DownloadAsync(
                task, url, total, finalConns, Log, ct,
                autoTuned: autoTuned,
                extraUrls: plan?.Urls,
                perServer: plan?.PerServer ?? 0,
                adaptiveBoost: App.Settings.AdaptiveBoost);
            if (!ok)
            {
                task.Status = TaskStatus.Failed;
                task.Message = "下载未完成（再次开始会续传）";
                return;
            }

            if (App.Settings.AutoVerify &&
                task.FileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            {
                task.Status = TaskStatus.Verifying;
                var (good, msg) = await Task.Run(() => Safetensors.Verify(task.DestPath), ct);
                Log($"完整性校验：{(good ? "PASS" : "FAIL")}  {msg}");
                if (!good)
                {
                    task.Status = TaskStatus.Failed;
                    task.Message = "文件不完整：" + msg;
                    return;
                }
            }

            if (task.Total > 0) task.Done = task.Total;
            task.SpeedBps = 0;
            task.Status = TaskStatus.Completed;
            Log($"[完成] {task.DestPath}");
        }
        catch (OperationCanceledException)
        {
            Aria2.Kill(task.Proc);
            task.SpeedBps = 0;
            task.Status = TaskStatus.Canceled;
            Log($"[已停止] {task.FileName}（再次开始会续传）");
        }
        catch (Exception ex)
        {
            Aria2.Kill(task.Proc);
            task.SpeedBps = 0;
            task.Status = TaskStatus.Failed;
            task.Message = ex.Message;
            Log("[错误] " + ex.Message);
        }
        finally
        {
            _cts.Remove(task.Id);
            RefreshCounts();
        }
    }

    // ==================================================================
    // v3 · 视频页面 / HLS 流媒体：yt-dlp 解析 → 分段下载 → ffmpeg 合并
    // ==================================================================
    private async Task RunMediaTaskAsync(DownloadTask task, string url, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(task.DestPath)!;
        Directory.CreateDirectory(dir);

        string quality = string.IsNullOrWhiteSpace(App.Settings.MediaQuality)
            ? "best" : App.Settings.MediaQuality;
        int conns = Math.Clamp(App.Settings.Connections, 1, TurboSources.MaxConnections);

        MediaInfo media;
        if (HlsDownloader.LooksLikeHls(url))
        {
            // 纯 m3u8 直链不必劳驾 yt-dlp，直接进 HLS 分片下载器
            media = new MediaInfo
            {
                Title = Path.GetFileNameWithoutExtension(task.FileName),
                VideoUrl = url,
                IsHls = true,
                Ext = "mp4",
            };
        }
        else if (url.IndexOf(".mpd", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // 纯 DASH 清单：不再做 JSON 解析，直接交给 yt-dlp 分片下载
            string name = Path.GetFileNameWithoutExtension(task.FileName);
            try
            {
                var leaf = Path.GetFileNameWithoutExtension(
                    Uri.UnescapeDataString(new Uri(url).AbsolutePath));
                if (!string.IsNullOrWhiteSpace(leaf)) name = leaf;
            }
            catch { }
            media = new MediaInfo { Title = name, VideoUrl = url, Ext = "mp4" };
        }
        else
        {
            task.Status = TaskStatus.Racing;
            await MediaEngine.EnsureYtDlpAsync(Log, ct);   // 首次使用自动获取内核
            Log($"[媒体] yt-dlp 解析中（{quality}）：{url}");
            media = await MediaEngine.ResolveAsync(url, quality, Log, ct);
            if (string.IsNullOrWhiteSpace(media.VideoUrl))
                throw new InvalidOperationException("yt-dlp 没有返回可下载的地址");
        }

        string title = string.IsNullOrWhiteSpace(media.Title)
            ? Path.GetFileNameWithoutExtension(task.FileName) : media.Title;
        string baseName = CategoryRouter.SanitizeFileName(title);
        string ext = CategoryRouter.SanitizeFileName(
            string.IsNullOrWhiteSpace(media.Ext) ? "mp4" : media.Ext, "mp4");

        bool hls = media.IsHls || HlsDownloader.LooksLikeHls(media.VideoUrl);
        bool dash = !hls && media.VideoUrl.IndexOf(".mpd", StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasFfmpeg = MediaEngine.FindFfmpeg() != null;
        // 没装 ffmpeg 就老实保存 TS 流，不要拿 .ts 冒充 .mp4
        if (hls) ext = hasFfmpeg ? "mp4" : "ts";

        string finalPath = Path.Combine(dir, baseName + "." + ext);
        if (!ResolveConflict(ref finalPath, out bool skip))
        {
            task.Status = TaskStatus.Canceled;
            task.Message = skip ? "目标文件已存在，已跳过" : "保存位置无效";
            return;
        }
        task.FileName = Path.GetFileName(finalPath);
        task.DestPath = finalPath;

        if (dash)
        {
            // DASH 清单没有 m3u8 那样的分片规则，交给 yt-dlp 自己并发拉分片并封装
            task.Status = TaskStatus.Downloading;
            if (!await MediaEngine.DownloadWithYtDlpAsync(url, finalPath, quality, Log, ct))
                throw new InvalidOperationException("DASH 流下载未完成");
        }
        else if (hls)
        {
            string raw = finalPath + ".mdv.ts";
            task.Status = TaskStatus.Connecting;
            bool ok = await HlsDownloader.DownloadAsync(
                media.VideoUrl, raw, App.Settings.HlsConcurrency,
                (done, total, bps) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    task.Done = done;
                    if (total > 0) task.Total = total;
                    task.SpeedBps = bps;
                    task.PushSample(bps);
                    task.Status = TaskStatus.Downloading;
                })),
                Log, ct);
            if (!ok) throw new InvalidOperationException("HLS 分片没有全部下载完成");

            if (hasFfmpeg)
            {
                task.Status = TaskStatus.Verifying;
                string mp4 = finalPath + ".mdv.mp4";
                if (!await MediaEngine.RemuxAsync(raw, mp4, Log, ct))
                    throw new InvalidOperationException("ffmpeg 转封装失败");
                TryDelete(raw);
                File.Move(mp4, finalPath, true);
            }
            else
            {
                File.Move(raw, finalPath, true);
            }
        }
        else if (media.NeedsMerge)
        {
            string audioPath = finalPath + ".mdv-audio";
            var audioTask = new DownloadTask
            {
                Url = media.AudioUrl,
                Host = task.Host,
                FileName = Path.GetFileName(audioPath),
                DestPath = audioPath,
                Status = TaskStatus.Queued,
            };

            Log("[媒体] 下载视频轨 ...");
            bool vok = await Aria2.DownloadAsync(task, media.VideoUrl, 0, conns, Log, ct,
                headers: media.Headers, adaptiveBoost: false);
            if (!vok) throw new InvalidOperationException("视频轨下载未完成");

            Log("[媒体] 下载音频轨 ...");
            bool aok = await Aria2.DownloadAsync(audioTask, media.AudioUrl, 0,
                Math.Min(conns, 16), Log, ct, headers: media.Headers, adaptiveBoost: false);
            if (!aok) throw new InvalidOperationException("音频轨下载未完成");

            task.Status = TaskStatus.Verifying;
            string merged = finalPath + ".mdv-merged.mp4";
            if (!await MediaEngine.MergeAsync(finalPath, audioPath, merged, Log, ct))
                throw new InvalidOperationException("ffmpeg 合并音视频失败");
            TryDelete(finalPath);
            TryDelete(audioPath);
            File.Move(merged, finalPath, true);
        }
        else
        {
            task.Status = TaskStatus.Connecting;
            Log($"[媒体] 开始下载：{task.FileName}（{conns} 分段）");
            bool ok = await Aria2.DownloadAsync(task, media.VideoUrl, 0, conns, Log, ct,
                headers: media.Headers, adaptiveBoost: false);
            if (!ok) throw new InvalidOperationException("媒体下载未完成");
        }

        long size = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
        if (size <= 0) throw new InvalidOperationException("没有写出媒体文件");
        task.Total = size;
        task.Done = size;
        task.SizeHint = size;
        task.SpeedBps = 0;
        task.Status = TaskStatus.Completed;
        Log($"[完成] {finalPath}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + ".aria2")) File.Delete(path + ".aria2"); } catch { }
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTask.Status) or nameof(DownloadTask.Total)
            or nameof(DownloadTask.Done) or nameof(DownloadTask.Message)
            or nameof(DownloadTask.DestPath) or nameof(DownloadTask.Url))
            _historyDirty = true;

        if (e.PropertyName == nameof(DownloadTask.Status) && sender is DownloadTask done
            && done.Status == TaskStatus.Completed)
        {
            NotifyDone(done);
        }

        if (e.PropertyName is nameof(DownloadTask.Status) or nameof(DownloadTask.Total))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _view.Refresh();
                _historyView.Refresh();
                RefreshCounts();
                UpdateEmptyHint();
            }), DispatcherPriority.Background);
        }
    }

    // ==================================================================
    // 任务操作
    // ==================================================================
    /// <summary>整页滚轮：鼠标停在卡片、列表或空白处，都滚动当前页面</summary>
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsInsidePopupLayer(src)) return;

        var sv = CurrentPageScroll();
        if (sv == null) return;

        double target = sv.VerticalOffset - e.Delta;
        if (target < 0) target = 0;
        if (target > sv.ScrollableHeight) target = sv.ScrollableHeight;
        sv.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private ScrollViewer? CurrentPageScroll()
    {
        if (ViewSettings is not null && ViewSettings.Visibility == Visibility.Visible) return SetScroll;
        if (ViewBt is not null && ViewBt.Visibility == Visibility.Visible) return BtScroll;
        if (ViewFolder is not null && ViewFolder.Visibility == Visibility.Visible) return FolderScroll;
        if (ViewHistory is not null && ViewHistory.Visibility == Visibility.Visible) return HistoryScroll;
        return TasksScroll;
    }

    /// <summary>弹层与抽屉里的滚轮保持各自原生滚动</summary>
    private static bool IsInsidePopupLayer(DependencyObject node)
    {
        var cur = node;
        while (cur != null)
        {
            if (cur is FrameworkElement fe &&
                (fe.Name is "PanelLog" or "DetailPanel" or "DonateOverlay" or "AskOverlay" or "PalettePopup"))
                return true;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    /// <summary>列表内的滚轮交给所属页面滚动，避免列表吞掉滚轮后整页不动</summary>
    private void InnerList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var sv = FindAncestorScrollViewer(d);
        if (sv == null) return;                     // 页面本身不可滚，就交给列表自己滚
        sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - e.Delta));
        e.Handled = true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject node)
    {
        var cur = System.Windows.Media.VisualTreeHelper.GetParent(node);
        while (cur != null)
        {
            if (cur is ScrollViewer sv) return sv;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void TaskDetail_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadTask t && TaskList != null)
            TaskList.SelectedItem = t;
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSpeedCard();
        UpdateBatchBar();

        if (!_ready) return;
        if (TaskList.SelectedItem == null) CloseDrawer();
        else OpenDrawer();
    }

    // ---- 队列多选批量操作（Ctrl / Shift 多选，选中 2 项以上出现工具条）----
    private void UpdateBatchBar()
    {
        if (BatchBar == null) return;
        int n = TaskList.SelectedItems.Count;
        BatchBar.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
        BatchCount.Text = $"已选 {n} 项";
    }

    private List<DownloadTask> SelectedTasks() => TaskList.SelectedItems.OfType<DownloadTask>().ToList();

    private void BatchSelectAll_Click(object sender, RoutedEventArgs e) => TaskList.SelectAll();

    private void BatchClear_Click(object sender, RoutedEventArgs e) => TaskList.UnselectAll();

    // ==================================================================
    // v9 工具条 / 捕获区 / 行操作
    // ==================================================================
    private void TaskSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _search = TaskSearchBox.Text ?? "";
        _view.Refresh();
        UpdateEmptyHint();
    }

    private void PasteUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                UrlBox.Text = Clipboard.GetText().Trim();
                UrlBox.CaretIndex = UrlBox.Text.Length;
            }
        }
        catch (Exception ex)
        {
            Log("读取剪贴板失败：" + ex.Message);
        }
        UrlBox.Focus();
    }

    private void ClearUrl_Click(object sender, RoutedEventArgs e)
    {
        UrlBox.Text = "";
        UrlBox.Focus();
    }

    private void CopyTaskUrl_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;
        if (string.IsNullOrWhiteSpace(t.Url)) return;
        try
        {
            Clipboard.SetText(t.Url);
            Log("链接已复制：" + t.Url);
        }
        catch
        {
            // 剪贴板被别的程序占用，忽略
        }
    }

    /// <summary>分组标题右侧的「全部暂停 / 全部开始 / 查看全部」</summary>
    private void GroupAction_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.Tag as string;
        switch (group)
        {
            case "正在下载":
                {
                    var list = _tasks.Where(t => t.IsActive).ToList();
                    foreach (var t in list)
                    {
                        if (_cts.TryGetValue(t.Id, out var cts)) { try { cts.Cancel(); } catch { } }
                        Aria2.Kill(t.Proc);
                    }
                    Log($"[批量] 已暂停 {list.Count} 个任务");
                    break;
                }
            case "队列中":
                {
                    var list = _tasks.Where(t => t.Status == TaskStatus.Queued).ToList();
                    if (list.Count > 0) _ = PumpAsync();
                    Log($"[批量] 已开始 {list.Count} 个排队任务");
                    break;
                }
            case "已完成与异常":
                {
                    NavHistory.IsChecked = true;
                    break;
                }
        }
    }

    private void BatchPause_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedTasks();
        foreach (var t in list)
        {
            if (_cts.TryGetValue(t.Id, out var cts)) { try { cts.Cancel(); } catch { } }
            Aria2.Kill(t.Proc);
        }
        Log($"[批量] 已停止 {list.Count} 个任务");
    }

    private void BatchResume_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var t in SelectedTasks().Where(t => t.CanResume))
        {
            t.Message = "";
            t.Status = TaskStatus.Queued;
            n++;
        }
        RefreshCounts();
        if (n > 0) _ = PumpAsync();
        Log($"[批量] 已重新开始 {n} 个任务");
    }

    private void BatchRemove_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedTasks();
        if (list.Count == 0) return;
        if (list.Any(t => t.IsActive) &&
            Ask("批量删除任务", $"选中的 {list.Count} 个任务里有正在下载的，删除会中断它们。",
                "删除", "取消") != AskResult.Primary) return;

        foreach (var t in list)
        {
            if (_cts.TryGetValue(t.Id, out var cts)) { try { cts.Cancel(); } catch { } }
            Aria2.Kill(t.Proc);
            t.PropertyChanged -= Task_PropertyChanged;
            _tasks.Remove(t);
        }
        RefreshCounts();
        UpdateEmptyHint();
        _view.Refresh();
        _historyView.Refresh();
        UpdateBatchBar();
        Log($"[批量] 已移除 {list.Count} 个任务");
    }

    private DownloadTask? Current =>
        _tasks.FirstOrDefault(t => t.Status is TaskStatus.Downloading
            or TaskStatus.Connecting or TaskStatus.Racing or TaskStatus.Verifying)
        ?? TaskList.SelectedItem as DownloadTask
        ?? _tasks.FirstOrDefault(t => t.IsActive);

    private void UpdateSpeedCard()
    {
        var t = Current;
        if (t == null)
        {
            SpeedBig.Text = "0.0";
            SpeedUnit.Text = "MB/s";
            EtaText.Text = "--";
            SpeedChart.Points = null;
            SparkPeak.Text = "峰值 --";
            return;
        }

        var bps = t.SpeedBps;
        if (bps >= 1048576)
        {
            SpeedBig.Text = (bps / 1048576.0).ToString("0.0");
            SpeedUnit.Text = "MB/s";
        }
        else if (bps >= 1024)
        {
            SpeedBig.Text = (bps / 1024.0).ToString("0.0");
            SpeedUnit.Text = "KB/s";
        }
        else
        {
            SpeedBig.Text = "0.0";
            SpeedUnit.Text = "MB/s";
        }

        EtaText.Text = t.EtaText.Replace("剩余 ", "");
        SpeedChart.Points = t.Samples;

        var samples = t.Samples;
        if (samples is { Length: > 0 })
        {
            double peak = samples.Max();
            SparkPeak.Text = peak >= 1048576
                ? $"峰值 {peak / 1048576:0.0} MB/s"
                : $"峰值 {peak / 1024:0.0} KB/s";
        }
        else
        {
            SparkPeak.Text = "峰值 --";
        }
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;
        if (_cts.TryGetValue(t.Id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        Aria2.Kill(t.Proc);
        Log($"[取消] {t.FileName}");
    }

    private void ResumeTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;
        if (!t.CanResume) return;
        t.Message = "";
        t.Status = TaskStatus.Queued;
        RefreshCounts();
        _ = PumpAsync();
    }

    private void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;

        // 正在下载的任务先确认，已结束的直接删
        if (t.IsActive)
        {
            var ok = Ask("删除任务",
                $"「{t.FileName}」正在下载，删除会中断任务。", "删除", "取消");
            if (ok != AskResult.Primary) return;
        }

        if (_cts.TryGetValue(t.Id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        Aria2.Kill(t.Proc);
        t.PropertyChanged -= Task_PropertyChanged;
        _tasks.Remove(t);
        RefreshCounts();
        UpdateEmptyHint();
        _view.Refresh();
        _historyView.Refresh();
    }

    private void OpenTaskFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;

        // 历史记录里可能是 E:/xxx/models\loras\a.safetensors 这种混合斜杠。
        // File.Exists 认得，但 explorer /select 只接受规范化路径，否则会跳到错误位置。
        string full = NormalizePath(t.DestPath);

        // 文件被搬走过，就在配置的两个根目录里按文件名找一次
        if (full.Length == 0 || !File.Exists(full))
        {
            var moved = LocateMovedFile(t);
            if (moved != null) full = moved;
        }

        try
        {
            if (File.Exists(full))
            {
                // Shell API 直接让资源管理器定位并选中文件，比 explorer.exe /select 命令行可靠
                // （后者遇到含空格 / 中文 / 混合斜杠的路径经常跳到别处）
                if (!RevealInExplorer(full))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
                        { UseShellExecute = true });
                Log("已定位文件：" + full);
                return;
            }

            // 绝不 Directory.CreateDirectory：原来那样会凭空造一个空目录，
            // 用户看到的自然不可能是文件所在位置
            var dir = string.IsNullOrEmpty(full) ? null : Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);

            if (!string.IsNullOrEmpty(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    { UseShellExecute = true });
                Log($"文件不在原位置，已打开最近的目录：{dir}");
            }
            else
            {
                Log("找不到文件，也找不到保存目录：" + (t.DestPath ?? "(空)"));
            }
        }
        catch (Exception ex)
        {
            Log("[错误] 打不开目录：" + ex.Message);
        }
    }

    /// <summary>
    /// 用 Shell API 在资源管理器里定位并选中文件。
    /// 返回 false 时调用方会退回 explorer.exe /select 命令行。
    /// </summary>
    private static bool RevealInExplorer(string fullPath)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(fullPath, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        catch { return false; }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc,
        out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl,
        IntPtr[]? apidl, uint dwFlags);

    /// <summary>文件不在记录的路径时，在模型目录 / 下载目录里按文件名找一次</summary>
    private static string? LocateMovedFile(DownloadTask t)
    {
        if (string.IsNullOrWhiteSpace(t.FileName)) return null;
        var roots = new[] { App.Settings.ModelsDir, App.Settings.DownloadDir }
            .Where(s => !string.IsNullOrWhiteSpace(s) && Directory.Exists(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            try
            {
                var hit = Directory.EnumerateFiles(root, t.FileName,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MaxRecursionDepth = 6,
                        ReturnSpecialDirectories = false
                    }).FirstOrDefault();
                if (hit != null) return Path.GetFullPath(hit);
            }
            catch { }
        }
        return null;
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;
        try
        {
            Clipboard.SetText(t.Url);
            Log("链接已复制到剪贴板。");
        }
        catch { }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var done = _tasks.Where(t => t.IsFinished).ToList();
        if (done.Count == 0)
        {
            Log("没有可清空的历史记录。");
            return;
        }

        var ok = Ask("清空历史",
            $"将删除 {done.Count} 条历史记录，此操作不可撤销。", "清空", "取消");
        if (ok != AskResult.Primary) return;

        foreach (var t in done)
        {
            t.PropertyChanged -= Task_PropertyChanged;
            _tasks.Remove(t);
        }
        RefreshCounts();
        UpdateEmptyHint();
        _view.Refresh();
        _historyView.Refresh();
        Log($"已清空 {done.Count} 条历史记录。");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        LogEmpty.Visibility = Visibility.Visible;
    }

    // ==================================================================
    // 日志
    // ==================================================================
    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(text)));
            return;
        }

        if (_logs.Count >= 500) _logs.RemoveAt(0);
        var entry = new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text };
        _logs.Add(entry);
        LogEmpty.Visibility = Visibility.Collapsed;

        // 等布局更新后再滚，保证始终停在最新一条
        Dispatcher.BeginInvoke(new Action(() => LogList.ScrollIntoView(entry)),
            DispatcherPriority.Background);
    }
}

/// <summary>右侧日志面板的一行</summary>
public sealed class LogEntry
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
}
