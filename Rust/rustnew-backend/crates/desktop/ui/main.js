/* ============================================================
   main.js — 入口胶水
   只负责「装配各模块 + 启动编排」，不写业务逻辑。
   ============================================================ */

import { invoke, setTheme, setLang, initCore } from "./core.js";
import { initLive, applyLiveConfig, setLiveRunning, loadHistory } from "./live.js";
import { initSettings, applySettingsConfig, loadSpeechResources, loadAiEndpoints } from "./settings.js";
import { initAudioPanel, loadAudioPanel } from "./audiopanel.js";

async function boot() {
  // 1. 绑定各模块自身的 DOM 事件
  initCore();
  initLive();
  initSettings();
  initAudioPanel();

  // 2. 读取后端配置
  let cfg = null;
  if (invoke) {
    try {
      cfg = await invoke("load_config");
    } catch (e) {
      console.error("load_config failed", e);
    }
  }

  // 3. 套用配置（不回写，避免无意义落盘）
  setTheme(cfg?.general?.theme || "light", false);
  setLang(cfg?.general?.language || "zh", false);
  applyLiveConfig(cfg);
  applySettingsConfig(cfg);

  // 4. 拉取动态数据
  await loadSpeechResources();
  await loadAiEndpoints();
  await loadAudioPanel();
  await loadHistory();

  // 5. 同步实时翻译运行态
  if (invoke) {
    try { setLiveRunning(await invoke("is_live_running")); } catch (e) { /* ignore */ }
  }
}

document.addEventListener("DOMContentLoaded", boot);
