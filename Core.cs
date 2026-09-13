using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraeUsageTray;

public static class AppPaths
{
    public static readonly string AppDir =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>当前 exe 完整路径（开机自启注册表用）</summary>
    public static readonly string ExePath = Environment.ProcessPath
        ?? Path.Combine(AppDir, "TRAE Usage.exe");

    public static readonly string ConfigPath = Path.Combine(AppDir, "config.json");
    public static readonly string LogPath = Path.Combine(AppDir, "tray.log");
    public static readonly string HistoryPath = Path.Combine(AppDir, "history.csv");

    public const string TraeBase = "https://console.enterprise.trae.cn";
    public const string TraeLoginUrl = TraeBase + "/personal/usage";
    public const string ArkPage =
        "https://console.volcengine.com/ark/region:cn-beijing/subscription/agent-plan-enterprise?tab=subscription";

    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        UseCookies = false, // 手动管理 Cookie 头，否则会被 CookieContainer 剥离
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

// ---------------- 配置 ----------------
public sealed class SourceConfig
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("cookie")] public string Cookie { get; set; } = "";
}

public sealed class AppConfig
{
    [JsonPropertyName("refresh_interval_sec")] public int RefreshIntervalSec { get; set; } = 60;
    [JsonPropertyName("notify_threshold_pct")] public int NotifyThresholdPct { get; set; } = 85;
    [JsonPropertyName("chrome_idle_stop_sec")] public int ChromeIdleStopSec { get; set; } = 600;
    [JsonPropertyName("cdp_port")] public int CdpPort { get; set; } = 9223;
    [JsonPropertyName("cdp_port_trae")] public int? CdpPortTrae { get; set; }
    [JsonPropertyName("chrome_profile_dir")] public string ChromeProfileDir { get; set; } = "";
    [JsonPropertyName("chrome_profile_dir_trae")] public string ChromeProfileDirTrae { get; set; } = "";
    [JsonPropertyName("chrome_path")] public string ChromePath { get; set; } = "";
    [JsonPropertyName("webui_port")] public int WebUiPort { get; set; } = 9876;
    [JsonPropertyName("icon_metric")] public string IconMetric { get; set; } = ""; // 图标圆环显示的指标，空=自动（各源最大值）
    [JsonPropertyName("icon_show_pct")] public bool IconShowPct { get; set; } // 图标圆环内是否显示百分比数字
    [JsonPropertyName("sources")] public List<SourceConfig> Sources { get; set; } = new();

    public static AppConfig Load()
    {
        var json = File.ReadAllText(AppPaths.ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, AppPaths.JsonOpts) ?? new AppConfig();
    }

    /// <summary>加载配置；首次运行或文件损坏时生成默认配置，绝不崩溃</summary>
    public static AppConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(AppPaths.ConfigPath), AppPaths.JsonOpts);
                if (cfg is not null) return cfg;
            }
        }
        catch (Exception e)
        {
            Log.Warn("config.json 解析失败，已生成默认配置: {0}", e.Message);
        }

        var def = new AppConfig();
        def.Sources.Add(new SourceConfig { Type = "trae", Name = "TRAE" });
        def.Sources.Add(new SourceConfig { Type = "ark_afp", Name = "方舟AFP" });
        def.Save();
        Log.Info("已生成默认配置 config.json（登录态将在首次刷新时引导）");
        return def;
    }

    public void Save()
    {
        File.WriteAllText(AppPaths.ConfigPath,
            JsonSerializer.Serialize(this, AppPaths.JsonOpts));
    }
}

// ---------------- 历史快照 ----------------
public sealed class History
{
    private readonly string _path;
    private readonly Dictionary<(string Date, string Name, string Label), (double Used, double Quota)> _rows = new();

    public History(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                var parts = line.Split(',');
                if (parts.Length == 5 && parts[0] != "date"
                    && double.TryParse(parts[3], out var u) && double.TryParse(parts[4], out var q))
                    _rows[(parts[0], parts[1], parts[2])] = (u, q);
            }
        }
        catch (Exception e)
        {
            Log.Warn("历史 CSV 读取失败: {0}", e.Message);
        }
    }

    public void UpsertToday(string name, string label, double used, double quota)
        => _rows[(DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"), name, label)] = (used, quota);

    public List<(string Date, double Pct)> Series(string name, string label, int days = 30)
    {
        var list = new List<(string, double)>();
        for (var i = days - 1; i >= 0; i--)
        {
            var d = DateOnly.FromDateTime(DateTime.Now).AddDays(-i).ToString("yyyy-MM-dd");
            if (_rows.TryGetValue((d, name, label), out var v) && v.Quota > 0)
                list.Add((d, v.Used / v.Quota * 100));
        }
        return list;
    }

    public void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            using (var w = new StreamWriter(tmp))
            {
                w.WriteLine("date,name,label,used,quota");
                foreach (var key in _rows.Keys.OrderBy(k => k))
                {
                    var (u, q) = _rows[key];
                    w.WriteLine($"{key.Item1},{key.Item2},{key.Item3},{u},{q}");
                }
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warn("历史 CSV 写入失败: {0}", e.Message);
        }
    }
}

