using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace TraeUsageTray;

/// <summary>用量数据源基类</summary>
public abstract class Source
{
    public string Name { get; }
    public string? Error { get; set; }
    public List<(string Label, double Used, double Quota)> Items { get; protected set; } = [];
    public List<string> ExtraLines { get; protected set; } = [];
    public Dictionary<string, double?> ResetTimes { get; protected set; } = new();

    protected Source(string name) => Name = name;

    /// <summary>月度（最后一个）窗口百分比</summary>
    public double Pct
    {
        get
        {
            if (Error is not null || Items.Count == 0) return 0;
            var (_, used, quota) = Items[^1];
            return quota > 0 ? used / quota * 100 : 0;
        }
    }

    public virtual (string Label, double Used, double Quota)? HistoryRow() => null;
    public virtual string? HistoryLabel() => null;

    /// <summary>悬停附加行（高优先级，如方舟重置倒计时），超长时最后裁剪</summary>
    public virtual IReadOnlyList<string> TooltipExtras => ExtraLines;

    /// <summary>悬停附加行（低优先级，如 Token），超长时先裁剪</summary>
    public virtual IReadOnlyList<string> TooltipExtrasLow => [];

    /// <summary>悬停空间不足时，该源是否可压缩为单行（仅显示最关键窗口）</summary>
    public virtual bool CompactWhenCrowded => false;

    /// <summary>压缩模式下的单行（默认取最后一个窗口，通常是月/总）</summary>
    public virtual string CompactLine()
    {
        if (Error is not null) return $"{Name}: 出错";
        if (Items.Count == 0) return $"{Name}: 无数据";
        var (lb, u, q) = Items[^1];
        return $"{Name} {lb}: {Fmt.Num(u)}/{Fmt.Num(q)}{(q > 0 ? $" {u / q * 100:0}%" : "")}";
    }

    public List<string> TooltipLines()
    {
        if (Error is not null) return [$"{Name}: 出错({Error})"];
        if (Items.Count == 0) return [$"{Name}: 无数据"];
        // 段落式排版：源名一行，每个窗口一行；仅最后一项附百分比，适应托盘 127 字符上限
        var lines = new List<string> { Name };
        for (var i = 0; i < Items.Count; i++)
        {
            var (lb, u, q) = Items[i];
            var pct = i == Items.Count - 1 && q > 0 ? $" {u / q * 100:0}%" : "";
            lines.Add($"{lb}: {Fmt.Num(u)}/{Fmt.Num(q)}{pct}");
        }
        return lines;
    }

    public List<string> DetailLines()
    {
        if (Error is not null) return [$"{Name}: 出错 — {Error}"];
        var lines = Items.Select(it =>
        {
            var (lb, u, q) = it;
            var cd = Fmt.Countdown(ResetTimes.GetValueOrDefault(lb));
            var suffix = cd is null ? "" : $"，{cd}";
            return $"{Name}.{lb}: {Fmt.Num(u)}/{Fmt.Num(q)} ({(q > 0 ? u / q * 100 : 0):0.0}%){suffix}";
        }).ToList();
        lines.AddRange(ExtraLines);
        return lines;
    }

    public abstract Task FetchAsync();
}

public sealed class NotLoggedInResponse : Exception
{
    public NotLoggedInResponse(string msg) : base(msg) { }
}

// ---------------- TRAE 企业版（HTTP 直连） ----------------
public sealed class TraeSource : Source
{
    private const string ApiQuota = AppPaths.TraeBase + "/trae/gtm/tob/api/v1/config/get_personal_quota";
    private const string ApiUsage = AppPaths.TraeBase + "/trae/gtm/tob/api/v1/config/get_user_model_usage";
    private const string ApiCore = AppPaths.TraeBase + "/trae/gtm/tob/api/v1/config/get_personal_core_data";

    private readonly SourceConfig _cfg;
    private readonly BrowserSession? _session;

    public string? TokenLine { get; private set; }

