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

/* ---------------- AI 终结点 ---------------- */
// 承载厂商资料包：选厂商 → 套默认模板 → 填地址密钥 → 保存。
let aiEndpoints = [];
let epProfiles = [];
let currentEp = null;

const KIND_LABELS = {
  OpenAiCompatible: "OpenAI Compatible",
  AzureOpenAi: "Azure OpenAI",
  ApiManagementGateway: "APIM 网关",
  AzureSpeech: "Azure Speech",
};
const PROTO_LABELS = {
  Auto: "自动",
  ChatCompletionsV1: "/v1/chat/completions",
  ChatCompletionsRaw: "chat/completions",
  Responses: "/responses",
};

function kindLabel(kind) {
  return KIND_LABELS[kind] || kind || "";
}
function profileName(pid) {
  const p = epProfiles.find(x => x.id === pid);
  return p ? p.displayName : "";
}

export async function loadAiEndpoints() {
  if (!invoke) return;
  try {
    aiEndpoints = (await invoke("list_ai_endpoints")) || [];
    epProfiles = (await invoke("list_endpoint_profiles")) || [];
  } catch (e) {
    console.error("load ai endpoints failed", e);
    aiEndpoints = [];
  }
  renderEpList();
}

function renderEpList() {
  const list = document.getElementById("setEpList");
  const empty = document.getElementById("setEpEmpty");
  if (!list) return;
  list.innerHTML = "";
  if (aiEndpoints.length === 0) {
    if (empty) empty.style.display = "block";
    return;
  }
  if (empty) empty.style.display = "none";
  for (const ep of aiEndpoints) {
    const item = el("div", "set-res-item");
    const info = el("div");
    info.appendChild(el("div", "ri-name", ep.name || "(未命名)"));
    const meta = kindLabel(ep.kind) + " · " + (ep.baseUrl || ep.speechRegion || "—");
    info.appendChild(el("div", "ri-meta", meta));
    item.appendChild(info);

    const actions = el("div", "ri-actions");
    const testBtn = el("button", "btn sm", tr("set.test"));
    testBtn.addEventListener("click", () => runEpTest(ep));
    actions.appendChild(testBtn);
    const editBtn = el("button", "btn sm", tr("set.edit"));
    editBtn.addEventListener("click", () => openEpForm(ep));
    actions.appendChild(editBtn);
    const delBtn = el("button", "btn sm danger", tr("set.delete"));
    delBtn.addEventListener("click", () => deleteEp(ep.id));
    actions.appendChild(delBtn);
    item.appendChild(actions);
    list.appendChild(item);
  }
}

/* 选厂商资料包 */
function openEpPicker() {
  document.getElementById("setEpForm").style.display = "none";
  const picker = document.getElementById("setEpPicker");
  const listEl = document.getElementById("epProfileList");
  if (!picker || !listEl) return;
  listEl.innerHTML = "";
  for (const p of epProfiles) {
    const card = el("div", "ep-profile-card");
    card.appendChild(el("span", "ep-glyph", p.glyph || "◎"));
    const txt = el("div", "ep-ptext");
    txt.appendChild(el("div", "ep-pname", p.displayName || p.id));
    txt.appendChild(el("div", "ep-psub", p.subtitle || ""));
    card.appendChild(txt);
    card.addEventListener("click", () => pickProfile(p.id));
    listEl.appendChild(card);
  }
  picker.style.display = "block";
}

async function pickProfile(profileId) {
  if (!invoke) return;
  try {
    const ep = await invoke("build_endpoint_from_profile", { profileId });
    document.getElementById("setEpPicker").style.display = "none";
    openEpForm(ep);
  } catch (e) {
    console.error("build endpoint from profile failed", e);
  }
}

