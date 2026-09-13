using System.Diagnostics;
using System.Text.Json;

namespace TraeUsageTray;

public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly AppConfig _cfg;
    private readonly Dictionary<string, BrowserSession> _sessions = new();
    private List<Source> _sources = [];
    private readonly History _history;
    private DateTime? _lastOk;
    private readonly Dictionary<string, double> _pctSeen = new();
    private DateTime _cfgMtime;
    private readonly WebPanel _web;
    private readonly CancellationTokenSource _stop = new();
    private Icon? _currentIcon;
    private ToolStripMenuItem? _iconMenu;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _cfg = AppConfig.Load();

        var arkPort = _cfg.CdpPort;
        _sessions["ark"] = new BrowserSession(
            arkPort,
            string.IsNullOrWhiteSpace(_cfg.ChromeProfileDir)
                ? Path.Combine(AppPaths.AppDir, "chrome-profile")
                : _cfg.ChromeProfileDir,
            tag: "方舟", loginUrl: AppPaths.ArkPage, check: ArkAFPSource.ArkCheckAsync,
            chromePath: NullIfEmpty(_cfg.ChromePath), idleStopSec: _cfg.ChromeIdleStopSec,
            cookieFile: Path.Combine(AppPaths.AppDir, "ark-cookies.json"));
        _sessions["trae"] = new BrowserSession(
            _cfg.CdpPortTrae ?? arkPort + 1,
            string.IsNullOrWhiteSpace(_cfg.ChromeProfileDirTrae)
                ? Path.Combine(AppPaths.AppDir, "trae-profile")
                : _cfg.ChromeProfileDirTrae,
            tag: "TRAE", loginUrl: AppPaths.TraeLoginUrl, check: TraeSource.TraeCheckAsync,
            chromePath: NullIfEmpty(_cfg.ChromePath), idleStopSec: _cfg.ChromeIdleStopSec,
            cookieFile: Path.Combine(AppPaths.AppDir, "trae-cookies.json"));
        foreach (var s in _sessions.Values) s.StartIdleWatcher();

        _history = new History(AppPaths.HistoryPath);
        BuildSources();
        _cfgMtime = ConfigMtime();

        _web = new WebPanel(this, _cfg.WebUiPort);
        _web.Start();

        _icon = new NotifyIcon
        {
            Icon = IconFactory.Make(0, false),
            Text = "用量监控: 加载中...",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenPanel();
        };

        _ = Task.Run(RunLoopAsync);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public List<Source> Sources => _sources;
    public History History => _history;
    public DateTime? LastOk => _lastOk;
    public int Interval => _cfg.RefreshIntervalSec;
    public int Threshold => _cfg.NotifyThresholdPct;
    public string? TraeCookie =>
        _sources.OfType<TraeSource>().FirstOrDefault()?.GetCookie();

    // ---- 源构建 ----
    private void BuildSources()
    {
        _sources = [];
        foreach (var sc in _cfg.Sources)
        {
            switch (sc.Type)
            {
                case "trae":
                    _sources.Add(new TraeSource(sc, _sessions["trae"]));
                    break;
                case "ark_afp":
                    _sources.Add(new ArkAFPSource(sc, _sessions["ark"]));
                    break;
            }
        }
    }

    // ---- 菜单 ----
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => OpenPanel());
        menu.Items.Add("刷新", null, (_, _) => _ = Task.Run(RefreshAsync));
        _iconMenu = BuildIconMenu();
        menu.Items.Add(_iconMenu);
        var showPctItem = new ToolStripMenuItem("图标显示百分比") { Checked = _cfg.IconShowPct };
        showPctItem.Click += (_, _) =>
        {
            _cfg.IconShowPct = !_cfg.IconShowPct;
            try { _cfg.Save(); }
            catch (Exception e) { Log.Warn("保存图标设置失败: {0}", e.Message); }
            showPctItem.Checked = _cfg.IconShowPct;
            UpdateUi();
        };
        menu.Items.Add(showPctItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("TRAE 用量页面", null,
            (_, _) => Process.Start(new ProcessStartInfo(AppPaths.TraeLoginUrl) { UseShellExecute = true }));
        menu.Items.Add("方舟 AFP 页面", null,
            (_, _) => Process.Start(new ProcessStartInfo(AppPaths.ArkPage) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("重新登录火山", null,
            (_, _) => Task.Run(async () =>
            {
                try
                {
                    await _sessions["ark"].ForceReloginAsync(m => Log.Info(m));
                    await RefreshAsync();
                }
                catch (Exception e) { Log.Error("重新登录火山失败: {0}", e.Message); }
            }));
        menu.Items.Add("重新登录 TRAE", null,
            (_, _) => Task.Run(async () =>
            {
                try
                {
                    await _sessions["trae"].ForceReloginAsync(m => Log.Info(m));
                    foreach (var s in _sources.OfType<TraeSource>()) await s.ReloadCookieAsync();
                    await RefreshAsync();
                }
                catch (Exception e) { Log.Error("重新登录 TRAE 失败: {0}", e.Message); }
            }));
        menu.Items.Add("重载配置", null, (_, _) => ReloadFromConfig());
        var autostart = new ToolStripMenuItem("开机自启")
        {
            Checked = Program.AutoStartEnabled(),
        };
        autostart.Click += (_, _) =>
        {
            try
            {
                Program.SetAutoStart(autostart.Checked = !autostart.Checked);
            }
            catch (Exception e)
            {
                Log.Error("设置开机自启失败: {0}", e.Message);
                autostart.Checked = Program.AutoStartEnabled();
            }
        };
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _stop.Cancel();
            _web.Stop();
            foreach (var s in _sessions.Values) _ = s.ShutdownAsync();
            _icon.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    // ---- 图标圆环指标选择 ----
    private ToolStripMenuItem BuildIconMenu()
    {
        var root = new ToolStripMenuItem("图标用量");
        // 展开时动态填充：启动时 Items 尚未抓取到，静态构建会只剩"自动"一项
        root.DropDownOpening += (_, _) => PopulateIconMenu(root);
        PopulateIconMenu(root);
        return root;
    }

    private void PopulateIconMenu(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        AddIconMenuItem(root, "自动（最大值）", "");
        foreach (var s in _sources)
            foreach (var (label, _, _) in s.Items)
                AddIconMenuItem(root, $"{s.Name} · {label}", $"{s.Name}:{label}");
        RefreshIconMenuChecks(root);
    }

    private void AddIconMenuItem(ToolStripMenuItem root, string text, string key)
    {
        var mi = new ToolStripMenuItem(text) { Tag = key };
        mi.Click += (_, _) =>
        {
            _cfg.IconMetric = key;
            try { _cfg.Save(); }
            catch (Exception e) { Log.Warn("保存图标设置失败: {0}", e.Message); }
            if (_iconMenu is not null) RefreshIconMenuChecks(_iconMenu);
            UpdateUi();
        };
        root.DropDownItems.Add(mi);
    }

    private void RefreshIconMenuChecks(ToolStripMenuItem root)
    {
        foreach (var mi in root.DropDownItems.OfType<ToolStripMenuItem>())
            mi.Checked = (mi.Tag as string) == _cfg.IconMetric;
    }

    /// <summary>图标圆环百分比：按配置选择源的窗口，空=各源最大值</summary>
    private double IconPct()
    {
        if (string.IsNullOrWhiteSpace(_cfg.IconMetric) || _sources.Count == 0)
            return _sources.Count == 0 ? 0 : _sources.Max(s => s.Pct);
        var idx = _cfg.IconMetric.LastIndexOf(':');
        if (idx <= 0) return _sources.Max(s => s.Pct);
        var name = _cfg.IconMetric[..idx];
        var label = _cfg.IconMetric[(idx + 1)..];
        var s = _sources.FirstOrDefault(x => x.Name == name);
        if (s is null) return _sources.Max(x => x.Pct);
        var it = s.Items.FirstOrDefault(i => i.Label == label);
        return it.Quota > 0 ? it.Used / it.Quota * 100 : 0;
    }

    private void OpenPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_web.Url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Error("打开面板失败: {0}", e.Message);
        }
    }

    // ---- 刷新循环 ----
    private async Task RunLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Log.Error("刷新循环异常: {0}", e.Message);
            }
            try
            {
                await Task.Delay(_cfg.RefreshIntervalSec * 1000, _stop.Token);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            CheckConfigChanged();
            if (_sources.Count == 0) return;
            // 并行刷新各源
            await Task.WhenAll(_sources.Select(async src =>
            {
                try
                {
                    await src.FetchAsync();
                }
                catch (Exception e)
                {
                    src.Error = e.Message.Length > 60 ? e.Message[..60] : e.Message;
                }
            }));
            RecordHistory();
            CheckThresholds();
            if (_sources.All(s => s.Error is null)) _lastOk = DateTime.Now;
        }
        catch (Exception e)
        {
            Log.Error("refresh 异常: {0}", e.Message);
        }
        UpdateUi();
    }

    private void RecordHistory()
    {
        var changed = false;
        foreach (var s in _sources)
        {
            if (s.Error is not null) continue;
            var row = s.HistoryRow();
            if (row is { } r)
            {
                _history.UpsertToday(s.Name, r.Label, r.Used, r.Quota);
                changed = true;
            }
        }
        if (changed) _history.Save();
    }

    private void CheckThresholds()
    {
        foreach (var s in _sources)
        {
            var pct = s.Pct;
            var prev = _pctSeen.GetValueOrDefault(s.Name, 0);
            if (pct >= _cfg.NotifyThresholdPct && prev < _cfg.NotifyThresholdPct && s.Error is null)
            {
                var msg = $"{s.Name} 用量已达 {pct:0}%，请注意";
                Log.Info("阈值提醒: {0}", msg);
                Notify(msg);
            }
            _pctSeen[s.Name] = pct;

            // 方舟 5h 窗口 ≥80% 单独预警：窗口重置最快，浪费最可惜
            if (s is ArkAFPSource ark
                && ark.Items.FirstOrDefault(it => it.Label == "5h") is { } f && f.Quota > 0)
            {
                var fivePct = f.Used / f.Quota * 100;
                var key = s.Name + ":5h";
                var prevFive = _pctSeen.GetValueOrDefault(key, 0);
                if (fivePct >= 80 && prevFive < 80 && s.Error is null)
                {
                    var cd = Fmt.Countdown(s.ResetTimes.GetValueOrDefault("5h"));
                    var msg = $"方舟 5h 窗口已用 {fivePct:0}%（{Fmt.Num(f.Used)}/{Fmt.Num(f.Quota)}）"
                              + (cd is null ? "" : $"，{cd}");
                    Log.Info("窗口预警: {0}", msg);
                    Notify(msg);
                }
                _pctSeen[key] = fivePct;
            }
        }
    }

    private void Notify(string msg)
    {
        _ui.Post(_ =>
        {
            try { _icon.ShowBalloonTip(5000, "用量提醒", msg, ToolTipIcon.None); }
            catch { }
        }, null);
    }

    private void UpdateUi()
    {
        string Join(bool extras, bool low, bool updated)
        {
            var ls = new List<string>();
            foreach (var s in _sources)
            {
                if (ls.Count > 0) ls.Add("");   // 源之间空行分段
                ls.AddRange(s.TooltipLines());
                if (extras) ls.AddRange(s.TooltipExtras);
                if (low) ls.AddRange(s.TooltipExtrasLow);
            }
            if (updated && _lastOk is { } t)
            {
                ls.Add("");
                ls.Add("更新于 " + t.ToString("HH:mm:ss"));
            }
            return string.Join("\n", ls);
        }

        // 托盘 127 字符上限：超长时依次裁掉"更新于"、Token 行、重置倒计时，用量行永不截断
        var text = Join(true, true, true);
        if (text.Length > 127) text = Join(true, true, false);
        if (text.Length > 127) text = Join(true, false, false);
        if (text.Length > 127) text = Join(false, false, false);
        if (text.Length > 127) text = text[..127];
        var err = _sources.Any(s => s.Error is not null);
        var pct = IconPct();
        Log.Info("tooltip len={0} icon={1} pct={2:0}: {3}", text.Length,
            string.IsNullOrEmpty(_cfg.IconMetric) ? "auto" : _cfg.IconMetric, pct,
            text.Replace("\n", " ⏎ "));

        _ui.Post(_ =>
        {
            try
            {
                _icon.Text = text;
                _currentIcon?.Dispose();
                _currentIcon = IconFactory.Make(pct, err, _cfg.IconShowPct);
                _icon.Icon = _currentIcon;
            }
            catch { }
        }, null);
    }

    // ---- 配置监视 / 重载 ----
    private DateTime ConfigMtime()
    {
        try { return File.GetLastWriteTimeUtc(AppPaths.ConfigPath); }
        catch { return default; }
    }

    private void CheckConfigChanged()
    {
        var m = ConfigMtime();
        if (_cfgMtime != default && m > _cfgMtime.AddSeconds(1))
        {
            Log.Info("检测到 config.json 变更，自动重载");
            ReloadFromConfig();
        }
        else
        {
            _cfgMtime = m;
        }
    }

    public void ReloadFromConfig()
    {
        try
        {
            var fresh = AppConfig.Load();
            _cfg.RefreshIntervalSec = fresh.RefreshIntervalSec;
            _cfg.NotifyThresholdPct = fresh.NotifyThresholdPct;
            _cfg.IconMetric = fresh.IconMetric;
            _cfg.IconShowPct = fresh.IconShowPct;
            var traeCookie = fresh.Sources.FirstOrDefault(s => s.Type == "trae")?.Cookie;
            if (!string.IsNullOrWhiteSpace(traeCookie))
                foreach (var sc in _cfg.Sources.Where(s => s.Type == "trae"))
                    sc.Cookie = traeCookie;
            BuildSources();
            // 菜单是 WinForms 控件，必须回 UI 线程重建，否则刷新线程会死锁
            _ui.Post(_ => _icon.ContextMenuStrip = BuildMenu(), null);
            _cfgMtime = ConfigMtime();
            Log.Info("配置已重载: {0} 个源, 间隔 {1}s", _sources.Count, _cfg.RefreshIntervalSec);
            _ = Task.Run(RefreshAsync);
        }
        catch (Exception e)
        {
            Log.Error("重载配置失败: {0}", e.Message);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _web.Stop();
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
