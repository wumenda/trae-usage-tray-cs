using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TraeUsageTray;

public sealed class CdpException : Exception
{
    public CdpException(string msg) : base(msg) { }
}

public sealed class NotLoginException : Exception
{
    public NotLoginException(string msg) : base(msg) { }
}

/// <summary>单个 Chrome tab 的 CDP WebSocket 连接（线程安全）</summary>
public sealed class CDPTab : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _id;

    private CDPTab(ClientWebSocket ws) => _ws = ws;

    public static async Task<CDPTab> ConnectAsync(string wsUrl, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        return new CDPTab(ws);
    }

    private static async Task<string> ReadMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new CdpException("CDP websocket 已关闭");
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    /// <summary>发送命令并等待匹配 id 的响应（忽略事件消息）</summary>
    public async Task<JsonElement> CmdAsync(string method, object? pars = null, int timeoutSec = 25,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = ++_id;
            var req = JsonSerializer.Serialize(new { id, method, @params = pars });
            var timeout = TimeSpan.FromSeconds(timeoutSec);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await _ws.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, cts.Token);

            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new CdpException($"CDP {method} 超时");
                using var msgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                msgCts.CancelAfter(remaining);
                var text = await ReadMessageAsync(_ws, msgCts.Token);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl) || idEl.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var err))
                    throw new CdpException($"CDP {method}: {err.ToString()?[..200]}");
                return root.GetProperty("result").Clone();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonElement?> EvaluateAsync(string expression, int timeoutSec = 25,
        CancellationToken ct = default)
    {
        var result = await CmdAsync("Runtime.evaluate", new
        {
            expression,
            awaitPromise = true,
            returnByValue = true,
        }, timeoutSec, ct);

        var r = result.GetProperty("result");
        if (r.TryGetProperty("subtype", out var sub) && sub.GetString() == "error")
            throw new CdpException("JS error: " +
                (r.TryGetProperty("description", out var d) ? d.GetString() : null));
        return r.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null
            && v.ValueKind != JsonValueKind.Undefined
            ? v.Clone()
            : null;
    }

    public void Dispose()
    {
        try { _ws.Dispose(); } catch { }
        _gate.Dispose();
    }
}

/// <summary>管理一个独立 profile 的 Chrome 实例（不影响日常浏览器）</summary>
public sealed class ChromeManager
{
    public int Port { get; }
    public string Profile { get; }
    public string Tag { get; }
    private readonly string _chromePath;
    private Process? _proc;
    private CDPTab? _tab;

    public ChromeManager(int port, string profileDir, string tag, string? chromePath = null)
    {
        Port = port;
        Tag = tag;
        Profile = string.IsNullOrWhiteSpace(profileDir)
            ? Path.Combine(AppPaths.AppDir, "chrome-profile-" + tag)
            : profileDir;
        Directory.CreateDirectory(Profile);
        _chromePath = chromePath ?? FindChrome();
    }

