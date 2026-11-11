using System;
using System.Globalization;
using System.Windows.Data;

namespace MDVTool;

/// <summary>
/// 长路径换行用：在每个分隔符后插入零宽空格，
/// 让 TextBlock 优先在「\」处断行，而不是把文件名从中间劈开。
/// </summary>
public sealed class PathWrapConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\u200B").Replace("/", "/\u200B");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