function behaviorSummary(ep) {
  const auth = ep.authMode === "Aad"
    ? "Microsoft Entra ID (AAD)"
    : ep.apiKeyHeaderMode === "ApiKeyHeader"
      ? "api-key Header"
      : ep.apiKeyHeaderMode === "Bearer"
        ? "Authorization: Bearer"
        : "自动";
  const proto = PROTO_LABELS[ep.textProtocol] || ep.textProtocol || "自动";
  const ver = ep.apiVersion ? ep.apiVersion : "未显式填写";
  return `模板策略：文本协议 ${proto}；认证 ${auth}；API 版本 ${ver}`;
}

function openEpForm(ep) {
  currentEp = { ...ep };
  document.getElementById("setEpPicker").style.display = "none";
  const isSpeech = ep.kind === "AzureSpeech";

  document.getElementById("epId").value = ep.id || "";
  document.getElementById("epProfileId").value = ep.profileId || "";
  document.getElementById("epKind").value = ep.kind || "";
  document.getElementById("epProfileLabel").textContent =
    profileName(ep.profileId) || kindLabel(ep.kind);
  document.getElementById("epName").value = ep.name || "";
  document.getElementById("epBaseUrl").value = ep.baseUrl || "";
  document.getElementById("epApiVersion").value = ep.apiVersion || "";
  document.getElementById("epRegion").value = ep.speechRegion || "";
  document.getElementById("epSpeechEndpoint").value = ep.speechEndpoint || "";
  document.getElementById("epApiKey").value = isSpeech
    ? (ep.speechSubscriptionKey || "")
    : (ep.apiKey || "");

  document.getElementById("epRowBaseUrl").style.display = isSpeech ? "none" : "";
  document.getElementById("epRowApiVersion").style.display = isSpeech ? "none" : "";
  document.getElementById("epRowRegion").style.display = isSpeech ? "" : "none";
  document.getElementById("epRowSpeechEndpoint").style.display = isSpeech ? "" : "none";
  document.getElementById("epKeyLabel").textContent = isSpeech
    ? tr("set.resKey")
    : tr("set.epApiKey");
  document.getElementById("epBehavior").textContent = behaviorSummary(ep);

  document.getElementById("setEpForm").style.display = "block";
}

function closeEpForm() {
  document.getElementById("setEpForm").style.display = "none";
  currentEp = null;
}

async function saveEp() {
  if (!invoke || !currentEp) return;
  const isSpeech = currentEp.kind === "AzureSpeech";
  const ep = { ...currentEp };
  ep.id = document.getElementById("epId").value || "";
  ep.name =
    document.getElementById("epName").value.trim() ||
    profileName(ep.profileId) ||
    "AI 终结点";
  if (isSpeech) {
    ep.speechSubscriptionKey = document.getElementById("epApiKey").value.trim();
    ep.speechRegion = document.getElementById("epRegion").value.trim();
    ep.speechEndpoint = document.getElementById("epSpeechEndpoint").value.trim();
  } else {
    ep.baseUrl = document.getElementById("epBaseUrl").value.trim();
    ep.apiKey = document.getElementById("epApiKey").value.trim();
    ep.apiVersion = document.getElementById("epApiVersion").value.trim();
  }
  try {
    await invoke("save_ai_endpoint", { endpoint: ep });
    closeEpForm();
    await loadAiEndpoints();
  } catch (e) {
    console.error("save ai endpoint failed", e);
  }
}

async function deleteEp(id) {
  if (!invoke) return;
  try {
    await invoke("delete_ai_endpoint", { id });
    await loadAiEndpoints();
  } catch (e) {
    console.error("delete ai endpoint failed", e);
  }
}

/* ---------------- 终结点连通性测试（结果窗体） ---------------- */
const TEST_STATUS_META = {
  Success: { cls: "ok", glyph: "✅", label: "通过" },
  Failed: { cls: "fail", glyph: "❌", label: "失败" },
  Skipped: { cls: "skip", glyph: "⏭", label: "跳过" },
};

