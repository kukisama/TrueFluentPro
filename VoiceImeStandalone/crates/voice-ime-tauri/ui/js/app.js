// ============================================================================
// app.js — 统一工具外壳导航、离页防抖落盘与无边框窗口拖拽/最大化/关闭管理
// ============================================================================

import {
  invokeCmd,
  minimizeWindow,
  toggleMaximizeWindow,
  closeWindow,
  startWindowDrag,
} from './api.js';
import { createUsageController } from './usage-view.js';
import { createLanguageController } from './language-view.js';
import { createSettingsController } from './settings-view.js';

let currentTab = null;
let navigating = false;

const settingsCtrl = createSettingsController();
const usageCtrl = createUsageController();
const languageCtrl = createLanguageController(() => {
  // 语言页保存后同步刷新设置页引用快照，防止覆盖
  settingsCtrl.reloadAfterLanguageSync();
});

const controllers = {
  usage: usageCtrl,
  language: languageCtrl,
  settings: settingsCtrl,
};

function showGlobalNotice(text) {
  const el = document.getElementById('global-sheet-notice');
  if (!text) {
    el.style.display = 'none';
  } else {
    el.textContent = text;
    el.style.display = 'block';
  }
}

async function navigateTo(targetTab) {
  if (navigating || targetTab === currentTab || !controllers[targetTab]) return;
  navigating = true;
  showGlobalNotice('');
  try {
    if (currentTab && controllers[currentTab]) {
      await controllers[currentTab].flushPending();
    }
    currentTab = targetTab;
    document.querySelectorAll('[data-main-tab]').forEach((btn) => {
      btn.classList.toggle('accent', btn.dataset.mainTab === targetTab);
    });
    document.querySelectorAll('[data-tab-panel]').forEach((panel) => {
      panel.classList.toggle('active', panel.dataset.tabPanel === targetTab);
    });
    await controllers[targetTab].onEnter();
  } catch (err) {
    showGlobalNotice(`设置尚未保存或页面加载未完成，请在当前页重试（${err.message || err}）`);
  } finally {
    navigating = false;
  }
}

async function handleGracefulClose() {
  try {
    for (const ctrl of Object.values(controllers)) {
      await ctrl.flushPending();
    }
    await closeWindow();
  } catch (err) {
    showGlobalNotice(`存在尚未保存的更改，请处理后再关闭窗口（${err.message || err}）`);
  }
}

window.addEventListener('DOMContentLoaded', async () => {
  usageCtrl.init();
  languageCtrl.init();
  settingsCtrl.init();

  document.querySelectorAll('[data-main-tab]').forEach((btn) => {
    btn.addEventListener('click', () => navigateTo(btn.dataset.mainTab));
  });

  // 自定义标题栏拖拽与窗口控制
  const dragArea = document.getElementById('title-drag-area');
  dragArea.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;
    if (e.detail === 2) {
      toggleMaximizeWindow();
    } else {
      startWindowDrag();
    }
  });

  document.getElementById('win-min-btn').addEventListener('click', minimizeWindow);
  document.getElementById('win-max-btn').addEventListener('click', toggleMaximizeWindow);
  document.getElementById('win-close-btn').addEventListener('click', handleGracefulClose);

  let initial = 'settings';
  try {
    initial = await invokeCmd('get_initial_tab');
  } catch {
    initial = 'settings';
  }
  await navigateTo(initial);
});