    public double? ChatTokens { get; private set; }
    public double? CompTokens { get; private set; }

    /// <summary>Token 行为低优先级：悬停空间不足时先于方舟重置倒计时被裁剪</summary>
    public override IReadOnlyList<string> TooltipExtras => [];

    public override IReadOnlyList<string> TooltipExtrasLow =>
        ChatTokens is { } c && CompTokens is { } p ? [$"Token: {Fmt.Num(c)}+{Fmt.Num(p)}"] : [];

    public TraeSource(SourceConfig cfg, BrowserSession? session) : base(string.IsNullOrWhiteSpace(cfg.Name) ? "TRAE" : cfg.Name)
    {
        _cfg = cfg;
        _session = session;
    }

    /// <summary>浏览器登录态检测：get_personal_core_data 返回 code==0</summary>
    public static async Task<bool> TraeCheckAsync(BrowserSession session)
    {
        var r = await session.CallApiAsync(ApiCore, new { });
        if (!r.TryGetProperty("json", out var j) || j.ValueKind != JsonValueKind.Object) return false;
        return j.TryGetProperty("code", out var code) && code.GetInt32() == 0;
    }

    private HttpRequestMessage Request(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Headers =
            {
                Accept = { new("application/json") },
                Referrer = new Uri(AppPaths.TraeBase + "/personal/usage"),
            },
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Origin", AppPaths.TraeBase);
        req.Headers.TryAddWithoutValidation("Cookie", _cfg.Cookie);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
        return req;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch
        {
            // 非 JSON（可能被重定向到登录页）
            throw new NotLoggedInResponse("响应非JSON: " + text[..Math.Min(60, text.Length)]);
        }
    }

    private static void EnsureLoggedIn(JsonElement body, HttpResponseMessage resp)
    {
        var status = (int)resp.StatusCode;
        if (status is 401 or 403) throw new NotLoggedInResponse($"HTTP {status}");
        if (!body.TryGetProperty("code", out var code) || code.GetInt32() != 0)
        {
            var msg = body.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            // 诊断：打印实际发送的请求头，定位 cookie 是否真正发出
            Log.Error("[TRAE] 未登录响应 status={0} req={1}", status, resp.RequestMessage?.ToString() ?? "n/a");
            if (msg.Contains("登录") || msg.Contains("login", StringComparison.OrdinalIgnoreCase))
                throw new NotLoggedInResponse(msg.Length > 50 ? msg[..50] : msg);
            throw new Exception("接口返回: " + (msg.Length > 50 ? msg[..50] : code.ToString()));
        }
    }

    public override async Task FetchAsync()
    {
        try
        {
            await FetchDataAsync();
        }
        catch (NotLoggedInResponse)
        {
            if (_session is null) throw;
            // 自动重登并回写 Cookie
            await _session.ForceReloginAsync(m => Log.Info(m));
            await RefreshCookieFromBrowserAsync();
            await FetchDataAsync();
        }
    }

