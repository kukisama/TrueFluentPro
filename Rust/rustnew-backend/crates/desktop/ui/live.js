/* ============================================================
   live.js — 实时翻译视图
   - 识别/翻译行渲染（partial 滚动更新 → final 落定）
   - 开始/停止开关、原文/译文/双语切换
   - 历史记录加载/清空
   - 监听后端 live:partial / live:final / live:status 事件
   语音资源下拉（#ltResource）归 settings.js 管，这里只调 activateRes。
   ============================================================ */

import { invoke, listen, state, tr, el, fmtTime, fillSelect } from "./core.js";
import { activateRes } from "./settings.js";

/* 可选语言（源：含自动识别；目标：翻译目标码） */
const SOURCE_LANGS = [
  { v: "auto", k: "lang.auto" },
  { v: "zh-CN", t: "中文 (普通话)" },
  { v: "en-US", t: "English (US)" },
  { v: "ja-JP", t: "日本語" },
  { v: "ko-KR", t: "한국어" },
  { v: "fr-FR", t: "Français" },
  { v: "de-DE", t: "Deutsch" },
  { v: "es-ES", t: "Español" },
];
const TARGET_LANGS = [
  { v: "zh-Hans", t: "中文 (简体)" },
  { v: "en", t: "English" },
  { v: "ja", t: "日本語" },
  { v: "ko", t: "한국어" },
  { v: "fr", t: "Français" },
  { v: "de", t: "Deutsch" },
  { v: "es", t: "Español" },
];

let livePartialRow = null;

/* ---------------- 渲染 ---------------- */
function renderLiveRow(p, partial) {
  const list = document.getElementById("ltList");
  const empty = document.getElementById("ltEmpty");
  if (empty) empty.style.display = "none";

  let row = partial ? livePartialRow : null;
  if (!row) {
    row = el("div", "subcard bi-row" + (partial ? " bi-live" : ""));
    const meta = el("div", "bi-meta");
    meta.appendChild(el("span", "bi-time", fmtTime(p.createdAt)));
    row.appendChild(meta);
    row.appendChild(el("div", "bi-orig", p.original));
    row.appendChild(el("div", "bi-trans", p.translated));
    list.appendChild(row);
    if (partial) livePartialRow = row;
  } else {
    row.querySelector(".bi-orig").textContent = p.original;
    row.querySelector(".bi-trans").textContent = p.translated;
    row.querySelector(".bi-time").textContent = fmtTime(p.createdAt);
  }
  list.scrollTop = list.scrollHeight;
}

function finalizeLiveRow(p) {
  if (livePartialRow) {
    livePartialRow.classList.remove("bi-live");
    livePartialRow.querySelector(".bi-orig").textContent = p.original;
    livePartialRow.querySelector(".bi-trans").textContent = p.translated;
    livePartialRow.querySelector(".bi-time").textContent = fmtTime(p.createdAt);
    livePartialRow = null;
  } else {
    renderLiveRow(p, false);
  }
}

/* ---------------- 运行状态 ---------------- */
export function setLiveRunning(running) {
  state.liveRunning = running;
  const btn = document.getElementById("ltToggle");
  const label = document.getElementById("ltToggleLabel");
  const dot = document.getElementById("ltRecDot");
  if (btn) { btn.classList.toggle("danger", running); btn.classList.toggle("primary", !running); }
  if (label) label.textContent = tr(running ? "lt.stop" : "lt.start");
  if (dot) dot.style.display = running ? "inline-block" : "none";
}

function setStatus(msg, isError) {
  const s = document.getElementById("ltStatus");
  if (s) { s.textContent = msg; s.style.color = isError ? "var(--error)" : ""; }
}

async function toggleLive() {
  if (!invoke) return;
  try {
    if (state.liveRunning) {
      await invoke("stop_live_translation");
    } else {
      const src = document.getElementById("ltSource").value;
      const tgt = document.getElementById("ltTarget").value;
      await invoke("set_languages", { source: src, target: tgt });
      await invoke("start_live_translation");
    }
  } catch (e) {
    setStatus(String(e), true);
    console.error("toggle live failed", e);
  }
}

