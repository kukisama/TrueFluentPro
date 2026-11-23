// ============================================================================
// settings-view.js — 设置页签（支持 21V 与 Global 传统微软 Speech 终结点、声音、断句与 TSF 管理）
// ============================================================================

import { invokeCmd, escapeHtml } from './api.js';
import { createAutosave } from './autosave.js';

export function createSettingsController() {
  let selectedSheet = 0; // 0:语音终结点 1:识别与声音 2:断句 3:输入法管理
  let snapshot = null;
  let editingSubId = null;
  let microphones = null;
  let loadingMics = false;
  let checkingInstall = null;
  let suppressGeneralEvents = false;
  let pendingInstallMode = null;

  const statusText = () => document.getElementById('settings-save-status');
  const autosave = createAutosave(saveGeneralImmediate, (err) => { statusText().textContent = `保存失败：${err}`; });

  function bindEvents() {
    document.querySelectorAll('[data-settings-sheet]').forEach((btn) => {
      btn.addEventListener('click', () => {
        selectedSheet = Number(btn.dataset.settingsSheet);
        syncSheets();
        if (selectedSheet === 1 && !microphones) void loadMicrophones();
        if (selectedSheet === 3) void refreshInstallStatus();
      });
    });

    // 终结点快速填入预设按钮（用户给出的两个典型 21V 与 Global 地址）
    document.getElementById('preset-21v-btn').addEventListener('click', () => {
      const epInput = document.getElementById('sub-endpoint-input');
      const nameInput = document.getElementById('sub-name-input');
      if (!nameInput.value.trim()) nameInput.value = '微软语音 · 中国区 21V';
      epInput.value = 'https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken';
      previewEndpoint();
    });

    document.getElementById('preset-global-btn').addEventListener('click', () => {
      const epInput = document.getElementById('sub-endpoint-input');
      const nameInput = document.getElementById('sub-name-input');
      if (!nameInput.value.trim()) nameInput.value = '微软语音 · 国际版 Global';
      epInput.value = 'https://southeastasia.api.cognitive.microsoft.com';
      previewEndpoint();
    });

    document.getElementById('sub-endpoint-input').addEventListener('input', previewEndpoint);
    document.getElementById('sub-reset-btn').addEventListener('click', clearSubEditor);
    document.getElementById('sub-save-btn').addEventListener('click', saveSubscription);
    document.getElementById('sub-test-btn').addEventListener('click', (event) => testSubscription(event.currentTarget));
    document.getElementById('sub-import-legacy-btn').addEventListener('click', importLegacy);

    // 识别与声音 / 断句 自动保存控件
    document.getElementById('active-sub-select').addEventListener('change', onGeneralEdited);
    document.getElementById('mic-device-select').addEventListener('change', onGeneralEdited);
    document.getElementById('enable-prewarm-toggle').addEventListener('change', onGeneralEdited);
    document.getElementById('end-silence-input').addEventListener('change', onGeneralEdited);
    document.getElementById('filter-particles-toggle').addEventListener('change', onGeneralEdited);

    document.getElementById('refresh-subs-btn').addEventListener('click', () => loadAll());
    document.getElementById('refresh-mics-btn').addEventListener('click', loadMicrophones);

    // 系统 TSF 安装管理与 UAC 确认弹窗
    document.getElementById('ime-install-btn').addEventListener('click', () => runInstall(true, false));
    document.getElementById('ime-uninstall-btn').addEventListener('click', () => runInstall(false, false));
    document.getElementById('ime-status-btn').addEventListener('click', () => refreshInstallStatus());

    document.getElementById('uac-cancel-btn').addEventListener('click', closeUacModal);
    document.getElementById('uac-confirm-btn').addEventListener('click', async () => {
      const mode = pendingInstallMode;
      closeUacModal();
      if (mode !== null) {
        await runInstall(mode, true);
      }
    });

    syncSheets();
  }

  function syncSheets() {
    document.querySelectorAll('[data-settings-sheet]').forEach((btn) => {
      btn.classList.toggle('accent', Number(btn.dataset.settingsSheet) === selectedSheet);
    });
    document.querySelectorAll('[data-settings-pane]').forEach((pane) => {
      pane.style.display = Number(pane.dataset.settingsPane) === selectedSheet ? 'block' : 'none';
    });
  }

  async function previewEndpoint() {
    const raw = document.getElementById('sub-endpoint-input').value;
    const hintEl = document.getElementById('sub-region-hint');
    try {
      const res = await invokeCmd('preview_endpoint_url', { endpoint: raw });
      hintEl.textContent = res.hintText;
      hintEl.style.color = res.valid ? 'var(--blue)' : raw.trim() ? 'var(--error-ink)' : 'var(--muted)';
    } catch (err) {
      hintEl.textContent = String(err);
    }
  }

  async function loadAll() {
    try {
      await autosave.flush();
      const snap = await invokeCmd('load_settings_page');
      if (autosave.dirty) return;
      applySnapshot(snap);
      statusText().textContent = '已加载当前配置（修改后自动保存）';
    } catch (err) {
      statusText().textContent = `加载配置失败：${err}`;
    }
    if (selectedSheet === 1 && !microphones) void loadMicrophones();
    if (selectedSheet === 3) void refreshInstallStatus();
  }

  async function loadMicrophones() {
    if (loadingMics) return;
    loadingMics = true;
    const select = document.getElementById('mic-device-select');
    const status = document.getElementById('mic-count-status');
    select.disabled = true;
    status.textContent = '正在检测麦克风…';
    try {
      microphones = await invokeCmd('list_input_devices');
      renderMicrophones(select.value || snapshot?.microphoneDeviceId || '');
    } catch (err) {
      status.textContent = `检测麦克风失败：${err}`;
    } finally {
      loadingMics = false;
      select.disabled = false;
    }
  }

  function renderMicrophones(savedId) {
    const devices = [...(microphones ?? [{ deviceId: '', displayName: '系统默认麦克风' }])];
    if (savedId && !devices.some((item) => item.deviceId === savedId)) {
      devices.push({ deviceId: savedId, displayName: microphones ? `${savedId}（当前未连接）` : savedId });
    }
    const select = document.getElementById('mic-device-select');
    select.innerHTML = devices.map((m) => `<option value="${escapeHtml(m.deviceId)}">${escapeHtml(m.displayName)}</option>`).join('');
    select.value = savedId;
    if (microphones) document.getElementById('mic-count-status').textContent = `已检测到 ${microphones.length - 1} 个物理输入设备。`;
  }

  function applySnapshot(snap) {
    snapshot = snap;
    suppressGeneralEvents = true;

    // 1. 订阅列表渲染
    const listEl = document.getElementById('subscriptions-list');
    if (snap.subscriptions.length === 0) {
      listEl.innerHTML = `<div class="metric-note" style="padding:12px 4px">尚未添加微软 Speech 终结点。请在上方表单填入中国区 21V 或国际版 Global 终结点与密钥后保存。</div>`;
    } else {
      listEl.innerHTML = snap.subscriptions
        .map((s) => {
          const isActive = s.id === snap.activeSubscriptionId;
          const isChina = s.cloudLabel.includes('21V');
          return `
            <div class="endpoint-item ${isActive ? 'active-sub' : ''}">
              <div style="min-width:0;flex:1">
                <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap">
                  <span style="font-weight:600">${escapeHtml(s.name)}</span>
                  <span class="cloud-pill ${isChina ? 'china' : ''}">${escapeHtml(s.cloudLabel)} · ${escapeHtml(s.region)}</span>
                  ${isActive ? '<span class="cloud-pill">当前使用</span>' : ''}
                </div>
                <div class="metric-note" style="margin-top:4px;word-break:break-all">${escapeHtml(s.endpoint)} · 密钥 ${escapeHtml(s.maskedKey)}</div>
              </div>
              <div style="display:flex;gap:6px;flex-shrink:0">
                ${!isActive ? `<button type="button" data-use-sub="${escapeHtml(s.id)}" style="min-height:32px;padding:4px 10px;font-size:12px">启用</button>` : ''}
                <button type="button" data-test-sub="${escapeHtml(s.id)}" style="min-height:32px;padding:4px 10px;font-size:12px">验证</button>
                <button type="button" data-edit-sub="${escapeHtml(s.id)}" style="min-height:32px;padding:4px 10px;font-size:12px">编辑</button>
                <button type="button" data-del-sub="${escapeHtml(s.id)}" style="min-height:32px;padding:4px 10px;font-size:12px">删除</button>
              </div>
            </div>
          `;
        })
        .join('');

      listEl.querySelectorAll('[data-use-sub]').forEach((btn) => {
        btn.addEventListener('click', () => {
          document.getElementById('active-sub-select').value = btn.dataset.useSub;
          onGeneralEdited();
        });
      });
      listEl.querySelectorAll('[data-edit-sub]').forEach((btn) => {
        btn.addEventListener('click', () => loadSubIntoEditor(btn.dataset.editSub));
      });
      listEl.querySelectorAll('[data-test-sub]').forEach((btn) => {
        btn.addEventListener('click', () => testSubscription(btn, btn.dataset.testSub));
      });
      listEl.querySelectorAll('[data-del-sub]').forEach((btn) => {
        btn.addEventListener('click', () => removeSubscription(btn.dataset.delSub));
      });
    }

    // 2. 识别页签下拉框与麦克风
    const activeSubSelect = document.getElementById('active-sub-select');
    if (snap.subscriptions.length === 0) {
      activeSubSelect.innerHTML = `<option value="">尚未添加语音终结点</option>`;
    } else {
      activeSubSelect.innerHTML = snap.subscriptions
        .map((s) => `<option value="${escapeHtml(s.id)}">${escapeHtml(s.displayTitle)}</option>`)
        .join('');
      activeSubSelect.value = snap.activeSubscriptionId;
    }

    renderMicrophones(snap.microphoneDeviceId);

    document.getElementById('enable-prewarm-toggle').checked = Boolean(snap.enablePrewarm);
    document.getElementById('end-silence-input').value = Number(snap.endSilenceTimeoutSeconds).toFixed(1);
    document.getElementById('filter-particles-toggle').checked = Boolean(snap.filterModalParticles);

    const legacyBtn = document.getElementById('sub-import-legacy-btn');
    legacyBtn.style.display = snap.canImportLegacy ? 'inline-flex' : 'none';

    suppressGeneralEvents = false;
  }

  function loadSubIntoEditor(id) {
    const sub = snapshot?.subscriptions.find((s) => s.id === id);
    if (!sub) return;
    editingSubId = sub.id;
    document.getElementById('sub-name-input').value = sub.name;
    document.getElementById('sub-endpoint-input').value = sub.endpoint;
    document.getElementById('sub-key-input').value = '';
    document.getElementById('sub-key-input').placeholder = `留空保持原密钥 (${sub.maskedKey})`;
    document.getElementById('sub-save-btn').textContent = '更新终结点';
    previewEndpoint();
  }

  function clearSubEditor() {
    editingSubId = null;
    document.getElementById('sub-name-input').value = '';
    document.getElementById('sub-endpoint-input').value = '';
    document.getElementById('sub-key-input').value = '';
    document.getElementById('sub-key-input').placeholder = '填写 Azure Speech 订阅密钥（Windows DPAPI 自动加密存储）';
    document.getElementById('sub-save-btn').textContent = '保存终结点';
    document.getElementById('sub-op-feedback').style.display = 'none';
    previewEndpoint();
  }

  function showSubFeedback(msg, kind = 'info') {
    const el = document.getElementById('sub-op-feedback');
    el.style.display = 'block';
    el.className = kind === 'error' ? 'status-banner error' : kind === 'success' ? 'status-banner success' : 'status-banner';
    el.style.margin = '0';
    el.textContent = msg;
  }

  async function saveSubscription() {
    const name = document.getElementById('sub-name-input').value;
    const endpoint = document.getElementById('sub-endpoint-input').value;
    const key = document.getElementById('sub-key-input').value;
    try {
      const snap = await invokeCmd('upsert_speech_subscription', {
        input: {
          id: editingSubId,
          name,
          endpoint,
          subscriptionKey: key,
          isEnabled: true,
        },
      });
      applySnapshot(snap);
      clearSubEditor();
      showSubFeedback('✓ 微软 Speech 终结点已加密保存。', 'success');
      statusText().textContent = '终结点配置已保存';
    } catch (err) {
      showSubFeedback(`✗ ${err}`, 'error');
    }
  }

  async function removeSubscription(id) {
    try {
      const snap = await invokeCmd('delete_speech_subscription', { subscriptionId: id });
      applySnapshot(snap);
      if (editingSubId === id) clearSubEditor();
      statusText().textContent = '已删除指定语音终结点';
    } catch (err) {
      showSubFeedback(`✗ ${err}`, 'error');
    }
  }

  let testingSubscription = false;

  async function testSubscription(btn, listId = null) {
    if (testingSubscription) return;
    const endpoint = document.getElementById('sub-endpoint-input').value;
    const key = document.getElementById('sub-key-input').value;
    const name = document.getElementById('sub-name-input').value;
    const savedId = listId ?? editingSubId ?? (!endpoint.trim() && !key.trim() && !name.trim() ? snapshot?.activeSubscriptionId : null);
    testingSubscription = true;
    btn.disabled = true;
    showSubFeedback('正在请求 STS issueToken 接口验证密钥与区域连通性…');
    try {
      const res = await invokeCmd('test_speech_subscription', {
        savedId: savedId || null,
        endpoint: listId ? '' : endpoint,
        subscriptionKey: listId ? '' : key,
      });
      showSubFeedback(
        res.valid ? `✓ ${res.message}` : `✗ ${res.message}`,
        res.valid ? 'success' : 'error'
      );
    } catch (err) {
      showSubFeedback(`✗ ${err}`, 'error');
    } finally {
      testingSubscription = false;
      btn.disabled = false;
    }
  }

  async function importLegacy() {
    try {
      const res = await invokeCmd('import_legacy_truefluent_subscriptions');
      applySnapshot(res.snapshot);
      showSubFeedback(
        `✓ 已从主程序导入 ${res.summary.importedCount} 条微软 Speech 订阅（跳过重复 ${res.summary.skippedDuplicates} 条）。`,
        'success'
      );
    } catch (err) {
      showSubFeedback(`✗ ${err}`, 'error');
    }
  }

  function onGeneralEdited() {
    if (suppressGeneralEvents) return;
    statusText().textContent = '有未保存的修改 · 修改后自动保存';
    autosave.edited();
  }

  async function saveGeneralImmediate(isCurrent) {
    const rawSilence = Number(document.getElementById('end-silence-input').value);
    const silence = Number.isFinite(rawSilence) ? Math.min(3.0, Math.max(0.2, rawSilence)) : 0.8;
    statusText().textContent = '正在保存…';
    try {
      const snap = await invokeCmd('save_general_settings', {
        update: {
          activeSubscriptionId: document.getElementById('active-sub-select').value || '',
          microphoneDeviceId: document.getElementById('mic-device-select').value || '',
          enablePrewarm: Boolean(document.getElementById('enable-prewarm-toggle').checked),
          endSilenceTimeoutSeconds: silence,
          filterModalParticles: Boolean(document.getElementById('filter-particles-toggle').checked),
        },
      });
      if (!isCurrent()) return;
      applySnapshot(snap);
      const now = new Date().toLocaleTimeString('zh-CN', { hour12: false });
      statusText().textContent = `已自动保存（${now}）`;
    } catch (err) {
      statusText().textContent = `保存失败：${err}`;
      throw err;
    }
  }

  function refreshInstallStatus() {
    checkingInstall ??= loadInstallStatus().finally(() => { checkingInstall = null; });
    return checkingInstall;
  }

  async function loadInstallStatus() {
    const statusLabel = document.getElementById('ime-reg-status');
    const verBadge = document.getElementById('ime-version-label');
    try {
      const st = await invokeCmd('query_install_status');
      statusLabel.textContent = st.statusText;
      if (verBadge) verBadge.textContent = st.versionText;
      const canManage = Boolean(st.isWindows);
      document.getElementById('ime-install-btn').disabled = !canManage;
      document.getElementById('ime-uninstall-btn').disabled = !canManage;
    } catch (err) {
      statusLabel.textContent = `查询安装状态失败：${err}`;
    }
  }

  async function runInstall(install, confirmedElevation) {
    const progress = document.getElementById('ime-progress');
    const opMsg = document.getElementById('ime-op-message');
    progress.style.display = 'block';
    opMsg.textContent = '正在执行输入法组件维护，请稍候…';
    try {
      await autosave.flush();
      const res = await invokeCmd('manage_ime_installation', {
        install,
        confirmedElevation,
      });
      if (res.requiresElevation) {
        pendingInstallMode = install;
        openUacModal(install);
        opMsg.textContent = '';
        return;
      }
      opMsg.textContent = res.message;
      await refreshInstallStatus();
    } catch (err) {
      opMsg.textContent = `操作失败：${err}`;
    } finally {
      progress.style.display = 'none';
    }
  }

  function openUacModal(install) {
    document.getElementById('uac-modal-title').textContent = install
      ? '需要管理员权限安装 / 修复输入法'
      : '需要管理员权限卸载输入法';
    document.getElementById('uac-modal-backdrop').classList.add('open');
  }

  function closeUacModal() {
    pendingInstallMode = null;
    document.getElementById('uac-modal-backdrop').classList.remove('open');
  }

  return {
    init: bindEvents,
    onEnter: () => loadAll(),
    reloadAfterLanguageSync: () => loadAll(),
    flushPending: autosave.flush,
  };
}
