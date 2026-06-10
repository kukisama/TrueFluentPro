/* ============================================================
   settings.js — 设置视图 + 语音资源
   - 语音资源列表/新增/编辑/删除/激活
   - 同时驱动实时翻译工具栏的资源下拉（#ltResource）与指示灯
   - 过滤语气词开关（展示用，写入随资源/配置保存逻辑后续接）
   ============================================================ */

import { invoke, state, tr, el } from "./core.js";

let speechResources = [];
let activeResId = "";

/* ---------------- 数据加载 ---------------- */
export async function loadSpeechResources() {
  if (!invoke) return;
  try {
    speechResources = (await invoke("list_speech_resources")) || [];
    activeResId = (await invoke("active_speech_resource_id")) || "";
  } catch (e) {
    console.error("load speech resources failed", e);
    speechResources = [];
  }
  renderResList();
  renderResSelector();
}

/* ---------------- 设置页：资源卡片列表 ---------------- */
function renderResList() {
  const list = document.getElementById("setResList");
  const empty = document.getElementById("setResEmpty");
  if (!list) return;
  list.innerHTML = "";
  if (speechResources.length === 0) {
    if (empty) empty.style.display = "block";
    return;
  }
  if (empty) empty.style.display = "none";
  for (const r of speechResources) {
    const item = el("div", "set-res-item" + (r.id === activeResId ? " active" : ""));
    const info = el("div");
    info.appendChild(el("div", "ri-name", r.name || "(未命名)"));
    info.appendChild(el("div", "ri-meta", r.serviceRegion || r.endpoint || "—"));
    item.appendChild(info);

    const actions = el("div", "ri-actions");
    if (r.id !== activeResId) {
      const useBtn = el("button", "btn sm", tr("set.activate"));
      useBtn.addEventListener("click", () => activateRes(r.id));
      actions.appendChild(useBtn);
    } else {
      actions.appendChild(el("span", "lbl-sm", tr("set.inUse")));
    }
    const editBtn = el("button", "btn sm", tr("set.edit"));
    editBtn.addEventListener("click", () => openResForm(r));
    actions.appendChild(editBtn);
    const delBtn = el("button", "btn sm danger", tr("set.delete"));
    delBtn.addEventListener("click", () => deleteRes(r.id));
    actions.appendChild(delBtn);
    item.appendChild(actions);
    list.appendChild(item);
  }
}

/* ---------------- 实时翻译工具栏：资源下拉 ---------------- */
function renderResSelector() {
  const sel = document.getElementById("ltResource");
  if (!sel) return;
  sel.innerHTML = "";
  if (speechResources.length === 0) {
    const opt = document.createElement("option");
    opt.value = "";
    opt.textContent = tr("lt.noRes");
    sel.appendChild(opt);
    sel.disabled = true;
    updateResLamp();
    return;
  }
  sel.disabled = false;
  for (const r of speechResources) {
    const opt = document.createElement("option");
    opt.value = r.id;
    opt.textContent = r.name || "(未命名)";
    sel.appendChild(opt);
  }
  sel.value = activeResId;
  updateResLamp();
}

function updateResLamp() {
  const lamp = document.getElementById("ltResLamp");
  if (!lamp) return;
  const r = speechResources.find(x => x.id === activeResId);
  const ok = r && r.subscriptionKey && (r.serviceRegion || r.endpoint);
  lamp.className = "lamp " + (ok ? "green" : "red");
  lamp.title = ok ? "" : tr("lt.resInvalid");
}

/* ---------------- 增删改激活 ---------------- */
export async function activateRes(id) {
  if (!invoke || !id) return;
  try {
    await invoke("set_active_speech_resource", { id });
    activeResId = id;
    renderResList();
    renderResSelector();
  } catch (e) {
    console.error("activate res failed", e);
  }
}

async function deleteRes(id) {
  if (!invoke) return;
  try {
    await invoke("delete_speech_resource", { id });
    await loadSpeechResources();
  } catch (e) {
    console.error("delete res failed", e);
  }
}

function openResForm(r) {
  const form = document.getElementById("setResForm");
  form.style.display = "block";
  document.getElementById("resId").value = r ? r.id : "";
  document.getElementById("resName").value = r ? (r.name || "") : "";
  document.getElementById("resKey").value = r ? (r.subscriptionKey || "") : "";
  document.getElementById("resRegion").value = r ? (r.serviceRegion || "") : "";
  document.getElementById("resEndpoint").value = r ? (r.endpoint || "") : "";
}

function closeResForm() {
  document.getElementById("setResForm").style.display = "none";
}

async function saveRes() {
  if (!invoke) return;
  const resource = {
    id: document.getElementById("resId").value || "",
    name: document.getElementById("resName").value.trim(),
    subscriptionKey: document.getElementById("resKey").value.trim(),
    serviceRegion: document.getElementById("resRegion").value.trim(),
    endpoint: document.getElementById("resEndpoint").value.trim(),
  };
  if (!resource.name) resource.name = resource.serviceRegion || "语音资源";
  try {
    await invoke("save_speech_resource", { resource });
    closeResForm();
    await loadSpeechResources();
  } catch (e) {
    console.error("save res failed", e);
  }
}

/* ---------------- 启动期：套用配置 ---------------- */
export function applySettingsConfig(cfg) {
  const fp = document.getElementById("setFilterParticles");
  if (fp) fp.checked = cfg?.general?.filterModalParticles !== false;
}

/* ---------------- 初始化 ---------------- */
export function initSettings() {
  const addBtn = document.getElementById("setAddRes");
  if (addBtn) addBtn.addEventListener("click", () => openResForm(null));
  const saveBtn = document.getElementById("resSave");
  if (saveBtn) saveBtn.addEventListener("click", saveRes);
  const cancelBtn = document.getElementById("resCancel");
  if (cancelBtn) cancelBtn.addEventListener("click", closeResForm);

  // 语言切换 → 重渲染依赖 i18n 的动态文本
  document.addEventListener("app:langchange", () => {
    renderResList();
    renderResSelector();
  });
}
