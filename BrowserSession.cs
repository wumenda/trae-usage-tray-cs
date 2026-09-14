using System.Text.Json;

namespace TraeUsageTray;

/// <summary>
/// 带登录编排与空闲退出的内置浏览器会话（线程安全）。
/// 火山等站点的登录 cookie 是 session cookie（浏览器关闭即丢），因此：
/// 登录成功后持久化 cookie 到文件；Chrome 每次启动后先注入持久化 cookie 再校验登录态，
/// 实现"重启免登录"，空闲自动退出省内存也就可行了。
/// </summary>
public sealed class BrowserSession
{
    public ChromeManager Mgr { get; }
    public string Tag { get; }
    public string LoginUrl { get; }
    private readonly Func<BrowserSession, Task<bool>> _check;
    private readonly string? _cookieFile;
    private readonly int _idleStopSec;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastUsed = DateTime.UtcNow;
    private int _busy;

    public BrowserSession(int port, string profileDir, string tag, string loginUrl,
        Func<BrowserSession, Task<bool>> check, string? chromePath = null,
        int idleStopSec = 600, string? cookieFile = null)
    {
        Mgr = new ChromeManager(port, profileDir, tag, chromePath);
        Tag = tag;
        LoginUrl = loginUrl;
        _check = check;
        _idleStopSec = idleStopSec;
        _cookieFile = cookieFile;
    }

    private string Origin => new Uri(LoginUrl).GetLeftPart(UriPartial.Authority);

    // ---- cookie 持久化 / 注入 ----
    private async Task PersistCookiesAsync()
    {
        if (_cookieFile is null) return;
        try
        {
            var cookies = await Mgr.CookiesAsync([Origin + "/"]);
            var json = JsonSerializer.Serialize(cookies);
            await File.WriteAllTextAsync(_cookieFile, json);
            Log.Info("[{0}] 已持久化 {1} 个 cookie", Tag, cookies.Length);
        }
        catch (Exception e)
        {
            Log.Warn("[{0}] cookie 持久化失败: {1}", Tag, e.Message);
        }
    }

    private async Task<bool> InjectCookiesAsync()
    {
        if (_cookieFile is null || !File.Exists(_cookieFile)) return false;
        List<JsonElement> cookies;
        try
        {
            var json = await File.ReadAllTextAsync(_cookieFile);
            cookies = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
        }
        catch (Exception e)
        {
            Log.Warn("[{0}] cookie 文件读取失败: {1}", Tag, e.Message);
            return false;
        }
        var ok = await Mgr.SetCookiesAsync(cookies);
        Log.Info("[{0}] 注入 {1}/{2} 个持久化 cookie", Tag, ok, cookies.Count);
        return ok > 0;
    }

    private void Touch() => _lastUsed = DateTime.UtcNow;

    public async Task<JsonElement> CallApiAsync(string apiUrl, object body)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            Touch();
            return await Mgr.CallApiAsync(apiUrl, body);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    public async Task<JsonElement[]> CookiesAsync(string[] urls)
    {
        Touch();
        return await Mgr.CookiesAsync(urls);
    }

    private async Task<bool> LoggedInAsync()
    {
        try
        {
            return await _check(this);
        }
        catch (Exception e)
        {
            Log.Info("[{0}] 登录检测异常: {1}", Tag, e.Message);
            return false;
        }
    }

