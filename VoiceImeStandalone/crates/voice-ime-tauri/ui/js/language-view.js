// ============================================================================
// language-view.js — 语言与翻译页签（1:1 复刻 VoiceImeLanguagePage，200ms 自动保存）
// ============================================================================

import { invokeCmd, escapeHtml } from './api.js';

export function createLanguageController(onLanguageSavedCallback) {
  let dirty = false;
  let saving = false;
  let debounceTimer = null;
  let suppressEvents = false;

  const srcSelect = () => document.getElementById('lang-source-select');
  const transToggle = () => document.getElementById('lang-enable-translation');
  const tgtSelect = () => document.getElementById('lang-target-select');
  const statusEl = () => document.getElementById('lang-save-status');
  const retryBtn = () => document.getElementById('lang-retry-save-btn');

  function bindEvents() {
    srcSelect().addEventListener('change', onEdited);
    transToggle().addEventListener('change', () => {
      tgtSelect().disabled = !transToggle().checked;
      onEdited();
    });
    tgtSelect().addEventListener('change', onEdited);

    document.getElementById('lang-reload-btn').addEventListener('click', () => load());
    retryBtn().addEventListener('click', () => saveImmediate());
  }

  function onEdited() {
    if (suppressEvents) return;
    dirty = true;
    retryBtn().style.display = 'none';
    statusEl().textContent = '有修改 · 修改后自动保存，下次录音生效';
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      debounceTimer = null;
      saveImmediate();
    }, 200);
  }

  async function load() {
    if (debounceTimer) {
      clearTimeout(debounceTimer);
      debounceTimer = null;
    }
    try {
      const snap = await invokeCmd('load_language_page');
      suppressEvents = true;
      srcSelect().innerHTML = snap.sourceLanguages
        .map((o) => `<option value="${escapeHtml(o.code)}">${escapeHtml(o.name)}</option>`)
        .join('');
      tgtSelect().innerHTML = snap.targetLanguages
        .map((o) => `<option value="${escapeHtml(o.code)}">${escapeHtml(o.name)}</option>`)
        .join('');
      srcSelect().value = snap.recognitionLanguage;
      transToggle().checked = Boolean(snap.enableTranslation);
      tgtSelect().value = snap.targetLanguage;
      tgtSelect().disabled = !snap.enableTranslation;
      suppressEvents = false;

      dirty = false;
      retryBtn().style.display = 'none';
      statusEl().textContent = '已加载已保存设置 · 修改后自动保存，下次录音生效';
    } catch (err) {
      suppressEvents = false;
      statusEl().textContent = `读取语言配置失败：${err}`;
    }
  }

  async function saveImmediate() {
    if (saving || !dirty) return;
    if (debounceTimer) {
      clearTimeout(debounceTimer);
      debounceTimer = null;
    }
    saving = true;
    const draft = {
      recognitionLanguage: srcSelect().value || 'auto',
      enableTranslation: Boolean(transToggle().checked),
      targetLanguage: tgtSelect().value || 'en-US',
    };
    statusEl().textContent = '正在保存…';
    try {
      await invokeCmd('save_language_page', { draft });
      dirty = false;
      retryBtn().style.display = 'none';
      const now = new Date().toLocaleTimeString('zh-CN', { hour12: false });
      statusEl().textContent = `已自动保存（${now}）· 下次录音生效`;
      if (onLanguageSavedCallback) onLanguageSavedCallback(draft);
    } catch (err) {
      statusEl().textContent = `保存失败：${err}`;
      retryBtn().style.display = 'inline-flex';
    } finally {
      saving = false;
    }
  }

  return {
    init: bindEvents,
    onEnter: () => load(),
    flushPending: async () => {
      if (dirty) await saveImmediate();
      if (dirty) throw new Error('语言设置尚未完成保存');
    },
  };
}