    private async Task FetchDataAsync()
    {
        using var r1 = await AppPaths.Http.SendAsync(Request(ApiQuota));
        var q = await ReadJsonAsync(r1);
        EnsureLoggedIn(q, r1);
        var uq = q.GetProperty("user_model_quota");
        var seatQ = uq.TryGetProperty("seat_pool_currency_quota", out var sq) ? sq.GetDouble() : 0;
        var paygoQ = uq.TryGetProperty("paygo_currency_quota", out var pq) ? pq.GetDouble() : 0;

        using var r2 = await AppPaths.Http.SendAsync(Request(ApiUsage));
        var u = await ReadJsonAsync(r2);
        EnsureLoggedIn(u, r2);

        double seatUsed = 0, paygoUsed = 0;
        foreach (var entry in u.GetProperty("user_usage_list").EnumerateArray())
        {
            var hasInSeat = entry.TryGetProperty("in_seat_detail", out var inSeat)
                            && inSeat.ValueKind == JsonValueKind.Object
                            && inSeat.TryGetProperty("chat_usage_details", out _);
            if (hasInSeat)
            {
                foreach (var d in inSeat.GetProperty("chat_usage_details").EnumerateArray())
                    seatUsed += d.TryGetProperty("total_cost_currency", out var c) ? c.GetDouble() : 0;
            }
            else
            {
                if (entry.TryGetProperty("model_usage_detail_list", out var mdl))
                    foreach (var d in mdl.EnumerateArray())
                        paygoUsed += d.TryGetProperty("total_cost_currency", out var c) ? c.GetDouble() : 0;
            }
        }

        Error = null;
        // 超额（PAYG 按量付费）单独一行：仅在有额度或实际有超额消耗时显示
        var items = new List<(string Label, double Used, double Quota)>
            { ("基础", seatUsed, seatQ) };
        if (paygoQ > 0 || paygoUsed > 0)
            items.Add(("超额", paygoUsed, paygoQ));
        items.Add(("总", seatUsed + paygoUsed, seatQ + paygoQ));
        Items = items;
        ResetTimes = [];

        // token 用量（官方汇总，get_personal_core_data；为官方聚合统计，可能有服务端缓存延迟）
        var prevChat = ChatTokens;
        var prevComp = CompTokens;
        TokenLine = null;
        ExtraLines = [];
        ChatTokens = null;
        CompTokens = null;
        try
        {
            using var r3 = await AppPaths.Http.SendAsync(Request(ApiCore));
            var c = await ReadJsonAsync(r3);
            if (c.TryGetProperty("code", out var code) && code.GetInt32() == 0
                && c.TryGetProperty("Data", out var data)
                && data.TryGetProperty("TokenUsage", out var tu))
            {
                var chat = tu.TryGetProperty("ChatUsageTokens", out var ch) ? ch.GetDouble() : 0;
                var comp = tu.TryGetProperty("CompletionUsageTokens", out var co) ? co.GetDouble() : 0;
                ChatTokens = chat;
                CompTokens = comp;
                TokenLine = $"{Name} Token: 对话{chat:N0} + 补全{comp:N0}";
                ExtraLines = [TokenLine];
                if (chat != prevChat || comp != prevComp)
                    Log.Info("[TRAE] Token 更新: 对话{0:N0} + 补全{1:N0}", chat, comp);
            }
        }
        catch
        {
            // token 是附加信息，失败不影响金额显示；ChatTokens 已清空，不会残留旧值
        }
    }

    /// <summary>从内置浏览器提取新会话 Cookie 并回写 config.json</summary>
    private async Task RefreshCookieFromBrowserAsync()
    {
        if (_session is null) throw new NotLoggedInResponse("无内置浏览器会话");
        var cookies = await _session.CookiesAsync([AppPaths.TraeBase + "/"]);
        string? val = null;
        foreach (var c in cookies)
        {
            if (c.TryGetProperty("name", out var n) && n.GetString() == "X-Cloudide-Tob-Session"
                && c.TryGetProperty("value", out var v))
            {
                val = v.GetString();
                break;
            }
        }
        if (string.IsNullOrEmpty(val)) throw new NotLoggedInResponse("浏览器中未找到 TRAE 会话 Cookie");

        _cfg.Cookie = "X-Cloudide-Tob-Session=" + val;
        try
        {
            var cfg = AppConfig.Load();
            foreach (var sc in cfg.Sources.Where(sc => sc.Type == "trae"))
                sc.Cookie = _cfg.Cookie;
            cfg.Save();
            Log.Info("[TRAE] Cookie 已自动更新并写回 config.json");
        }
        catch (Exception e)
        {
            Log.Warn("[TRAE] Cookie 回写失败(本次会话内仍可用): {0}", e.Message);
        }
    }

    public string GetCookie() => _cfg.Cookie;

    /// <summary>从内置浏览器重新提取 Cookie（供"重新登录 TRAE"菜单使用）</summary>
    public Task ReloadCookieAsync() => RefreshCookieFromBrowserAsync();

