namespace TraeUsageTray;

/// <summary>liquid-glass 面板页面（自 webui.py 移植，样式与端点保持一致）</summary>
internal static class HtmlTemplate
{
    public const string Html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>用量监控</title>
<style>
:root {
  color-scheme: light;
  --bg: #e9edf5;
  --border: rgba(255, 255, 255, 0.65);
  --text: #1c1c1e;
  --muted: #6e6e73;
  --primary: #007aff;
  --primary-soft: rgba(0, 122, 255, 0.12);
  --danger: #ff3b30;
  --danger-soft: rgba(255, 59, 48, 0.1);
  --tag-bg: rgba(120, 120, 128, 0.12);
  --glass-bg: rgba(255, 255, 255, 0.55);
  --glass-strong: rgba(255, 255, 255, 0.78);
  --glass-btn: rgba(255, 255, 255, 0.5);
  --glass-input: rgba(255, 255, 255, 0.6);
  --glass-edge: rgba(255, 255, 255, 0.7);
  --glass-blur: blur(28px) saturate(1.8);
  --glass-blur-light: blur(16px) saturate(1.6);
  --glass-shadow: 0 8px 32px rgba(31, 38, 135, 0.1), 0 2px 8px rgba(31, 38, 135, 0.06);
  --glass-inner-hl: inset 0 1px 0 rgba(255, 255, 255, 0.75);
  --primary-ring: rgba(0, 122, 255, 0.22);
  --ease-spring: cubic-bezier(0.32, 0.72, 0, 1);
  --dur-fast: 0.2s;
  --dur-med: 0.35s;
}
:root[data-theme='dark'] {
  color-scheme: dark;
  --bg: #0a0c12;
  --border: rgba(255, 255, 255, 0.14);
  --text: #f2f2f7;
  --muted: #98989f;
  --primary: #0a84ff;
  --primary-soft: rgba(10, 132, 255, 0.18);
  --danger: #ff453a;
  --danger-soft: rgba(255, 69, 58, 0.14);
  --tag-bg: rgba(120, 120, 128, 0.24);
  --glass-bg: rgba(28, 30, 38, 0.55);
  --glass-strong: rgba(44, 46, 56, 0.8);
  --glass-btn: rgba(255, 255, 255, 0.1);
  --glass-input: rgba(255, 255, 255, 0.08);
  --glass-edge: rgba(255, 255, 255, 0.16);
  --glass-shadow: 0 12px 40px rgba(0, 0, 0, 0.45), 0 2px 10px rgba(0, 0, 0, 0.3);
  --glass-inner-hl: inset 0 1px 0 rgba(255, 255, 255, 0.12);
  --primary-ring: rgba(10, 132, 255, 0.3);
}
* { box-sizing: border-box; }
html { -webkit-font-smoothing: antialiased; }
body {
  margin: 0; min-height: 100vh;
  background: var(--bg); color: var(--text); font-size: 14px;
  font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Text', 'Inter', 'Segoe UI',
    'PingFang SC', 'Microsoft YaHei', system-ui, sans-serif;
  transition: color var(--dur-med) var(--ease-spring);
}
::selection { background: var(--primary-ring); }
::-webkit-scrollbar { width: 8px; height: 8px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: rgba(120, 120, 128, 0.35); border-radius: 99px; }
body::before {
  content: ''; position: fixed; inset: 0; z-index: -2; pointer-events: none;
  background:
    radial-gradient(ellipse 60% 50% at 12% 8%, rgba(0, 122, 255, 0.28), transparent 65%),
    radial-gradient(ellipse 55% 45% at 88% 12%, rgba(191, 90, 242, 0.24), transparent 65%),
    radial-gradient(ellipse 60% 55% at 82% 88%, rgba(88, 86, 214, 0.22), transparent 65%),
    radial-gradient(ellipse 55% 50% at 15% 85%, rgba(100, 210, 255, 0.26), transparent 65%),
    var(--bg);
  transition: background 0.5s ease;
}
body::after {
  content: ''; position: fixed; inset: -25%; z-index: -1; pointer-events: none;
  background:
    radial-gradient(circle 38vw at 30% 40%, rgba(0, 122, 255, 0.14), transparent 70%),
    radial-gradient(circle 32vw at 70% 60%, rgba(255, 45, 146, 0.1), transparent 70%);
  filter: blur(40px);
  animation: aurora-drift 28s ease-in-out infinite alternate;
}
@keyframes aurora-drift {
  0%   { transform: translate3d(-4%, -3%, 0) scale(1); }
  50%  { transform: translate3d(3%, 4%, 0) scale(1.06); }
  100% { transform: translate3d(-2%, 2%, 0) scale(1.02); }
}
:root[data-theme='dark'] body::before {
  background:
    radial-gradient(ellipse 60% 50% at 12% 8%, rgba(10, 132, 255, 0.22), transparent 65%),
    radial-gradient(ellipse 55% 45% at 88% 12%, rgba(191, 90, 242, 0.16), transparent 65%),
    radial-gradient(ellipse 60% 55% at 82% 88%, rgba(94, 92, 230, 0.18), transparent 65%),
    radial-gradient(ellipse 55% 50% at 15% 85%, rgba(100, 210, 255, 0.12), transparent 65%),
    var(--bg);
}
.glass-panel {
  background: var(--glass-bg);
  -webkit-backdrop-filter: var(--glass-blur); backdrop-filter: var(--glass-blur);
  border: 1px solid var(--glass-edge);
  border-radius: 18px;
  box-shadow: var(--glass-shadow), var(--glass-inner-hl);
  transition: box-shadow var(--dur-med) var(--ease-spring);
}
.glass-panel:hover {
  box-shadow: 0 14px 44px rgba(31, 38, 135, 0.16), 0 3px 10px rgba(31, 38, 135, 0.08),
    var(--glass-inner-hl);
}
:root[data-theme='dark'] .glass-panel:hover {
  box-shadow: 0 16px 48px rgba(0, 0, 0, 0.55), 0 3px 10px rgba(0, 0, 0, 0.3),
    var(--glass-inner-hl);
}
.topbar {
  display: flex; align-items: center; gap: 14px; flex-wrap: wrap;
  min-height: 54px; margin: 12px 16px 0; padding: 8px 16px;
  position: sticky; top: 12px; z-index: 10;
}
.brand-text {
  font-size: 17px; font-weight: 700;
  background: linear-gradient(135deg, var(--primary), #bf5af2);
  -webkit-background-clip: text; background-clip: text; -webkit-text-fill-color: transparent;
}
.topbar .updated { color: var(--muted); font-size: 12px; margin-right: auto; }
.segmented {
  display: flex; gap: 2px; padding: 3px;
  background: var(--tag-bg); border-radius: 999px; width: fit-content;
}
.segmented button {
  padding: 4px 12px; border-radius: 999px; font-size: 12px; border: none;
  background: transparent; box-shadow: none; color: var(--muted);
  -webkit-backdrop-filter: none; backdrop-filter: none;
}
.segmented button.is-active {
  background: var(--glass-strong); color: var(--primary); font-weight: 600;
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.1), var(--glass-inner-hl);
}
button {
  font: inherit; cursor: pointer; padding: 6px 16px;
  border: 1px solid var(--border); border-radius: 999px;
  background: var(--glass-btn); color: var(--text);
  -webkit-backdrop-filter: var(--glass-blur-light); backdrop-filter: var(--glass-blur-light);
  box-shadow: var(--glass-inner-hl);
  transition: background-color var(--dur-fast) var(--ease-spring),
    border-color var(--dur-fast) var(--ease-spring), color var(--dur-fast) var(--ease-spring),
    box-shadow var(--dur-fast) var(--ease-spring), transform var(--dur-fast) var(--ease-spring),
    opacity var(--dur-fast) var(--ease-spring);
}
button:hover { border-color: var(--primary); color: var(--primary); background: var(--primary-soft); }
button:active { opacity: 0.75; transform: scale(0.96); }
button:focus-visible { outline: none; box-shadow: var(--glass-inner-hl), 0 0 0 3.5px var(--primary-ring); }
button.primary {
  background: linear-gradient(180deg, #3b9bff 0%, var(--primary) 100%);
  border-color: transparent; color: #fff; font-weight: 600;
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.35), 0 4px 14px rgba(0, 122, 255, 0.35);
  text-shadow: 0 1px 1px rgba(0, 0, 0, 0.12);
}
button.primary:hover { filter: brightness(1.08); color: #fff; border-color: transparent; }
input, textarea {
  font: inherit; padding: 7px 12px; width: 100%;
  border: 1px solid var(--border); border-radius: 10px;
  background: var(--glass-input); color: var(--text);
  -webkit-backdrop-filter: var(--glass-blur-light); backdrop-filter: var(--glass-blur-light);
  box-shadow: inset 0 1px 3px rgba(0, 0, 0, 0.05);
  transition: border-color var(--dur-fast) var(--ease-spring),
    box-shadow var(--dur-fast) var(--ease-spring);
}
input::placeholder, textarea::placeholder { color: var(--muted); opacity: 0.8; }
input:focus, textarea:focus {
  outline: none; border-color: var(--primary);
  box-shadow: inset 0 1px 3px rgba(0, 0, 0, 0.05), 0 0 0 3.5px var(--primary-ring);
}
.wrap { max-width: 1080px; margin: 0 auto; padding: 16px; display: grid; gap: 16px; }
.grid-cards { display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); }
.card { padding: 16px 18px; }
.source-head { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
.source-name { font-weight: 700; font-size: 15px; }
.badge { font-size: 11px; font-weight: 600; padding: 2px 9px; border-radius: 999px;
  background: var(--primary-soft); color: var(--primary); }
.badge.err { background: var(--danger-soft); color: var(--danger); }
.metric { display: flex; align-items: baseline; gap: 8px; margin: 8px 0 4px; }
.metric .label { font-size: 12px; color: var(--muted); width: 34px; flex: none; }
.metric .value { font-variant-numeric: tabular-nums; font-size: 13px; }
.metric .pct { margin-left: auto; font-variant-numeric: tabular-nums; font-size: 12px; color: var(--muted); }
.progress { height: 6px; border-radius: 99px; background: var(--tag-bg); overflow: hidden; }
.progress .bar {
  height: 100%; border-radius: 99px; width: 0;
  transition: width 0.6s var(--ease-spring), background-color 0.3s var(--ease-spring);
}
.bar.ok { background: linear-gradient(90deg, #34c759, #28c840); }
.bar.warn { background: linear-gradient(90deg, #ffb340, #ff9f0a); }
.bar.danger { background: linear-gradient(90deg, #ff6961, #ff3b30); }
.note {
  margin-top: 10px; font-size: 12px; color: var(--muted);
  background: var(--tag-bg); border-radius: 10px; padding: 7px 10px;
}
.err-text { color: var(--danger); font-size: 13px; }
.curve-svg { width: 100%; height: auto; display: block; }
.legend { display: flex; gap: 14px; flex-wrap: wrap; margin-top: 8px; }
.legend span { font-size: 12px; color: var(--muted); display: inline-flex; align-items: center; gap: 5px; }
.legend i { width: 14px; height: 3px; border-radius: 2px; display: inline-block; }
.cfg-grid { display: grid; gap: 12px; grid-template-columns: 1fr 1fr; }
.cfg-item label { display: block; font-size: 12px; color: var(--muted); margin-bottom: 5px; }
.cfg-full { grid-column: 1 / -1; }
textarea { resize: vertical; min-height: 64px; font-family: Consolas, monospace; font-size: 12px; }
.cfg-actions { display: flex; gap: 10px; align-items: center; grid-column: 1 / -1; }
.save-tip { font-size: 12px; color: var(--muted); }
h2.card-title { font-size: 13px; font-weight: 600; color: var(--muted); margin: 0 0 10px;
  letter-spacing: 0.4px; }
.page-enter { animation: page-in 0.35s var(--ease-spring) both; }
@keyframes page-in { from { opacity: 0; transform: translateY(12px); } to { opacity: 1; transform: none; } }
.loading { animation: pulse-fade 1.2s ease-in-out infinite; color: var(--muted); padding: 24px; text-align: center; }
@keyframes pulse-fade { 0%, 100% { opacity: 1; } 50% { opacity: 0.4; } }
::view-transition-old(root), ::view-transition-new(root) { animation-duration: 0.35s; }
@media (prefers-reduced-motion: reduce) {
  body::after { animation: none; }
  *, *::before, *::after {
    animation-duration: 0.01ms !important; animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
  }
}
@media (max-width: 640px) {
  .topbar { margin: 8px 8px 0; }
  .wrap { padding: 8px; }
  .grid-cards, .cfg-grid { grid-template-columns: 1fr; }
}
</style>
</head>
<body>
<header class="topbar glass-panel">
  <span class="brand-text">用量监控</span>
  <span class="updated" id="updated">加载中…</span>
  <div class="segmented" id="theme-seg">
    <button data-th="light">浅色</button>
    <button data-th="dark">深色</button>
  </div>
  <button class="primary" id="btn-refresh">立即刷新</button>
</header>

<main class="wrap page-enter">
  <section class="grid-cards" id="sources">
    <div class="glass-panel loading">正在获取数据…</div>
  </section>

  <section class="glass-panel card">
    <h2 class="card-title">近 30 天月度用量趋势（%）</h2>
    <svg class="curve-svg" id="curve" viewBox="0 0 640 170" preserveAspectRatio="none"></svg>
    <div class="legend" id="legend"></div>
  </section>

  <section class="glass-panel card">
    <h2 class="card-title">配置</h2>
    <div class="cfg-grid">
      <div class="cfg-item">
        <label for="cfg-interval">刷新间隔（秒，最小 5）</label>
        <input id="cfg-interval" type="number" min="5" step="5">
      </div>
      <div class="cfg-item">
        <label for="cfg-threshold">通知阈值（%）</label>
        <input id="cfg-threshold" type="number" min="0" max="100">
      </div>
      <div class="cfg-item cfg-full">
        <label for="cfg-cookie">TRAE Cookie（留空表示不修改，失效时程序也会自动重登）</label>
        <textarea id="cfg-cookie" placeholder="X-Cloudide-Tob-Session=..."></textarea>
      </div>
      <div class="cfg-actions">
        <button class="primary" id="btn-save">保存并重载</button>
        <span class="save-tip" id="save-tip"></span>
      </div>
    </div>
  </section>
</main>

<script>
const fmt = v => v >= 1e8 ? (v/1e8).toFixed(2)+'亿' : v >= 1e4 ? (v/1e4).toFixed(2)+'万' : v.toFixed(0);
const esc = s => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const COLORS = __CURVE_COLORS__;
let state = null, cfgFilled = false;

const root = document.documentElement;
const savedTheme = localStorage.getItem('theme') ||
  (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
function applyTheme(t) {
  const fn = () => { root.dataset.theme = t; localStorage.setItem('theme', t);
    document.querySelectorAll('#theme-seg button').forEach(b =>
      b.classList.toggle('is-active', b.dataset.th === t)); };
  document.startViewTransition ? document.startViewTransition(fn) : fn();
}
document.querySelectorAll('#theme-seg button').forEach(b =>
  b.onclick = () => applyTheme(b.dataset.th));
applyTheme(savedTheme);

async function fetchState() {
  const r = await fetch('/api/state');
  state = await r.json();
  render();
}
function barClass(p) { return p >= 85 ? 'danger' : p >= 60 ? 'warn' : 'ok'; }

function render() {
  if (!state) return;
  document.getElementById('updated').textContent =
    state.last_ok ? ('更新于 ' + state.last_ok) : '尚未成功更新';

  const box = document.getElementById('sources');
  box.innerHTML = state.sources.map((s, i) => {
    let body;
    if (s.error) {
      body = '<div class="err-text">出错：' + esc(s.error) + '</div>';
    } else {
      body = s.items.map(it => {
        const [lb, u, q] = it;
        const p = q ? Math.min(u / q * 100, 100) : 0;
        return '<div class="metric"><span class="label">' + esc(lb) + '</span>' +
          '<span class="value">' + fmt(u) + ' / ' + fmt(q) + '</span>' +
          '<span class="pct">' + p.toFixed(1) + '%</span></div>' +
          '<div class="progress"><div class="bar ' + barClass(p) + '" style="width:' + p + '%"></div></div>';
      }).join('') +
        (s.token ? '<div class="note">' + esc(s.token) + '</div>' : '') +
        (s.month_token ? '<div class="note">' + esc(s.month_token) + '</div>' : '') +
        (s.resets ? s.resets.filter(Boolean).map(r =>
          '<div class="note">' + esc(r) + '</div>').join('') : '');
    }
    const badge = s.error ? '<span class="badge err">出错</span>'
      : '<span class="badge">' + s.pct.toFixed(0) + '%</span>';
    return '<div class="glass-panel card source-card">' +
      '<div class="source-head"><span class="source-name">' + esc(s.name) + '</span>' + badge + '</div>' +
      body + '</div>';
  }).join('');

  const svg = document.getElementById('curve');
  const W = 640, H = 170, pl = 34, pr = 10, pt = 10, pb = 22;
  let g = '';
  [0, 50, 100].forEach(v => {
    const y = pt + (H - pt - pb) * (1 - v / 100);
    g += '<line x1="' + pl + '" y1="' + y + '" x2="' + (W - pr) + '" y2="' + y +
      '" stroke="rgba(120,120,128,0.18)" stroke-width="1"/>' +
      '<text x="' + (pl - 6) + '" y="' + (y + 3) + '" text-anchor="end" font-size="9" fill="#98989f">' + v + '</text>';
  });
  const legend = [];
  state.sources.forEach((s, i) => {
    const pts = (s.history && s.history.points) || [];
    if (pts.length < 2) return;
    const color = (state.curve_colors || COLORS)[i % (state.curve_colors || COLORS).length];
    const coords = pts.map((p, j) => {
      const x = pl + (W - pl - pr) * j / (pts.length - 1);
      const y = pt + (H - pt - pb) * (1 - Math.max(0, Math.min(p[1], 100)) / 100);
      return x.toFixed(1) + ',' + y.toFixed(1);
    }).join(' ');
    g += '<polyline points="' + coords + '" fill="none" stroke="' + color +
      '" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>';
    legend.push('<span><i style="background:' + color + '"></i>' + esc(s.name) + ' ' +
      esc(s.history.label) + '</span>');
  });
  svg.innerHTML = g + '<text x="' + pl + '" y="' + (H - 6) + '" font-size="9" fill="#98989f">-30天</text>' +
    '<text x="' + (W - pr) + '" y="' + (H - 6) + '" text-anchor="end" font-size="9" fill="#98989f">今天</text>';
  document.getElementById('legend').innerHTML =
    legend.join('') || '<span>数据积累中（每天记录一条快照）</span>';

  if (!cfgFilled) {
    document.getElementById('cfg-interval').value = state.interval;
    document.getElementById('cfg-threshold').value = state.threshold;
    document.getElementById('cfg-cookie').value = state.cookie_trae || '';
    cfgFilled = true;
  }
}

document.getElementById('btn-refresh').onclick = async () => {
  const b = document.getElementById('btn-refresh');
  b.disabled = true;
  try { await fetch('/api/refresh', { method: 'POST' }); } catch (e) {}
  setTimeout(async () => { await fetchState(); b.disabled = false; }, 2500);
};

document.getElementById('btn-save').onclick = async () => {
  const tip = document.getElementById('save-tip');
  const body = {
    interval: parseInt(document.getElementById('cfg-interval').value, 10),
    threshold: parseInt(document.getElementById('cfg-threshold').value, 10),
    cookie: document.getElementById('cfg-cookie').value.trim(),
  };
  try {
    const r = await fetch('/api/config', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const j = await r.json();
    tip.textContent = j.ok ? '已保存并重载 ✓' : ('失败：' + j.error);
  } catch (e) { tip.textContent = '失败：' + e; }
  setTimeout(() => tip.textContent = '', 4000);
};

fetchState();
setInterval(fetchState, 15000);
</script>
</body>
</html>
""";
}
