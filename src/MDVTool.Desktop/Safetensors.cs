using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace MDVTool;

/// <summary>safetensors 完整性自检（头部偏移 + 数据区越界）</summary>
public static class Safetensors
{
    public static (bool Ok, string Message) Verify(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            using var fs = File.OpenRead(path);
            var head = new byte[8];
            if (fs.Read(head, 0, 8) < 8) return (false, "文件太短");

            ulong n = BinaryPrimitives.ReadUInt64LittleEndian(head);
            if (n == 0 || n > 200UL * 1024 * 1024) return (false, $"header 长度异常（{n}）");
            if ((long)(8 + n) > size) return (false, "header 越界");

            var buf = new byte[(int)n];
            int read = 0;
            while (read < buf.Length)
            {
                int r = fs.Read(buf, read, buf.Length - read);
                if (r <= 0) return (false, "header 读取不完整");
                read += r;
            }

            using var doc = JsonDocument.Parse(buf);
            long worst = 0;
            int count = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "__metadata__") continue;
                if (!prop.Value.TryGetProperty("data_offsets", out var offs)) continue;
                if (offs.GetArrayLength() < 2) continue;
                count++;
                worst = Math.Max(worst, offs[1].GetInt64());
            }

            long need = (long)(8 + n) + worst;
            if (need > size)
                return (false, $"数据区越界：需要 {need} 字节，实际 {size}");

            return (true, $"{count} 个 tensor，数据区完整");
        }
        catch (Exception ex)
        {
            return (false, "解析失败：" + ex.Message);
        }
    }
}