    public override (string, double, double)? HistoryRow()
        => Items.FirstOrDefault(it => it.Label == "总" && it.Quota > 0) is { } it ? (it.Label, it.Used, it.Quota) : null;

    public override string? HistoryLabel() => "总";
}

// ---------------- 火山方舟 AgentPlan 企业版 AFP ----------------
public sealed class ArkAFPSource : Source
{
    public const string ApiSeat =
        "https://console.volcengine.com/api/top/ark/cn-beijing/2024-01-01/GetSeatInfo?";
    public const string ApiUsage =
        "https://console.volcengine.com/api/top/ark/cn-beijing/2024-01-01/GetAgentPlanSeatAFPUsage?";

    private readonly BrowserSession _session;

    public ArkAFPSource(SourceConfig cfg, BrowserSession session)
        : base(string.IsNullOrWhiteSpace(cfg.Name) ? "方舟AFP" : cfg.Name)
        => _session = session;

    /// <summary>方舟登录态检测：GetSeatInfo 返回 SeatID</summary>
    public static async Task<bool> ArkCheckAsync(BrowserSession session)
    {
        var r = await session.CallApiAsync(ApiSeat,
            new { ProjectName = "default", Scene = "agent_plan_enterprise" });
        if (!r.TryGetProperty("json", out var j))
        {
            // 非 JSON：多半是被 WAF 拦截或重定向到登录页，记录原文便于诊断
            var text = r.TryGetProperty("text", out var t) ? t.GetString() : null;
            var status = r.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
            Log.Info("[方舟] 校验失败: 响应非JSON status={0} text={1}", status,
                string.IsNullOrEmpty(text) ? "(空)" : text[..Math.Min(120, text.Length)]);
            return false;
        }
        if (j.ValueKind != JsonValueKind.Object) return false;
        if (j.TryGetProperty("ResponseMetadata", out var meta)
            && meta.TryGetProperty("Error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            Log.Info("[方舟] 校验失败: {0}: {1}",
                err.TryGetProperty("Code", out var c) ? c.GetString() ?? "?" : "?",
                err.TryGetProperty("Message", out var m) ? m.GetString() ?? "?" : "?");
            return false;
        }
        if (j.TryGetProperty("Result", out var res)
           && res.ValueKind == JsonValueKind.Object
           && res.TryGetProperty("SeatID", out var seat)
           && seat.GetString() is { Length: > 0 })
            return true;
        Log.Info("[方舟] 校验失败: Result 中无 SeatID: {0}", j.ToString()[..Math.Min(200, j.ToString().Length)]);
        return false;
    }

    public override async Task FetchAsync()
    {
        try
        {
            await FetchDataAsync();
        }
        catch (NotLoginException)
        {
            await _session.ForceReloginAsync(m => Log.Info(m));
            await FetchDataAsync();
        }
    }