// ---------------- 图标 ----------------
public static class IconFactory
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static Icon Make(double pct, bool error, bool showPct = false)
    {
        // 按任务栏实际显示尺寸原生绘制（SM_CXSMICON），避免 64px 缩小 4 倍导致文字发虚
        var n = GetSystemMetrics(49);
        if (n is < 12 or > 64) n = 16;
        var s = (float)n;
        using var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var stroke = Math.Max(2f, s / 8f);
            var inset = stroke / 2f + s * 0.04f;
            var d = s - inset * 2f;
            using var trackPen = new Pen(Color.FromArgb(255, 120, 120, 120), stroke);
            g.DrawArc(trackPen, inset, inset, d, d, 0, 360);

            if (error)
            {
                using var white = new SolidBrush(Color.White);
                var w = s / 8f;                              // 竖条+点（64 设计等比）
                g.FillRectangle(white, s * 0.4375f, s * 0.21875f, w, s * 0.375f);
                g.FillEllipse(white, s * 0.4375f, s * 0.671875f, w, w);
            }
            else
            {
                var color = pct < 60 ? Color.FromArgb(255, 76, 175, 80)
                    : pct < 85 ? Color.FromArgb(255, 255, 193, 7)
                    : Color.FromArgb(255, 244, 67, 54);
                if (pct > 0)
                {
                    using var pen = new Pen(color, stroke);
                    var sweep = (float)Math.Clamp(pct, 0, 100) / 100f * 360f;
                    if (sweep >= 360f) sweep = 359.9f;      // DrawArc 满圆不闭合，规避
                    g.DrawArc(pen, inset, inset, d, d, -90, sweep);
                }
                if (showPct)
                    DrawPctText(g, s, pct, color);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>圆环内绘制百分比数字：硬边文字渲染 + 白色描边，原生像素尺寸下清晰不发虚</summary>
    private static void DrawPctText(Graphics g, float s, double pct, Color color)
    {
        var text = ((int)Math.Round(Math.Clamp(pct, 0, 100))).ToString();
        var em = text.Length switch { 1 => s * 0.66f, 2 => s * 0.55f, _ => s * 0.42f };
        using var font = new Font(FontFamily.GenericSansSerif, em, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var layout = new RectangleF(s * 0.06f, s * 0.10f, s * 0.88f, s * 0.80f);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        using var white = new SolidBrush(Color.White);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            g.DrawString(text, font, white,
                new RectangleF(layout.X + dx, layout.Y + dy, layout.Width, layout.Height), fmt);
        }
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, layout, fmt);
    }
}

// ---------------- 工具 ----------------
public static class Fmt
{
    public static string Num(double v)
    {
        if (v >= 1e8) return $"{v / 1e8:0.##}亿";
        if (v >= 1e4) return $"{v / 1e4:0.##}万";
        return $"{v:0}";
    }

    /// <summary>ResetTime(毫秒 epoch) → "12天2小时后重置"，已过期返回 null</summary>
    public static string? Countdown(double? resetMs)
    {
        if (resetMs is not { } ms) return null;
        var sec = ms / 1000.0 - (DateTime.Now - DateTime.UnixEpoch).TotalSeconds;
        if (sec <= 0) return null;
        var days = (int)(sec / 86400);
        var hours = (int)(sec % 86400 / 3600);
        var mins = (int)(sec % 3600 / 60);
        if (days > 0) return $"{days}天{hours}小时后重置";
        if (hours > 0) return $"{hours}小时{mins}分钟后重置";
        return $"{mins}分钟后重置";
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static event Action<string>? Emitted;

    public static void Info(string fmt, params object[] args) => Write("INFO ", fmt, args);
    public static void Warn(string fmt, params object[] args) => Write("WARN ", fmt, args);
    public static void Error(string fmt, params object[] args) => Write("ERROR", fmt, args);

    private static void Write(string level, string fmt, object[] args)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} " + string.Format(fmt, args);
        lock (Gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(AppPaths.LogPath, line + Environment.NewLine);
            }
            catch { }
        }
        Emitted?.Invoke(line);
    }

    /// <summary>日志轮转：tray.log 超 1MB 时顺延为 tray.1.log / tray.2.log（共 3 份）</summary>
    private static void RotateIfNeeded()
    {
        var path = AppPaths.LogPath;
        if (!File.Exists(path) || new FileInfo(path).Length <= 1_000_000) return;
        var one = path + ".1";
        var two = path + ".2";
        if (File.Exists(two)) File.Delete(two);
        if (File.Exists(one)) File.Move(one, two);
        File.Move(path, one);
    }
}
