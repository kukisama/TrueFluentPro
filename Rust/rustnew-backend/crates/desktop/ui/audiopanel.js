/* ============================================================
   audiopanel.js — 音频与录音设置面板
   - 识别音源 / 混音模式 / 增益 / 识别采样率
   - 录音容器 / 采样率 / 声道 / MP3 码率
   - 独立录音测试（start_recording / stop_recording，不依赖云识别）
   读写后端 get_audio_settings / set_audio_settings；保存时回写「整份」设置，
   避免覆盖其它字段。
   ============================================================ */

import { invoke, tr } from "./core.js";

// 整份音频设置缓存（保存时回写全量，防止丢字段）。
let audio = null;
let recording = false;

/* ---------------- 加载 ---------------- */
export async function loadAudioPanel() {
  if (!invoke) return;
  try {
    const [settings, loopbackOk] = await Promise.all([
      invoke("get_audio_settings").catch(() => null),
      invoke("audio_loopback_supported").catch(() => false),
    ]);
    audio = settings || defaultAudio();
    ensureShape(audio);
    populate(loopbackOk);
  } catch (e) {
    console.error("load audio panel failed", e);
  }
}

function defaultAudio() {
  return {
    sourceMode: "defaultMic",
    recordingMode: "loopbackOnly",
    selectedInputDeviceId: "",
    selectedOutputDeviceId: "",
    useInputForRecognition: true,
    useOutputForRecognition: false,
    mixMode: "downmixMono",
    micGain: 1.0,
    loopbackGain: 1.0,
    recognitionFormat: { sampleRate: 16000, bitsPerSample: 16, channels: 1 },
    recorder: {
      enabled: true,
      container: "mp3",
      format: { sampleRate: 48000, bitsPerSample: 16, channels: 2 },
      mp3BitrateKbps: 256,
    },
  };
}

// 后端可能返回精简对象（旧配置/默认），补齐嵌套结构以免读取报错。
function ensureShape(a) {
  const d = defaultAudio();
  a.sourceMode ??= d.sourceMode;
  a.mixMode ??= d.mixMode;
  a.micGain ??= d.micGain;
  a.loopbackGain ??= d.loopbackGain;
  a.recognitionFormat ??= d.recognitionFormat;
  a.recognitionFormat.sampleRate ??= d.recognitionFormat.sampleRate;
  a.recognitionFormat.bitsPerSample ??= d.recognitionFormat.bitsPerSample;
  a.recognitionFormat.channels ??= d.recognitionFormat.channels;
  a.recorder ??= d.recorder;
  a.recorder.container ??= d.recorder.container;
  a.recorder.mp3BitrateKbps ??= d.recorder.mp3BitrateKbps;
  a.recorder.format ??= d.recorder.format;
  a.recorder.format.sampleRate ??= d.recorder.format.sampleRate;
  a.recorder.format.bitsPerSample ??= d.recorder.format.bitsPerSample;
  a.recorder.format.channels ??= d.recorder.format.channels;
}

/* ---------------- 渲染 ---------------- */
function populate(loopbackOk) {
  setVal("setAudioSource", audio.sourceMode);
  setVal("setMixMode", audio.mixMode);
  setVal("setMicGain", audio.micGain);
  setVal("setLoopbackGain", audio.loopbackGain);
  setVal("setRecogRate", String(audio.recognitionFormat.sampleRate));
  setVal("setRecContainer", audio.recorder.container);
  setVal("setRecRate", String(audio.recorder.format.sampleRate));
  setVal("setRecChannels", String(audio.recorder.format.channels));
  setVal("setMp3Bitrate", String(audio.recorder.mp3BitrateKbps));

  // 回环不支持时置灰对应选项
  const srcSel = document.getElementById("setAudioSource");
  if (srcSel) {
    const opt = [...srcSel.options].find(o => o.value === "loopback");
    if (opt && !loopbackOk) {
      opt.disabled = true;
      opt.textContent = tr("set.srcLoopback") + " — " + tr("set.loopbackUnsupported");
      if (audio.sourceMode === "loopback") {
        srcSel.value = "defaultMic";
      }
    }
  }
  updateMp3Visibility();
}