    private async Task FetchDataAsync()
    {
        await _session.EnsureReadyAsync(m => Log.Info(m));
        var r = await _session.CallApiAsync(ApiSeat,
            new { ProjectName = "default", Scene = "agent_plan_enterprise" });
        var (j, err) = Unwrap(r);
        if (err is not null)
        {
            if (err.Value.GetProperty("Code").GetString() is { } c and ("NotLogin" or "LoginExpired"))
                throw new NotLoginException(c);
            throw new Exception("方舟API: " + (err.Value.TryGetProperty("Message", out var m) ? m.GetString() : "")?[..60]);
        }
        var seatId = j?.GetProperty("Result").GetProperty("SeatID").GetString();
        if (string.IsNullOrEmpty(seatId)) throw new Exception("未获取到 SeatID");

        var r2 = await _session.CallApiAsync(ApiUsage, new { SeatIDs = new[] { seatId } });
        var (j2, err2) = Unwrap(r2);
        if (err2 is not null)
        {
            if (err2.Value.GetProperty("Code").GetString() is { } c2 and ("NotLogin" or "LoginExpired"))
                throw new NotLoginException(c2);
            throw new Exception("方舟API: " + (err2.Value.TryGetProperty("Message", out var m2) ? m2.GetString() : "")?[..60]);
        }
        var usages = j2?.GetProperty("Result").GetProperty("SeatAFPUsages");
        if (usages is null || usages.Value.GetArrayLength() == 0)
            throw new Exception("无 SeatAFPUsages 数据");

        var u = usages.Value[0];
        var items = new List<(string, double, double)>();
        var resets = new Dictionary<string, double?>();
        foreach (var (key, label) in new[] { ("AFPFiveHour", "5h"), ("AFPWeekly", "周"), ("AFPMonthly", "月") })
        {
            if (!u.TryGetProperty(key, out var w)) continue;
            var quota = w.TryGetProperty("Quota", out var q) ? q.GetDouble() : 0;
            var used = w.TryGetProperty("Used", out var usedEl) ? usedEl.GetDouble() : 0;
            if (quota > 0)
            {
                items.Add((label, used, quota));
                resets[label] = w.TryGetProperty("ResetTime", out var rt) ? rt.GetDouble() : null;
            }
        }
        Error = null;
        Items = items;
        ResetTimes = resets;
        var cd = Fmt.Countdown(resets.GetValueOrDefault("月"));
        ExtraLines = cd is null ? [] : [$"月额度{cd}"];

        // 本月 Token 用量（套餐调用明细口径）
        await FetchMonthTokensAsync(seatId);
    }

    public const string ApiInferenceUsage =
        "https://console.volcengine.com/api/top/ark/cn-beijing/2024-01-01/GetInferenceUsage?";
    public const string ApiSeatUsageDetails =
        "https://console.volcengine.com/api/top/ark/cn-beijing/2024-01-01/GetSeatUsageDetails?";

    /// <summary>本月 Token 总用量（输入+输出）</summary>
    public double? MonthTokens { get; private set; }
    public string? MonthTokenLine { get; private set; }

    /// <summary>
    /// 本月套餐 Token 统计走浏览器会话（GetSeatUsageDetails，与 arkcli usage plan-details 同一底层 API）。
    /// 不用 arkcli：arkcli 有独立登录态，若登录的是别的火山账号会报 "no Agent Plan enterprise seat found"。
    /// </summary>
    internal async Task FetchMonthTokensAsync(string seatId)
    {
        double total = 0;
        try
        {
            var monthStart = DateTime.Now.AddDays(-(DateTime.Now.Day - 1)).ToString("yyyy-MM-dd");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var r = await _session.CallApiAsync(ApiSeatUsageDetails, new
            {
                Filter = new { StartTime = monthStart, EndTime = today },
                QueryInterval = "Day",
                SeatIDs = new[] { seatId },
            });
            if (r.TryGetProperty("json", out var j) && j.ValueKind == JsonValueKind.Object
                && j.TryGetProperty("Result", out var res)
                && res.TryGetProperty("SeatUsageDetails", out var seats)
                && seats.ValueKind == JsonValueKind.Array)
            {
                foreach (var seat in seats.EnumerateArray())
                {
                    if (!seat.TryGetProperty("Details", out var details)
                        || details.ValueKind != JsonValueKind.Array) continue;
                    foreach (var d in details.EnumerateArray())
                    {
                        var unit = d.TryGetProperty("Unit", out var un) ? un.GetString() : null;
                        if (!string.Equals(unit, "Tokens", StringComparison.OrdinalIgnoreCase)) continue;
                        if (d.TryGetProperty("Usage", out var usage) && usage.ValueKind == JsonValueKind.Number)
                            total += usage.GetDouble();
                    }
                }
            }
            Log.Info("[方舟] 本月Token(GetSeatUsageDetails): {0:N0}", total);
        }
        catch (Exception e)
        {
            Log.Warn("[方舟] 本月Token统计失败: {0}", e.Message);
        }
        MonthTokens = total;
        // 面板用完整行；悬停用短格式（托盘 127 字符预算有限）
        MonthTokenLine = $"本月Token: {Fmt.Num(total)}";
        ExtraLines = [.. ExtraLines, $"本月Token {Fmt.Num(total)}"];
    }

