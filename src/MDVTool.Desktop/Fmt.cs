using System;

namespace MDVTool;

/// <summary>数字格式化小工具</summary>
public static class Fmt
{
    public static string Human(long n)
    {
        double v = n;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        foreach (var u in units)
        {
            if (v < 1024 || u == "TB")
                return u == "B" ? $"{(long)v} B" : $"{v:0.0} {u}";
            v /= 1024.0;
        }
        return $"{v:0.0} TB";
    }

    public static string Human(double n) => Human((long)n);

    /// <summary>字节/秒 -> "13.7 MB/s"</summary>
    public static string Speed(double bps)
    {
        if (bps < 1) return "--";
        double v = bps;
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        foreach (var u in units)
        {
            if (v < 1024 || u == "GB/s")
                return $"{v:0.0} {u}";
            v /= 1024.0;
        }
        return $"{v:0.0} GB/s";
    }

    public static string Hms(double sec)
    {
        if (sec <= 0 || double.IsNaN(sec) || double.IsInfinity(sec)) return "--";
        var t = (int)Math.Min(sec, 359999);
        int h = t / 3600, m = (t % 3600) / 60, s = t % 60;
        if (h > 0) return $"{h} 小时 {m} 分";
        if (m > 0) return $"{m} 分 {s} 秒";
        return $"{s} 秒";
    }
}
