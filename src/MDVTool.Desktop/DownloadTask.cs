using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MDVTool;

public enum TaskStatus
{
    Queued,
    Racing,
    Connecting,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Canceled
}

/// <summary>一个下载任务（绑定到界面）</summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    private static readonly Dispatcher Ui =
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    private static readonly Brush BSub = Freeze("#8B93A6");
    private static readonly Brush BAccent = Freeze("#3B82F6");
    private static readonly Brush BGreen = Freeze("#22C55E");
    private static readonly Brush BRed = Freeze("#F05252");
    private static readonly Brush BAmber = Freeze("#F59E0B");
    private static readonly Brush BDim = Freeze("#5A6274");

    private static Brush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private long _total;
    private long _done;
    private double _speed;
    private TaskStatus _status = TaskStatus.Queued;
    private string _message = "";
    private readonly List<double> _samples = new();

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTime AddedAt { get; } = DateTime.Now;
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string DestPath { get; set; } = "";
    public long SizeHint { get; set; }
    public Process? Proc { get; set; }

    // ------------------------------------------------------------------
    private void OnUi(Action act)
    {
        if (Ui.CheckAccess()) act();
        else Ui.Invoke(act);
    }

    public long Total
    {
        get => _total;
        set
        {
            if (_total == value) return;
            OnUi(() =>
            {
                _total = value;
                Raise(nameof(Total));
                Raise(nameof(TotalText));
                Raise(nameof(Pct));
                Raise(nameof(PctText));
                Raise(nameof(EtaText));
                Raise(nameof(SubLine));
                Raise(nameof(MetaLine));
            });
        }
    }

    public long Done
    {
        get => _done;
        set
        {
            if (_done == value) return;
            OnUi(() =>
            {
                _done = value;
                Raise(nameof(Done));
                Raise(nameof(DoneText));
                Raise(nameof(Pct));
                Raise(nameof(PctText));
                Raise(nameof(EtaText));
                Raise(nameof(SubLine));
            });
        }
    }

    public double SpeedBps
    {
        get => _speed;
        set
        {
            OnUi(() =>
            {
                _speed = value;
                Raise(nameof(SpeedBps));
                Raise(nameof(SpeedText));
                Raise(nameof(SpeedCell));
                Raise(nameof(EtaText));
            });
        }
    }

    public TaskStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            OnUi(() =>
            {
                _status = value;
                Raise(nameof(Status));
                Raise(nameof(StatusText));
                Raise(nameof(StatusKind));
                Raise(nameof(SpeedCell));
                Raise(nameof(StatusBrush));
                Raise(nameof(IsActive));
                Raise(nameof(IsFinished));
                Raise(nameof(CanResume));
                Raise(nameof(IsRunning));
                Raise(nameof(QueueGroup));
                Raise(nameof(GroupOrder));
                Raise(nameof(ProgressBrush));
            });
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value) return;
            OnUi(() =>
            {
                _message = value;
                Raise(nameof(Message));
            });
        }
    }

    // ------------------------------------------------------------------
    /// <summary>不再预截断：交给 XAML 的 TextTrimming 按实际可用宽度省略，
    /// 宽窗口能显示全名；完整文件名始终可通过 ToolTip 查看</summary>
    public string DisplayName => FileName;
    public string BadgeText => Badge.Text(FileName);
    /// <summary>任务行左侧的类型图标（矢量，替代原来的 SFT 字母色块）</summary>
    public string BadgeIcon => Badge.Icon(FileName);
    public Brush BadgeBrush => Badge.BrushFor(FileName);
    public string TotalText => Total > 0 ? Fmt.Human(Total) : "未知";
    public string DoneText => Fmt.Human(Done);
    public string SpeedText => Fmt.Speed(SpeedBps);

    /// <summary>大卡片右侧的大号速度数字（单位单独用小字排版）</summary>
    public string SpeedNumber => (SpeedBps / 1048576.0).ToString("0.0");

    /// <summary>列表速度列：非进行中的任务显示占位符，避免一排 0.0 B/s 噪声</summary>
    public string SpeedCell => Status is TaskStatus.Downloading or TaskStatus.Racing
        or TaskStatus.Connecting or TaskStatus.Verifying
        ? SpeedText
        : "—";
    public string EtaText => SpeedBps > 1 && Total > 0 ? "剩余 " + Fmt.Hms((Total - Done) / SpeedBps) : "--";
    public double Pct => Total > 0 ? Math.Clamp(Done * 100.0 / Total, 0, 100) : 0;
    public string PctText => Total > 0 ? $"{Pct:0}%" : "--";
    public string SizeLine => Total > 0 ? $"{DoneText} / {TotalText}" : DoneText;

    /// <summary>列表行第二行：大小 · 百分比（合并后不再单独占两列）</summary>
    public string SubLine => Total > 0 ? $"{DoneText} / {TotalText} · {PctText}" : DoneText;

    /// <summary>列表行第二行（v3）：大小 · 目标目录 · 来源，一行说清，不再重复百分比</summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string> { Total > 0 ? TotalText : "大小未知" };
            var tgt = TargetLine;
            if (!string.IsNullOrWhiteSpace(tgt)) parts.Add(tgt);
            var host = HostShort;
            if (!string.IsNullOrWhiteSpace(host)) parts.Add(host);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>来源短名：huggingface.co → HuggingFace</summary>
    public string HostShort => Host switch
    {
        null or "" => "",
        "BT" => "BitTorrent",
        var h when h.Contains("huggingface", StringComparison.OrdinalIgnoreCase) => "HuggingFace",
        var h when h.Contains("hf-mirror", StringComparison.OrdinalIgnoreCase) => "hf-mirror",
        var h when h.Contains("civitai", StringComparison.OrdinalIgnoreCase) => "Civitai",
        var h when h.Contains("modelscope", StringComparison.OrdinalIgnoreCase) => "ModelScope",
        var h => h
    };

    /// <summary>历史页按天分组的小标题</summary>
    public string DayLabel
    {
        get
        {
            var d = AddedAt.Date;
            var today = DateTime.Today;
            if (d == today) return "今天 · " + d.ToString("M 月 d 日");
            if (d == today.AddDays(-1)) return "昨天 · " + d.ToString("M 月 d 日");
            return d.ToString("yyyy 年 M 月 d 日");
        }
    }

    /// <summary>状态语义，给徽章/进度条配色用：done / active / fail / idle</summary>
    public string StatusKind => Status switch
    {
        TaskStatus.Completed => "done",
        TaskStatus.Failed => "fail",
        TaskStatus.Downloading or TaskStatus.Racing or TaskStatus.Connecting or TaskStatus.Verifying => "active",
        _ => "idle"
    };

    /// <summary>来源显示：本地补录的任务没有 URL，不要留一个空卡片</summary>
    public string SourceText => string.IsNullOrWhiteSpace(Url) ? "本地文件（无下载来源）" : Url;

    /// <summary>「打开文件夹」按钮的提示：带完整路径，方便核对打开的位置</summary>
    public string FolderHint => string.IsNullOrWhiteSpace(DestPath)
        ? "打开所在文件夹"
        : "打开所在文件夹\n" + DestPath;

    /// <summary>保存位置显示成最后两级目录，比如 models\loras、Downloads</summary>
    public string TargetLine
    {
        get
        {
            var dir = string.IsNullOrEmpty(DestPath) ? "" : Path.GetDirectoryName(DestPath);
            if (string.IsNullOrEmpty(dir)) return Folder;
            var parts = dir.TrimEnd('\\', '/').Split(new[] { '\\', '/' },
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? parts[^2] + "\\" + parts[^1]
                : (parts.Length == 1 ? parts[0] : dir);
        }
    }

    public string StatusText => Status switch
    {
        TaskStatus.Queued => "排队中",
        TaskStatus.Racing => "测速选源",
        TaskStatus.Connecting => "连接中",
        TaskStatus.Downloading => "下载中",
        TaskStatus.Verifying => "校验中",
        TaskStatus.Completed => "已完成",
        TaskStatus.Failed => "失败",
        TaskStatus.Canceled => "已停止",
        _ => ""
    };

    public Brush StatusBrush => Status switch
    {
        TaskStatus.Downloading => BAccent,
        TaskStatus.Completed => BGreen,
        TaskStatus.Failed => BRed,
        TaskStatus.Racing or TaskStatus.Connecting or TaskStatus.Verifying => BAmber,
        TaskStatus.Canceled => BDim,
        _ => BSub
    };

    public Brush ProgressBrush => Status switch
    {
        TaskStatus.Completed => BGreen,
        TaskStatus.Failed => BRed,
        TaskStatus.Canceled => BDim,
        _ => BAccent
    };

    public bool IsActive => Status is TaskStatus.Racing or TaskStatus.Connecting
        or TaskStatus.Downloading or TaskStatus.Verifying or TaskStatus.Queued;

    public bool IsFinished => Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Canceled;

    public bool CanResume => Status is TaskStatus.Failed or TaskStatus.Canceled;

    // ------------------------------------------------------------------
    // v8 队列分组：进行中 / 等待 / 已完成 / 已停止
    // ------------------------------------------------------------------

    /// <summary>真正在跑（不含排队），用大卡片展示</summary>
    public bool IsRunning => Status is TaskStatus.Racing or TaskStatus.Connecting
        or TaskStatus.Downloading or TaskStatus.Verifying;

    /// <summary>队列分组标题</summary>
    public string QueueGroup => Status switch
    {
        TaskStatus.Racing or TaskStatus.Connecting or TaskStatus.Downloading or TaskStatus.Verifying => "正在下载",
        TaskStatus.Queued => "队列中",
        _ => "已完成与异常"
    };

    /// <summary>分组排序：正在下载 → 队列中 → 已完成与异常</summary>
    public int GroupOrder => Status switch
    {
        TaskStatus.Racing or TaskStatus.Connecting or TaskStatus.Downloading or TaskStatus.Verifying => 0,
        TaskStatus.Queued => 1,
        _ => 2
    };

    /// <summary>紧凑行用的短元信息：目录名 · 大小（只取末级目录，避免长路径占位）</summary>
    public string ShortMeta
    {
        get
        {
            var size = Total > 0 ? TotalText : "大小未知";
            var leaf = FolderLeaf;
            return string.IsNullOrWhiteSpace(leaf) ? size : $"{leaf} · {size}";
        }
    }

    /// <summary>目标目录的末级名字：models\loras → loras</summary>
    public string FolderLeaf
    {
        get
        {
            var t = TargetLine;
            var i = t.LastIndexOf('\\');
            return i >= 0 && i < t.Length - 1 ? t[(i + 1)..] : t;
        }
    }


    public double[] Samples => _samples.ToArray();

    /// <summary>速度曲线采样（约每 0.5 秒一次）</summary>
    public void PushSample(double bps)
    {
        OnUi(() =>
        {
            _samples.Add(bps);
            if (_samples.Count > 90) _samples.RemoveRange(0, _samples.Count - 90);
            Raise(nameof(Samples));
        });
    }

    public void ClearSamples()
    {
        OnUi(() =>
        {
            _samples.Clear();
            Raise(nameof(Samples));
        });
    }

    /// <summary>把界面依赖的派生属性一起刷新（文件名/目录等在构造后改过时用）</summary>
    public void RefreshAll()
    {
        OnUi(() =>
        {
            Raise(nameof(FileName));
            Raise(nameof(DisplayName));
            Raise(nameof(BadgeText));
            Raise(nameof(BadgeIcon));
            Raise(nameof(BadgeBrush));
            Raise(nameof(Folder));
            Raise(nameof(DestPath));
            Raise(nameof(TargetLine));
            Raise(nameof(Url));
            Raise(nameof(Host));
            Raise(nameof(TotalText));
            Raise(nameof(SizeLine));
            Raise(nameof(MetaLine));
            Raise(nameof(HostShort));
            Raise(nameof(DayLabel));
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
