using System.Windows;
using System.Windows.Controls;

namespace MDVTool;

/// <summary>
/// 队列行模板选择器（v8）。
/// 正在跑的任务用大卡片（76px，24px 百分比），其余用紧凑行（44px）。
/// 三种行高是全界面层级的主要来源。
/// </summary>
public sealed class TaskRowSelector : DataTemplateSelector
{
    public DataTemplate? Running { get; set; }
    public DataTemplate? Compact { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is DownloadTask t && t.IsRunning) return Running ?? Compact;
        return Compact;
    }
}