function openTestOverlay() {
  const ov = document.getElementById("epTestOverlay");
  if (ov) ov.style.display = "flex";
}
function closeTestOverlay() {
  const ov = document.getElementById("epTestOverlay");
  if (ov) ov.style.display = "none";
}

function renderTestSummary(items) {
  const ok = items.filter(i => i.status === "Success").length;
  const fail = items.filter(i => i.status === "Failed").length;
  const skip = items.filter(i => i.status === "Skipped").length;
  return `共 ${items.length} 项 · 通过 ${ok} · 失败 ${fail} · 跳过 ${skip}`;
}

function renderTestItems(items) {
  const list = document.getElementById("epTestList");
  if (!list) return;
  list.innerHTML = "";
  for (const it of items) {
    const meta = TEST_STATUS_META[it.status] || { cls: "skip", glyph: "•", label: it.status };
    const card = el("div", "ep-test-item " + meta.cls);

    const head = el("div", "eti-head");
    head.appendChild(el("span", "eti-badge " + meta.cls, meta.glyph + " " + meta.label));
    const title = it.capability + (it.modelId ? " · " + it.modelId : "");
    head.appendChild(el("span", "eti-title", title));
    if (it.durationMs > 0) head.appendChild(el("span", "eti-dur", it.durationMs + " ms"));
    card.appendChild(head);

    card.appendChild(el("div", "eti-summary", it.summary || ""));

    if (it.requestUrl) {
      const urlRow = el("div", "eti-url");
      urlRow.appendChild(el("span", "eti-url-k", "最终访问 URL"));
      urlRow.appendChild(el("div", "eti-url-v", it.requestUrl));
      card.appendChild(urlRow);
    }
    if (it.details) {
      card.appendChild(el("div", "eti-details", it.details));
    }
    list.appendChild(card);
  }
}

async function runEpTest(ep) {
  if (!invoke) return;
  const title = document.getElementById("epTestTitle");
  const summary = document.getElementById("epTestSummary");
  if (title) title.textContent = tr("set.epTestTitle") + " · " + (ep.name || ep.id);
  if (summary) summary.textContent = tr("set.epTesting");
  renderTestItems([]);
  openTestOverlay();
  try {
    const items = (await invoke("test_ai_endpoint", { id: ep.id })) || [];
    if (summary) summary.textContent = renderTestSummary(items);
    renderTestItems(items);
  } catch (e) {
    console.error("test ai endpoint failed", e);
    if (summary) summary.textContent = "测试失败：" + e;
    renderTestItems([
      { status: "Failed", capability: "整体", modelId: "", summary: "测试调用失败。", details: String(e), requestUrl: "", durationMs: 0 },
    ]);
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

  // AI 终结点
  const addEpBtn = document.getElementById("setAddEp");
  if (addEpBtn) addEpBtn.addEventListener("click", openEpPicker);
  const epPickerCancel = document.getElementById("epPickerCancel");
  if (epPickerCancel)
    epPickerCancel.addEventListener("click", () => {
      document.getElementById("setEpPicker").style.display = "none";
    });
  const epSaveBtn = document.getElementById("epSave");
  if (epSaveBtn) epSaveBtn.addEventListener("click", saveEp);
  const epCancelBtn = document.getElementById("epCancel");
  if (epCancelBtn) epCancelBtn.addEventListener("click", closeEpForm);

  // 终结点测试结果窗体
  const epTestClose = document.getElementById("epTestClose");
  if (epTestClose) epTestClose.addEventListener("click", closeTestOverlay);
  const epTestOverlay = document.getElementById("epTestOverlay");
  if (epTestOverlay)
    epTestOverlay.addEventListener("click", (e) => {
      if (e.target === epTestOverlay) closeTestOverlay();
    });

  // 语言切换 → 重渲染依赖 i18n 的动态文本
  document.addEventListener("app:langchange", () => {
    renderResList();
    renderResSelector();
    renderEpList();
  });
}