    /// <summary>保证浏览器可用且已登录；必要时自动弹出登录窗口</summary>
    public async Task EnsureReadyAsync(Action<string>? notify = null)
    {
        await _lock.WaitAsync();
        try
        {
            Touch();
            if (Mgr.IsUp())
            {
                if (await LoggedInAsync()) return;
                Log.Info("[{0}] 登录态已失效，重启内置浏览器", Tag);
                Mgr.Stop();
            }

            await Mgr.StartAsync(headless: true, LoginUrl);
            await Mgr.OpenOrNavigateAsync(LoginUrl);
            if (await LoggedInAsync())
            {
                Log.Info("[{0}] 已有登录态", Tag);
                await PersistCookiesAsync(); // 顺手存盘，保证重启免登录
                return;
            }

            // 尝试注入持久化 cookie（session cookie 重启后会丢）。
            // 刚重启的浏览器页面/WAF 可能尚未就绪导致校验瞬时失败，重试几次再下结论
            if (await InjectCookiesAsync())
            {
                for (var i = 0; i < 3; i++)
                {
                    if (await LoggedInAsync())
                    {
                        Log.Info("[{0}] 持久化 cookie 注入成功", Tag);
                        return;
                    }
                    Log.Info("[{0}] 注入后校验未通过({1}/3)，稍后重试", Tag, i + 1);
                    await Task.Delay(3000);
                }
            }

            // 需要用户登录
            Mgr.Stop();
            notify?.Invoke($"[{Tag}] 需要登录，请在弹出的浏览器窗口中登录");
            Log.Info("[{0}] 弹出有头浏览器等待用户登录...", Tag);
            await Mgr.StartAsync(headless: false, LoginUrl);
            await Mgr.OpenOrNavigateAsync(LoginUrl);
            var deadline = DateTime.UtcNow.AddMinutes(10);
            var ok = false;
            while (DateTime.UtcNow < deadline && !_cts.IsCancellationRequested)
            {
                if (await LoggedInAsync()) { ok = true; break; }
                await Task.Delay(3000);
            }
            if (!ok)
            {
                Mgr.Stop();
                throw new CdpException($"[{Tag}] 等待登录超时");
            }
            await PersistCookiesAsync(); // 关键：session cookie 关浏览器即丢，先存盘

            Mgr.Stop(); // 先关掉 headful 实例，否则 StartAsync 会复用可见窗口
            await Mgr.StartAsync(headless: true, LoginUrl);
            await Mgr.OpenOrNavigateAsync(LoginUrl);
            await InjectCookiesAsync();
            var recheck = false;
            for (var i = 0; i < 3 && !recheck; i++)
            {
                recheck = await LoggedInAsync();
                if (!recheck)
                {
                    Log.Info("[{0}] 登录后校验未通过({1}/3)，稍后重试", Tag, i + 1);
                    await Task.Delay(3000);
                }
            }
            if (!recheck)
            {
                Mgr.Stop();
                throw new CdpException($"[{Tag}] 登录后校验失败");
            }
            Log.Info("[{0}] 登录完成，headless 就绪", Tag);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ForceReloginAsync(Action<string>? notify = null)
    {
        await _lock.WaitAsync();
        try
        {
            Mgr.Stop();
            if (_cookieFile is not null && File.Exists(_cookieFile))
            {
                try { File.Delete(_cookieFile); } catch { } // 强制重登须丢弃持久化 cookie
            }
            // 必须清除浏览器 profile 里的登录态，否则 EnsureReady 检测
            // "已有登录态"会静默通过，表现为"点了没反应"
            try
            {
                Directory.Delete(Mgr.Profile, recursive: true);
                Directory.CreateDirectory(Mgr.Profile);
                Log.Info("[{0}] 已清除浏览器 profile，准备重新登录", Tag);
            }
            catch (Exception e)
            {
                Log.Warn("[{0}] profile 清理失败(可能被占用): {1}", Tag, e.Message);
            }
        }
        finally
        {
            _lock.Release();
        }
        await EnsureReadyAsync(notify);
    }

    public void StartIdleWatcher()
    {
        if (_idleStopSec > 0)
            _ = Task.Run(IdleLoopAsync);
    }

    private async Task IdleLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(30_000);
            if (_cts.IsCancellationRequested) break;
            if (_busy > 0 || !Mgr.IsUp()) continue;
            if (!await _lock.WaitAsync(0)) continue; // 登录流程进行中则跳过
            try
            {
                if ((DateTime.UtcNow - _lastUsed).TotalSeconds > _idleStopSec && Mgr.IsUp())
                {
                    Log.Info("[{0}] 空闲超过 {1}s，关闭内置浏览器（下次刷新自动拉起）", Tag, _idleStopSec);
                    Mgr.Stop();
                }
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    public async Task ShutdownAsync()
    {
        _cts.Cancel();
        await _lock.WaitAsync();
        try { Mgr.Stop(); }
        finally { _lock.Release(); }
    }
}