function updateMp3Visibility() {
  const row = document.getElementById("rowMp3Bitrate");
  if (row) row.style.display = audio.recorder.container === "mp3" ? "" : "none";
}

/* ---------------- 保存 ---------------- */
async function save() {
  if (!invoke || !audio) return;
  try {
    await invoke("set_audio_settings", { audio });
    setRecStatus(tr("set.audioSaved"), false);
  } catch (e) {
    setRecStatus(String(e), true);
    console.error("save audio settings failed", e);
  }
}

/* ---------------- 录音测试 ---------------- */
async function toggleRecording() {
  if (!invoke) return;
  if (!recording) {
    const path = (document.getElementById("setRecPath")?.value || "").trim();
    if (!path) {
      setRecStatus(tr("set.recPath"), true);
      return;
    }
    try {
      await invoke("start_recording", { path });
      recording = true;
      setRecToggleLabel(true);
      setRecStatus(tr("set.recRecording"), false);
    } catch (e) {
      setRecStatus(String(e), true);
      console.error("start_recording failed", e);
    }
  } else {
    try {
      const saved = await invoke("stop_recording");
      recording = false;
      setRecToggleLabel(false);
      setRecStatus(tr("set.recSaved") + saved, false);
    } catch (e) {
      recording = false;
      setRecToggleLabel(false);
      setRecStatus(String(e), true);
      console.error("stop_recording failed", e);
    }
  }
}

/* ---------------- DOM 工具 ---------------- */
function setVal(id, v) {
  const el = document.getElementById(id);
  if (el != null && v != null) el.value = v;
}

function setRecStatus(text, isErr) {
  const s = document.getElementById("setRecStatus");
  if (!s) return;
  s.textContent = text;
  s.style.color = isErr ? "var(--danger, #d9534f)" : "";
}

function setRecToggleLabel(isRec) {
  const lbl = document.getElementById("setRecToggleLabel");
  if (lbl) lbl.textContent = isRec ? tr("set.recStop") : tr("set.recStart");
  const btn = document.getElementById("setRecToggle");
  if (btn) btn.classList.toggle("danger", isRec);
}

/* ---------------- 初始化 ---------------- */
export function initAudioPanel() {
  bind("setAudioSource", "change", v => { audio.sourceMode = v; save(); });
  bind("setMixMode", "change", v => { audio.mixMode = v; save(); });
  bind("setMicGain", "change", v => { audio.micGain = clampGain(v); save(); });
  bind("setLoopbackGain", "change", v => { audio.loopbackGain = clampGain(v); save(); });
  bind("setRecogRate", "change", v => { audio.recognitionFormat.sampleRate = parseInt(v, 10); save(); });
  bind("setRecContainer", "change", v => { audio.recorder.container = v; updateMp3Visibility(); save(); });
  bind("setRecRate", "change", v => { audio.recorder.format.sampleRate = parseInt(v, 10); save(); });
  bind("setRecChannels", "change", v => { audio.recorder.format.channels = parseInt(v, 10); save(); });
  bind("setMp3Bitrate", "change", v => { audio.recorder.mp3BitrateKbps = parseInt(v, 10); save(); });

  const toggle = document.getElementById("setRecToggle");
  if (toggle) toggle.addEventListener("click", toggleRecording);

  // 语言切换后刷新按钮/状态文案
  document.addEventListener("app:langchange", () => {
    setRecToggleLabel(recording);
    if (!recording) setRecStatus(tr("set.recIdle"), false);
  });
}

function bind(id, evt, fn) {
  const el = document.getElementById(id);
  if (!el) return;
  // 绑定时 audio 可能尚未加载（initAudioPanel 在 loadAudioPanel 之前跑），
  // 故触发时再判断 audio 是否就绪。
  el.addEventListener(evt, e => { if (audio) fn(e.target.value); });
}

function clampGain(v) {
  const n = parseFloat(v);
  if (isNaN(n)) return 1.0;
  return Math.min(4.0, Math.max(0.0, n));
}
