// ============================================================================
// api.js — Tauri 2 IPC 桥接层（单一职责：隔离底层 invoke 与窗口控制）
// ============================================================================

const hasTauri = () => Boolean(window.__TAURI_INTERNALS__);

export async function invokeCmd(cmd, args = {}) {
  if (hasTauri()) {
    return window.__TAURI_INTERNALS__.invoke(cmd, args);
  }
  throw new Error(`Tauri IPC 未就绪: ${cmd}`);
}

export async function minimizeWindow() {
  if (hasTauri()) {
    await window.__TAURI_INTERNALS__.invoke('plugin:window|minimize', { label: 'main' });
  }
}

export async function toggleMaximizeWindow() {
  if (hasTauri()) {
    await window.__TAURI_INTERNALS__.invoke('plugin:window|toggle_maximize', { label: 'main' });
  }
}

export async function closeWindow() {
  if (hasTauri()) {
    await window.__TAURI_INTERNALS__.invoke('plugin:window|close', { label: 'main' });
  } else {
    window.close();
  }
}

export async function startWindowDrag() {
  if (hasTauri()) {
    await window.__TAURI_INTERNALS__.invoke('plugin:window|start_dragging', { label: 'main' });
  }
}

export function formatNumber(n) {
  return Number(n || 0).toLocaleString('zh-CN');
}

export function formatDuration(ms) {
  const value = Number(ms || 0);
  const seconds = Math.floor(value / 1000);
  if (value > 0 && seconds === 0) return '< 1 秒';
  if (seconds < 60) return `${seconds} 秒`;
  if (seconds < 3600) {
    const m = Math.floor(seconds / 60);
    const s = String(seconds % 60).padStart(2, '0');
    return `${m} 分 ${s} 秒`;
  }
  const h = Math.floor(seconds / 3600);
  const m = String(Math.floor((seconds % 3600) / 60)).padStart(2, '0');
  return `${h} 时 ${m} 分`;
}

export function escapeHtml(str) {
  return String(str ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}