    private static (JsonElement? json, JsonElement? error) Unwrap(JsonElement res)
    {
        if (!res.TryGetProperty("json", out var j) || j.ValueKind != JsonValueKind.Object)
            return (null, null);
        if (j.TryGetProperty("ResponseMetadata", out var meta)
            && meta.TryGetProperty("Error", out var err) && err.ValueKind == JsonValueKind.Object)
            return (j.Clone(), err.Clone());
        return (j.Clone(), null);
    }

    public override (string, double, double)? HistoryRow()
        => Items.FirstOrDefault(it => it.Label == "月" && it.Quota > 0) is { } it ? (it.Label, it.Used, it.Quota) : null;

    public override string? HistoryLabel() => "月";
}

// ---------------- 火山方舟 AgentPlan（arkcli 数据源） ----------------
/// <summary>通过 arkcli usage plan 查询套餐 quota 快照；不依赖内置浏览器，走 arkcli 自己的登录态</summary>
public sealed class ArkCliSource : Source
{
    private readonly string _product;
    private readonly BrowserSession _session;

    public ArkCliSource(SourceConfig cfg, BrowserSession session)
        : base(string.IsNullOrWhiteSpace(cfg.Name) ? "方舟CLI" : cfg.Name)
    {
        _product = string.IsNullOrWhiteSpace(cfg.Cookie) ? "agent-plan-team" : cfg.Cookie.Trim();
        _session = session;
    }

    /// <summary>与方舟AFP 数据同源重复，悬停拥挤时压缩为单行，完整数据看面板</summary>
    public override bool CompactWhenCrowded => true;

    public override string CompactLine()
    {
        if (Error is not null) return $"{Name}: 出错({Error})";
        if (Items.FirstOrDefault(it => it.Label == "月") is { } m)
            return $"{Name} 月: {Fmt.Num(m.Used)}/{Fmt.Num(m.Quota)} {(m.Quota > 0 ? m.Used / m.Quota * 100 : 0):0}%";
        return base.CompactLine();
    }

    public override async Task FetchAsync()
    {
        var (json, err) = await ArkCli.RunAsync("usage", "plan", "--product", _product);
        if (json is null) throw new Exception("arkcli: " + err);

        var item = json.Value.GetProperty("items").EnumerateArray().FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("subscribed", out var sub)
            || !sub.GetBoolean())
            throw new Exception("arkcli: 未订阅 " + _product);

        var items = new List<(string Label, double Used, double Quota)>();
        var resets = new Dictionary<string, double?>();
        foreach (var p in item.GetProperty("periods").EnumerateArray())
        {
            var label = p.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            var used = p.TryGetProperty("used", out var u) ? u.GetDouble() : 0;
            var total = p.TryGetProperty("total", out var t) ? t.GetDouble() : 0;
            // CodingPlan 只有 percent，无 used/total
            if (total <= 0 && p.TryGetProperty("percent", out var pc))
            {
                var pct = pc.GetDouble();
                items.Add((label, pct, 100));
                resets[label] = null;
                continue;
            }
            if (total <= 0) continue;
            var map = label switch { "5h" => "5h", "weekly" => "周", "monthly" => "月", _ => label };
            items.Add((map, used, total));
            resets[map] = p.TryGetProperty("reset_at", out var r) && r.GetString() is { } s
                ? (double?)(DateTimeOffset.Parse(s).ToUnixTimeMilliseconds())
                : null;
        }
        Error = null;
        Items = items;
        ResetTimes = resets;
        var cd = Fmt.Countdown(resets.GetValueOrDefault("月"));
        ExtraLines = cd is null ? [] : [$"月额度{cd}"];

