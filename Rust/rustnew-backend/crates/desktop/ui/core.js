/* ============================================================
   core.js — 共享底座
   - Tauri 句柄、全局可变状态
   - DOM 小工具（el / fmtTime / fillSelect / tr）
   - 主题 / 语言切换（向外广播 app:langchange）
   core 不依赖 live/settings，跨模块通信一律走 document 自定义事件，避免循环依赖。
   ============================================================ */

import { I18N, applyLang, applyTheme } from "./i18n.js";

const tauriGlobal = window.__TAURI__;
export const invoke = tauriGlobal ? tauriGlobal.core.invoke : null;
export const listen = tauriGlobal && tauriGlobal.event ? tauriGlobal.event.listen : null;

/** 全局可变状态（用对象持有，跨模块可改） */
export const state = {
  theme: "light",
  lang: "zh",
  liveRunning: false,
};

/* ---------------- i18n 取词 ---------------- */
export function tr(key) {
  return (I18N[state.lang] && I18N[state.lang][key]) || key;
}

/* ---------------- DOM 工具 ---------------- */
export function el(tag, cls, text) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text != null) n.textContent = text;
  return n;
}

export function fmtTime(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return d.toLocaleTimeString("zh-CN", { hour12: false });
}

export function fillSelect(sel, items, current) {
  if (!sel) return;
  sel.innerHTML = "";
  for (const it of items) {
    const opt = document.createElement("option");
    opt.value = it.v;
    if (it.k) { opt.dataset.i18n = it.k; opt.textContent = tr(it.k); }
    else opt.textContent = it.t || it.v;
    sel.appendChild(opt);
  }
  if (current) sel.value = current;
}

/* ---------------- 主题 / 语言 ---------------- */
async function persistPrefs() {
  if (!invoke) return;
  try {
    await invoke("set_ui_prefs", { theme: state.theme, language: state.lang });
  } catch (e) {
    console.error("persist prefs failed", e);
  }
}

export function setTheme(theme, persist = true) {
  state.theme = theme;
  applyTheme(theme);
  const sel = document.getElementById("setTheme");
  if (sel) sel.value = theme;
  if (persist) persistPrefs();
}

export function setLang(lang, persist = true) {
  state.lang = lang;
  applyLang(lang);
  const sel = document.getElementById("setUiLang");
  if (sel) sel.value = lang;
  // 广播：依赖 i18n 的动态文本由各模块自行重渲染
  document.dispatchEvent(new CustomEvent("app:langchange"));
  if (persist) persistPrefs();
}

/* ---------------- 视图切换底座 ---------------- */
export function initCore() {
  // 明暗切换
  const themeNavBtn = document.getElementById("themeNavBtn");
  if (themeNavBtn) {
    themeNavBtn.addEventListener("click", () =>
      setTheme(state.theme === "light" ? "dark" : "light"));
  }

  // 视图切换 → 广播 app:viewchange
  document.querySelectorAll(".nav-btn[data-view]").forEach(b =>
    b.addEventListener("click", () => {
      document.querySelectorAll(".nav-btn[data-view]").forEach(x => x.classList.remove("selected"));
      b.classList.add("selected");
      document.querySelectorAll(".view").forEach(v => v.classList.remove("active"));
      const target = document.getElementById("view-" + b.dataset.view);
      if (target) target.classList.add("active");
      document.dispatchEvent(new CustomEvent("app:viewchange", { detail: { view: b.dataset.view } }));
    }));

  // 导航折叠
  const navToggle = document.getElementById("navToggle");
  if (navToggle) {
    navToggle.addEventListener("click", () =>
      document.getElementById("navRail").classList.toggle("expanded"));
  }

  // 设置页：界面语言 / 主题
  const uiLang = document.getElementById("setUiLang");
  if (uiLang) uiLang.addEventListener("change", () => setLang(uiLang.value));
  const setThemeSel = document.getElementById("setTheme");
  if (setThemeSel) setThemeSel.addEventListener("change", () => setTheme(setThemeSel.value));
}
