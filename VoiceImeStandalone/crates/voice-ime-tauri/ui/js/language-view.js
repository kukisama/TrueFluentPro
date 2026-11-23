// ============================================================================
// language-view.js — 语言与翻译页签（1:1 复刻 VoiceImeLanguagePage，200ms 自动保存）
// ============================================================================

import { invokeCmd, escapeHtml } from './api.js';
import { createAutosave } from './autosave.js';

export function createLanguageController(onLanguageSavedCallback) {
  let suppressEvents = false;

  const srcSelect = () => document.getElementById('lang-source-select');
  const transToggle = () => document.getElementById('lang-enable-translation');
  const tgtSelect = () => document.getElementById('lang-target-select');
  const statusEl = () => document.getElementById('lang-save-status');
  const retryBtn = () => document.getElementById('lang-retry-save-btn');
  const saveFailed = (err) => {
    statusEl().textContent = `保存失败：${err}`;
    retryBtn().style.display = 'inline-flex';
  };
  const autosave = createAutosave(saveImmediate, saveFailed);

  function bindEvents() {
    srcSelect().addEventListener('change', onEdited);
    transToggle().addEventListener('change', () => {
      tgtSelect().disabled = !transToggle().checked;
      onEdited();
    });
    tgtSelect().addEventListener('change', onEdited);

    document.getElementById('lang-reload-btn').addEventListener('click', () => load());
    retryBtn().addEventListener('click', () => autosave.flush().catch(saveFailed));
  }

  function onEdited() {
    if (suppressEvents) return;
    retryBtn().style.display = 'none';
    statusEl().textContent = '有修改 · 修改后自动保存，下次录音生效';
    autosave.edited();
  }

  async function load() {
    try { await autosave.flush(); } catch (err) { saveFailed(err); return; }
    try {
      const snap = await invokeCmd('load_language_page');
      if (autosave.dirty) return;
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

      retryBtn().style.display = 'none';
      statusEl().textContent = '已加载已保存设置 · 修改后自动保存，下次录音生效';
    } catch (err) {
      suppressEvents = false;
      statusEl().textContent = `读取语言配置失败：${err}`;
    }
  }

  async function saveImmediate(isCurrent) {
    const draft = {
      recognitionLanguage: srcSelect().value || 'auto',
      enableTranslation: Boolean(transToggle().checked),
      targetLanguage: tgtSelect().value || 'en-US',
    };
    statusEl().textContent = '正在保存…';
    try {
      await invokeCmd('save_language_page', { draft });
      if (!isCurrent()) return;
      retryBtn().style.display = 'none';
      const now = new Date().toLocaleTimeString('zh-CN', { hour12: false });
      statusEl().textContent = `已自动保存（${now}）· 下次录音生效`;
      if (onLanguageSavedCallback) onLanguageSavedCallback(draft);
    } catch (err) {
      saveFailed(err);
      throw err;
    }
  }

  return {
    init: bindEvents,
    onEnter: () => load(),
    flushPending: autosave.flush,
  };
}