    private static string FindChrome()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] cands =
        [
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
        ];
        foreach (var p in cands)
            if (File.Exists(p)) return p;
        throw new CdpException("未找到 Chrome/Edge，请在 config.json 的 chrome_path 指定");
    }

    public bool IsUp()
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = c.GetAsync($"http://127.0.0.1:{Port}/json/version").GetAwaiter().GetResult();
            return resp.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    public async Task StartAsync(bool headless, string url)
    {
        if (IsUp()) return;
        var args = new List<string>
        {
            $"--remote-debugging-port={Port}",
            $"--user-data-dir={Profile}",
            "--no-first-run",
            "--no-default-browser-check",
            "--window-size=1280,900",
            url,
        };
        if (headless) args.Insert(0, "--headless=new");

        Log.Info("[{0}] 启动 Chrome: headless={1}", Tag, headless);
        _proc = Process.Start(new ProcessStartInfo(_chromePath) { Arguments = string.Join(' ', args) });
        if (!await WaitUpAsync(30))
        {
            Log.Warn("[{0}] Chrome 端口未就绪，清理僵尸进程后重试", Tag);
            Stop();
            _proc = Process.Start(new ProcessStartInfo(_chromePath) { Arguments = string.Join(' ', args) });
            if (!await WaitUpAsync(30)) throw new CdpException("Chrome 调试端口未就绪");
        }
        Log.Info("[{0}] Chrome 已启动 (port={1} headless={2})", Tag, Port, headless);
    }

    private async Task<bool> WaitUpAsync(int seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            if (IsUp()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    private int? ListenerPid()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && parts[3] == "LISTENING" && parts[1].EndsWith(":" + Port)
                    && int.TryParse(parts[4], out var pid))
                    return pid;
            }
        }
        catch { }
        return null;
    }

    private void KillProfileProcesses()
    {
        // 兜底清理：按 profile 目录名过滤浏览器进程，绝不会碰到日常浏览器
        var marker = Path.GetFileName(Profile.TrimEnd('\\', '/'));
        var ps = "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe' or Name='msedge.exe'\" | " +
                 $"Where-Object {{ $_.CommandLine -and $_.CommandLine.Contains('{marker}') }} | " +
                 "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
        try
        {
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(20000);
        }
        catch { }
    }

    public void Stop()
    {
        _tab?.Dispose();
        _tab = null;
        var pid = _proc?.Id ?? ListenerPid();
        if (pid.HasValue)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("taskkill",
                    $"/F /T /PID {pid.Value}") { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(10000);
            }
            catch { }
        }
        KillProfileProcesses();
        _proc = null;
        for (var i = 0; i < 10 && IsUp(); i++) Thread.Sleep(500);
    }

    private JsonElement[] ListTabs()
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var json = c.GetStringAsync($"http://127.0.0.1:{Port}/json/list").GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Where(t => t.TryGetProperty("type", out var ty) && ty.GetString() == "page"
                        && t.TryGetProperty("webSocketDebuggerUrl", out _))
            .Select(t => t.Clone())
            .ToArray();
    }

    private JsonElement OpenTab(string url)
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var json = c.PutAsync($"http://127.0.0.1:{Port}/json/new?{Uri.EscapeDataString(url)}", null)
            .GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out _))
            throw new CdpException($"[{Tag}] 新建 tab 失败");
        return doc.RootElement.Clone();
    }

    private async Task WaitLoadedAsync(CDPTab tab, int seconds = 40)
    {
        for (var i = 0; i < seconds; i++)
        {
            try
            {
                if (await tab.EvaluateAsync("document.readyState", 6) is JsonElement el
                    && el.GetString() == "complete") break;
            }
            catch { }
            await Task.Delay(1000);
        }
        await Task.Delay(1000); // SPA 余量
    }

    /// <summary>复用/新建 tab 并确保停在 url 的 origin，等待加载完成</summary>
    public async Task OpenOrNavigateAsync(string url)
    {
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
        _tab?.Dispose();
        _tab = null;
        var tabs = ListTabs();
        CDPTab tab;
        if (tabs.Length > 0)
        {
            tab = await CDPTab.ConnectAsync(tabs[0].GetProperty("webSocketDebuggerUrl").GetString()!);
            string href = "";
            try
            {
                var v = await tab.EvaluateAsync("location.href", 8);
                href = v?.GetString() ?? "";
            }
            catch { }
            if (!href.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
            {
                try { await tab.CmdAsync("Page.navigate", new { url }, 15); }
                catch (CdpException) { /* 页面跳转瞬间可能报错，交给等待逻辑 */ }
            }
        }
        else
        {
            var t = OpenTab(url);
            tab = await CDPTab.ConnectAsync(t.GetProperty("webSocketDebuggerUrl").GetString()!);
        }
        _tab = tab;
        await WaitLoadedAsync(_tab);
    }

    /// <summary>在页面上下文 fetch 同源 API（自动带 csrf/cookie）→ {status, json, text}</summary>
    public async Task<JsonElement> CallApiAsync(string apiUrl, object body)
    {
        var origin = new Uri(apiUrl).GetLeftPart(UriPartial.Authority);
        if (_tab is null)
        {
            await OpenOrNavigateAsync(origin + "/");
        }
        else
        {
            string href = "";
            try { href = (await _tab.EvaluateAsync("location.href", 8))?.GetString() ?? ""; }
            catch { }
            if (!href.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                await OpenOrNavigateAsync(origin + "/");
        }

        var bodyJson = JsonSerializer.Serialize(body);
        var js = "(async () => {" +
                 " const csrf = (document.cookie.match(/csrfToken=([^;]+)/) || [])[1] || '';" +
                 " const h = {'content-type':'application/json', 'x-csrf-token': csrf};" +
                 " try {" +
                 $"   const r = await fetch('{apiUrl}', {{method:'POST', headers:h, body: JSON.stringify({bodyJson})}});" +
                 "   const t = await r.text();" +
                 "   let j=null; try{j=JSON.parse(t);}catch(e){}" +
                 "   return {status:r.status, json:j, text:t.slice(0,200)};" +
                 " } catch(e) { return {status:0, json:null, text:String(e)}; }" +
                 "})()";
        var res = await _tab!.EvaluateAsync(js) ?? throw new CdpException($"[{Tag}] evaluate 返回异常");
        return res;
    }

    /// <summary>返回指定 URL 范围的浏览器 cookie（含 HttpOnly）</summary>
    public async Task<JsonElement[]> CookiesAsync(string[] urls)
    {
        if (_tab is null) throw new CdpException($"[{Tag}] 浏览器未打开");
        var res = await _tab.CmdAsync("Network.getCookies", new { urls });
        return res.GetProperty("cookies").EnumerateArray().Select(c => c.Clone()).ToArray();
    }

    /// <summary>注入 cookie（Network.setCookie），返回成功条数</summary>
    public async Task<int> SetCookiesAsync(IEnumerable<JsonElement> cookies)
    {
        if (_tab is null) throw new CdpException($"[{Tag}] 浏览器未打开");
        var ok = 0;
        foreach (var c in cookies)
        {
            var pars = new Dictionary<string, object?>();
            foreach (var key in new[] { "name", "value", "domain", "path", "secure", "httpOnly" })
                if (c.TryGetProperty(key, out var v)) pars[key] = v.Clone();
            if (!pars.ContainsKey("name")) continue;
            if (c.TryGetProperty("expires", out var exp) && exp.ValueKind == JsonValueKind.Number
                && exp.GetDouble() > 0) pars["expires"] = exp.GetDouble();
            if (c.TryGetProperty("sameSite", out var ss))
            {
                var s = ss.GetString();
                if (s is "Strict" or "Lax" or "None") pars["sameSite"] = s;
            }
            try
            {
                var res = await _tab.CmdAsync("Network.setCookie", pars, 10);
                if (res.TryGetProperty("success", out var s2) && s2.GetBoolean()) ok++;
            }
            catch { /* 单个 cookie 失败不致命 */ }
        }
        return ok;
    }
}
