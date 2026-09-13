using System.Net;
using System.Text;
using System.Text.Json;

namespace TraeUsageTray;

/// <summary>本地 Web 面板（liquid-glass 液态玻璃设计系统），仅监听 localhost</summary>
public sealed class WebPanel
{
    private readonly TrayApp _app;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }

    private const string CurveColors = "[\"#007aff\",\"#bf5af2\",\"#ff9f0a\",\"#28c840\",\"#ff3b30\"]";

    public WebPanel(TrayApp app, int port)
    {
        _app = app;
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(ServeLoopAsync);
        Log.Info("Web 面板已启动: {0}", Url);
    }

    public void Stop()
    {
        try { _cts.Cancel(); _listener.Stop(); } catch { }
    }

    private async Task ServeLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            switch (req.HttpMethod)
            {
                case "GET" when req.Url!.AbsolutePath is "/" or "/index.html":
                    await SendTextAsync(resp, 200, "text/html; charset=utf-8", Html());
                    break;
                case "GET" when req.Url!.AbsolutePath == "/api/state":
                    await SendJsonAsync(resp, BuildState());
                    break;
                case "POST" when req.Url!.AbsolutePath == "/api/refresh":
                    _ = Task.Run(_app.RefreshAsync);
                    await SendJsonAsync(resp, JsonSerializer.Serialize(new { ok = true }));
                    break;
                case "POST" when req.Url!.AbsolutePath == "/api/config":
                    await SaveConfigAsync(req, resp);
                    break;
                default:
                    await SendTextAsync(resp, 404, "text/plain; charset=utf-8", "not found");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warn("Web 请求处理异常: {0}", e.Message);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        return await sr.ReadToEndAsync();
    }

    private static async Task SendTextAsync(HttpListenerResponse resp, int code, string ctype, string body)
    {
        resp.StatusCode = code;
        resp.ContentType = ctype;
        resp.Headers["Cache-Control"] = "no-store";
        var bytes = Encoding.UTF8.GetBytes(body);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    private static Task SendJsonAsync(HttpListenerResponse resp, string json)
        => SendTextAsync(resp, 200, "application/json; charset=utf-8", json);

    private string BuildState()
    {
        var sources = _app.Sources.Select(s => new
        {
            name = s.Name,
            error = s.Error,
            pct = Math.Round(s.Pct, 1),
            items = s.Items.Select(it => new object[] { it.Label, it.Used, it.Quota }).ToArray(),
            resets = s.Items.Select(it =>
            {
                var cd = Fmt.Countdown(s.ResetTimes.GetValueOrDefault(it.Label));
                return cd is null ? null : $"{it.Label}额度{cd}";
            }).ToArray(),
            token = (s as TraeSource)?.TokenLine,
            history = new
            {
                label = s.HistoryLabel() ?? "",
                points = s.HistoryLabel() is { } hl
                    ? _app.History.Series(s.Name, hl, 30).Select(p => new object[] { p.Date, Math.Round(p.Pct, 2) }).ToArray()
                    : Array.Empty<object[]>(),
            },
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            last_ok = _app.LastOk?.ToString("yyyy-MM-dd HH:mm:ss"),
            interval = _app.Interval,
            threshold = _app.Threshold,
            sources,
            cookie_trae = _app.TraeCookie,
            curve_colors = CurveColors,
        });
    }

    private async Task SaveConfigAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(req));
            var cfg = AppConfig.Load();
            if (body.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number
                && iv.GetInt32() >= 5) cfg.RefreshIntervalSec = iv.GetInt32();
            if (body.TryGetProperty("threshold", out var th) && th.ValueKind == JsonValueKind.Number
                && th.GetInt32() is >= 0 and <= 100) cfg.NotifyThresholdPct = th.GetInt32();
            if (body.TryGetProperty("cookie", out var ck) && ck.ValueKind == JsonValueKind.String)
            {
                var cookie = ck.GetString()?.Trim();
                if (!string.IsNullOrEmpty(cookie))
                    foreach (var sc in cfg.Sources.Where(s => s.Type == "trae"))
                        sc.Cookie = cookie;
            }
            cfg.Save();
            _app.ReloadFromConfig();
            await SendJsonAsync(resp, """{"ok":true}""");
        }
        catch (Exception e)
        {
            Log.Error("保存配置失败: {0}", e.Message);
            await SendJsonAsync(resp, JsonSerializer.Serialize(new { ok = false, error = e.Message }));
        }
    }

    // ---------------- 面板 HTML（liquid-glass） ----------------
    private string Html()
    {
        var html = HtmlTemplate.Html.Replace("__CURVE_COLORS__", CurveColors);
        return html;
    }
}