/* ---------------- 历史 ---------------- */
export async function loadHistory() {
  if (!invoke) return;
  try {
    const rows = await invoke("list_translation_history", { limit: 100, offset: 0 });
    const wrap = document.getElementById("ltHistList");
    const empty = document.getElementById("ltHistEmpty");
    if (!wrap) return;
    wrap.innerHTML = "";
    if (!rows || rows.length === 0) {
      if (empty) empty.style.display = "block";
      return;
    }
    if (empty) empty.style.display = "none";
    for (const r of rows) {
      const item = el("div", "subcard hist-row");
      item.appendChild(el("div", "hist-ts", fmtTime(r.createdAt)));
      item.appendChild(el("div", "hist-trans", r.translatedText || ""));
      item.appendChild(el("div", "hist-orig", r.sourceText || ""));
      wrap.appendChild(item);
    }
  } catch (e) {
    console.error("load history failed", e);
  }
}

async function clearHistory() {
  if (!invoke) return;
  try {
    await invoke("clear_translation_history");
    await loadHistory();
  } catch (e) {
    console.error("clear history failed", e);
  }
}

/* ---------------- 启动期：套用配置 ---------------- */
export function applyLiveConfig(cfg) {
  const src = cfg?.general?.defaultSourceLang || "en-US";
  const tgt = cfg?.general?.defaultTargetLang || "zh-Hans";
  fillSelect(document.getElementById("ltSource"), SOURCE_LANGS, src);
  fillSelect(document.getElementById("ltTarget"), TARGET_LANGS, tgt);
}

/* ---------------- 初始化 ---------------- */
export function initLive() {
  // 右侧 Tab：历史 / 洞察
  document.querySelectorAll(".tab-btn[data-lttab]").forEach(b =>
    b.addEventListener("click", () => {
      document.querySelectorAll(".tab-btn[data-lttab]").forEach(x => x.classList.remove("active"));
      b.classList.add("active");
      document.getElementById("lttab-hist").style.display = b.dataset.lttab === "hist" ? "block" : "none";
      document.getElementById("lttab-insight").style.display = b.dataset.lttab === "insight" ? "block" : "none";
      if (b.dataset.lttab === "hist") loadHistory();
    }));

  // 原文/译文/双语
  document.querySelectorAll("#ltViewMode .seg-btn").forEach(b =>
    b.addEventListener("click", () => {
      b.parentElement.querySelectorAll(".seg-btn").forEach(x => x.classList.remove("active"));
      b.classList.add("active");
      const list = document.getElementById("ltList");
      if (list) list.dataset.mode = b.dataset.mode;
    }));

  // 开始/停止
  const toggle = document.getElementById("ltToggle");
  if (toggle) toggle.addEventListener("click", toggleLive);

  // 资源下拉切换 → 激活（逻辑在 settings.js）
  const resSel = document.getElementById("ltResource");
  if (resSel) resSel.addEventListener("change", () => activateRes(resSel.value));

  // 历史按钮
  const clearBtn = document.getElementById("ltClearHist");
  if (clearBtn) clearBtn.addEventListener("click", clearHistory);
  const refreshBtn = document.getElementById("ltRefreshHist");
  if (refreshBtn) refreshBtn.addEventListener("click", loadHistory);

  // 语言切换 → 重渲染按钮文案
  document.addEventListener("app:langchange", () => setLiveRunning(state.liveRunning));

  // 切回实时翻译视图 → 刷新历史
  document.addEventListener("app:viewchange", (e) => {
    if (e.detail?.view === "live") loadHistory();
  });

  bindLiveEvents();
}

/* ---------------- 后端事件 ---------------- */
async function bindLiveEvents() {
  if (!listen) return;
  await listen("live:partial", (e) => renderLiveRow(e.payload, true));
  await listen("live:final", (e) => finalizeLiveRow(e.payload));
  await listen("live:status", (e) => {
    const { state: st, message } = e.payload || {};
    switch (st) {
      case "started":
        setLiveRunning(true);
        setStatus(tr("lt.statusRunning"));
        break;
      case "stopped":
        setLiveRunning(false);
        setStatus(tr("lt.statusIdle"));
        livePartialRow = null;
        loadHistory();
        break;
      case "error":
        setLiveRunning(false);
        setStatus(message || "error", true);
        break;
      case "sessionStarted":
        setStatus(tr("lt.statusRunning"));
        break;
      default:
        if (message) setStatus(message);
    }
  });
}