        // 本月 Token 用量（与方舟AFP 同口径：套餐调用明细 GetSeatUsageDetails）
        var mt = await ArkMonthTokens.FetchAsync(_product);
        MonthTokens = mt;
        MonthTokenLine = $"本月Token: {Fmt.Num(mt)}";
        ExtraLines = [.. ExtraLines, $"本月Token {Fmt.Num(mt)}"];
    }

    /// <summary>本月 Token 总用量（输入+输出）</summary>
    public double? MonthTokens { get; private set; }
    public string? MonthTokenLine { get; private set; }

    public override (string, double, double)? HistoryRow()
        => Items.FirstOrDefault(it => it.Label == "月" && it.Quota > 0) is { } it ? (it.Label, it.Used, it.Quota) : null;

    public override string? HistoryLabel() => "月";
}

/// <summary>
/// 本月套餐 Token 统计。
/// 注意：必须用 arkcli usage plan-details（底层 GetSeatUsageDetails，套餐调用明细）——
/// GetInferenceUsage 是按量付费管道，AgentPlan 套餐调用只返回异常行，正常消耗完全不在其中。
/// </summary>
internal static class ArkMonthTokens
{
    public static async Task<double> FetchAsync(string product)
    {
        try
        {
            var monthStart = DateTime.Now.AddDays(-(DateTime.Now.Day - 1)).ToString("yyyy-MM-dd");
            var (json, err) = await ArkCli.RunAsync("usage", "plan-details",
                "--product", product, "--start", monthStart);
            if (json is null)
            {
                Log.Warn("[方舟] 本月Token统计失败: {0}", err ?? "unknown");
                return 0;
            }
            double total = 0;
            if (json.Value.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in details.EnumerateArray())
                {
                    var unit = d.TryGetProperty("unit", out var u) ? u.GetString() : null;
                    if (!string.Equals(unit, "Tokens", StringComparison.OrdinalIgnoreCase)) continue;
                    if (d.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Number)
                        total += usage.GetDouble();
                }
            }
            Log.Info("[方舟] 本月Token(plan-details): {0:N0}", total);
            return total;
        }
        catch (Exception e)
        {
            Log.Warn("[方舟] 本月Token统计失败: {0}", e.Message);
            return 0;
        }
    }
}

/// <summary>arkcli 命令行封装</summary>
public static class ArkCli
{
    /// <summary>定位 arkcli 可执行文件：npm 全局安装通常是 arkcli.cmd，Process.Start 无法自动解析</summary>
    private static readonly Lazy<string?> ExePath = new(() =>
    {
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var p = Path.Combine(dir.Trim(), "arkcli" + ext.ToLower());
                if (File.Exists(p)) return p;
            }
            var bare = Path.Combine(dir.Trim(), "arkcli");
            if (File.Exists(bare)) return bare;
        }
        return null;
    });

    public static async Task<(JsonElement? json, string? error)> RunAsync(params string[] args)
    {
        var exe = ExePath.Value
            ?? throw new Exception("未找到 arkcli（请先安装: npm i -g @volcengine/ark-cli）");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.EnvironmentVariables["ARKCLI_NO_UPDATE_NOTIFIER"] = "1";
        psi.EnvironmentVariables["ARKCLI_CALLER_TYPE"] = "ai_agent";
        psi.EnvironmentVariables["ARKCLI_CALLER_NAME"] = "trae-usage-tray";
        psi.EnvironmentVariables["ARKCLI_SKILL_NAME"] = "arkcli-usage";

        using var p = Process.Start(psi)
            ?? throw new Exception("无法启动 arkcli（请确认已安装: npm i -g @volcengine/ark-cli）");
        var outs = await p.StandardOutput.ReadToEndAsync();
        var errs = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            return (null, (errs.Length > 0 ? errs : outs) is { } m && m.Length > 0 ? m[..Math.Min(120, m.Length)] : $"exit {p.ExitCode}");
        try
        {
            return (JsonDocument.Parse(outs).RootElement.Clone(), null);
        }
        catch
        {
            return (null, "输出非 JSON: " + outs[..Math.Min(80, outs.Length)]);
        }
    }
}
