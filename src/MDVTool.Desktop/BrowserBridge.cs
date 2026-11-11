using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MDVTool;

/// <summary>
/// 浏览器扩展与本地程序之间的 HTTP 桥。
/// 只监听回环地址 127.0.0.1:18765，供扩展把嗅探到的链接送进来。
/// </summary>
public static class BrowserBridge
{
    public const int Port = 18765;

    private static readonly string Prefix = $"http://127.0.0.1:{Port}/";
    private static readonly object Gate = new();

    private static HttpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static int _running; // 0 = 未运行，1 = 运行中

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    public static void Start(Action<string> onUrl, Action<string> log)
    {
        // 包一层，避免调用方回调抛异常时把监听循环带崩
        Action<string> safeLog = m => { try { log(m); } catch { } };
        Action<string> safeOnUrl = u =>
        {
            try { onUrl(u); }
            catch (Exception ex) { safeLog($"下载回调执行失败：{ex.Message}"); }
        };

        lock (Gate)
        {
            if (Volatile.Read(ref _running) == 1) return; // 重复调用直接返回，保证幂等

            HttpListener? listener = null;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(Prefix);
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                safeLog($"浏览器嗅探桥启动失败：端口 {Port} 可能被占用或没有监听权限（{ex.Message}）");
                SafeClose(listener);
                return;
            }
            catch (Exception ex)
            {
                safeLog($"浏览器嗅探桥启动失败：{ex.Message}");
                SafeClose(listener);
                return;
            }

            var cts = new CancellationTokenSource();
            _listener = listener;
            _cts = cts;
            Volatile.Write(ref _running, 1);
            safeLog($"浏览器嗅探桥已启动：{Prefix}");

            _ = Task.Run(() => ListenLoopAsync(listener, safeOnUrl, safeLog, cts.Token));
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            var cts = _cts;
            var listener = _listener;
            _cts = null;
            _listener = null;
            Volatile.Write(ref _running, 0);

            try { cts?.Cancel(); } catch { }
            SafeClose(listener);
            try { cts?.Dispose(); } catch { }
        }
    }

    private static async Task ListenLoopAsync(
        HttpListener listener, Action<string> onUrl, Action<string> log, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }   // Stop() 关闭了监听
                catch (ObjectDisposedException) { break; } // listener 已释放
                catch (OperationCanceledException) { break; }

                try
                {
                    await HandleContextAsync(ctx, onUrl, log).ConfigureAwait(false);
                }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    log($"浏览器嗅探桥处理请求出错：{ex.Message}");
                }
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log($"浏览器嗅探桥监听循环异常：{ex.Message}");
        }
        finally
        {
            // 非主动 Stop 导致的退出，复位状态，保证之后还能再次 Start
            if (!ct.IsCancellationRequested) MarkStopped(listener, log);
        }
    }

    private static async Task HandleContextAsync(HttpListenerContext ctx, Action<string> onUrl, Action<string> log)
    {
        var req = ctx.Request;
        string method = req.HttpMethod ?? "GET";
        string path = req.Url?.AbsolutePath ?? "/";

        string? token = req.Headers["X-MDV-Token"];
        if (!string.IsNullOrEmpty(token)) log($"浏览器嗅探桥收到令牌：{token}");

        try
        {
            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx, 200, "{}").ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/ping")
            {
                await WriteJsonAsync(ctx, 200, "{\"ok\":true,\"app\":\"MDV Tool\"}").ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path == "/add")
            {
                string body;
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                string? url = null, title = null, pageUrl = null;
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        url = ReadString(root, "url");
                        title = ReadString(root, "title");
                        pageUrl = ReadString(root, "pageUrl");
                    }
                }
                catch (JsonException)
                {
                    await WriteJsonAsync(ctx, 400, "{\"ok\":false,\"error\":\"invalid json\"}").ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    await WriteJsonAsync(ctx, 400, "{\"ok\":false,\"error\":\"missing url\"}").ConfigureAwait(false);
                    return;
                }

                log($"浏览器嗅探收到链接：{url}"
                    + (string.IsNullOrEmpty(title) ? "" : $"，标题：{title}")
                    + (string.IsNullOrEmpty(pageUrl) ? "" : $"，来源页：{pageUrl}"));

                onUrl(url); // 后台线程执行，UI 线程调度由调用方负责
                await WriteJsonAsync(ctx, 200, "{\"ok\":true}").ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(ctx, 404, "{\"ok\":false,\"error\":\"not found\"}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"浏览器嗅探桥处理请求失败：{ex.Message}");
            try { await WriteJsonAsync(ctx, 500, "{\"ok\":false,\"error\":\"internal error\"}").ConfigureAwait(false); }
            catch { }
        }
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, string json)
    {
        var resp = ctx.Response;
        try
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            TryAddHeader(resp, "Access-Control-Allow-Origin", "*");
            TryAddHeader(resp, "Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            TryAddHeader(resp, "Access-Control-Allow-Headers", "Content-Type");
            // Chrome 从扩展访问回环地址时可能附带私有网络预检头
            if (!string.IsNullOrEmpty(ctx.Request.Headers["Access-Control-Request-Private-Network"]))
                TryAddHeader(resp, "Access-Control-Allow-Private-Network", "true");

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { resp.Close(); } catch { }
        }
    }

    private static void TryAddHeader(HttpListenerResponse resp, string name, string value)
    {
        try { resp.Headers.Add(name, value); } catch { }
    }

    private static void SafeClose(HttpListener? listener)
    {
        if (listener is null) return;
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
    }

    private static void MarkStopped(HttpListener listener, Action<string> log)
    {
        bool changed = false;
        lock (Gate)
        {
            if (ReferenceEquals(_listener, listener))
            {
                SafeClose(listener);
                _listener = null;
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                Volatile.Write(ref _running, 0);
                changed = true;
            }
        }
        if (changed) log("浏览器嗅探桥已停止");
    }
}
