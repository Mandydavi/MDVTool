using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDVSetup
{
    /// <summary>多段并发下载：每段一个临时文件，天然支持断点续传，最后顺序拼回。</summary>
    internal static class Downloader
    {
        private sealed class Reporter
        {
            private readonly object _gate = new object();
            private readonly long _total;
            private readonly Action<long, long, double> _sink;
            private long _done;
            private long _lastDone;
            private DateTime _last = DateTime.UtcNow;

            public Reporter(long total, Action<long, long, double> sink)
            {
                _total = total;
                _sink = sink;
            }

            public void Add(long n)
            {
                lock (_gate)
                {
                    _done += n;
                    DateTime now = DateTime.UtcNow;
                    double dt = (now - _last).TotalSeconds;
                    if (dt >= 0.25)
                    {
                        _sink(_done, _total, (_done - _lastDone) / dt);
                        _lastDone = _done;
                        _last = now;
                    }
                }
            }

            public void Finish()
            {
                lock (_gate) _sink(_done, _total <= 0 ? _done : _total, 0);
            }
        }

        public static string WorkDirFor(string url)
        {
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "");
            string dir = Path.Combine(Path.GetTempPath(), "MDVSetup-" + hash.Substring(0, 10));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static async Task DownloadAsync(
            string url, string dest, CancellationToken ct,
            Action<long, long, double> progress, Action<string> log)
        {
            string work = Path.GetDirectoryName(dest);
            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxConnectionsPerServer = 16
            })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MDVSetup/1.0");

                long total = 0;
                bool ranges = false;
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Head, url))
                    using (var resp = await http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (resp.Content.Headers.ContentLength.HasValue)
                            total = resp.Content.Headers.ContentLength.Value;
                        ranges = resp.Headers.AcceptRanges.Contains("bytes");
                    }
                }
                catch (Exception ex)
                {
                    log("探测文件大小失败，按单连接下载：" + ex.Message);
                }

                int parts = ranges && total > 4L * 1024 * 1024 ? 8 : 1;
                if (parts > 1) log("分 " + parts + " 段并发下载（支持断点续传）");

                long[] starts = new long[parts];
                long[] ends = new long[parts];
                string[] paths = new string[parts];
                long per = total > 0 ? (long)Math.Ceiling(total / (double)parts) : 0;

                var reporter = new Reporter(total, progress);
                for (int i = 0; i < parts; i++)
                {
                    starts[i] = total > 0 ? i * per : 0;
                    ends[i] = total > 0 ? Math.Min(total - 1, (i + 1) * per - 1) : 0;
                    paths[i] = dest + ".part" + i;
                    if (File.Exists(paths[i])) reporter.Add(new FileInfo(paths[i]).Length);
                }

                var jobs = new List<Task>();
                for (int i = 0; i < parts; i++)
                {
                    int idx = i;
                    jobs.Add(Task.Run(async () =>
                    {
                        string part = paths[idx];
                        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                        long want = total > 0 ? ends[idx] - starts[idx] + 1 : long.MaxValue;
                        // 服务端不支持 Range 时，旧缓存不能续传，否则会把整份内容追加到半截文件后面
                        if (have > 0 && !ranges && parts == 1)
                        {
                            try { File.Delete(part); } catch { }
                            have = 0;
                        }
                        if (have >= want) return;

                        using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            if (total > 0 && parts > 1)
                                req.Headers.Range = new RangeHeaderValue(starts[idx] + have, ends[idx]);
                            else if (have > 0 && parts == 1)
                                req.Headers.Range = new RangeHeaderValue(have, null);

                            using (var resp = await http.SendAsync(
                                req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                            {
                                resp.EnsureSuccessStatusCode();
                                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var fs = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.Read))
                                {
                                    var buf = new byte[131072];
                                    while (true)
                                    {
                                        int n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                                        if (n <= 0) break;
                                        fs.Write(buf, 0, n);
                                        reporter.Add(n);
                                    }
                                }
                            }
                        }
                    }, ct));
                }

                await Task.WhenAll(jobs).ConfigureAwait(false);
                reporter.Finish();

                using (var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    for (int i = 0; i < parts; i++)
                    {
                        using (var fs = File.OpenRead(paths[i])) fs.CopyTo(outFs);
                    }
                }
                for (int i = 0; i < parts; i++)
                {
                    try { File.Delete(paths[i]); } catch { }
                }
                log("下载完成：" + new FileInfo(dest).Length + " 字节");
            }
        }

        public static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
