"use strict";

(() => {
  const $ = (selector) => document.querySelector(selector);
  const $$ = (selector) => Array.from(document.querySelectorAll(selector));
  const stage = $("#stage");
  const viewport = $("#viewport");
  const transcript = $("#conversation-scroll");
  const reference = [
    { source: "mic", time: "10:24:17", original: "让交流跨越语言的边界。", translated: "Let every conversation flow naturally." },
    { source: "mic", time: "10:24:29", original: "这真是一个很棒的想法。", translated: "That’s a wonderful idea." }
  ];
  const longDialogue = [
    { ...reference[0], original: "让交流跨越语言的边界。我们希望让来自不同地方的人都能自然地表达想法，不必因为语言而错过值得深入讨论的机会。", translated: "Let every conversation flow naturally. We want people from different places to express their ideas with ease, without missing meaningful opportunities because of language." },
    { ...reference[1], original: "这真是一个很棒的想法。技术可以让世界更紧密地连接，但我们同样需要保留每个人说话时的语气，以及那些藏在句子之间的善意。", translated: "That’s a wonderful idea. Technology can bring the world closer together, while preserving each person’s tone and the kindness between their words." },
    { source: "mic", time: "10:25:04", original: "如果将实时翻译用于跨国团队的会议，我们还可以在会后梳理共识、待办和仍然需要澄清的问题。", translated: "For international team meetings, we can also organize shared decisions, action items, and questions that still need clarification." },
    { source: "system", time: "10:25:16", original: "这一段是系统音频来源示例。音源标签不表示人物身份，阅读时可以继续调整字号。", translated: "This is a system-audio example. The input label does not identify a person. Text size remains adjustable while reading." },
    { source: "unknown", time: "10:25:43", original: "我们先从一个小团队开始试用，收集实际使用的反馈，再决定下一步。", translated: "Let’s start with a small team, gather practical feedback, and decide on the next step." }
  ];
  const historyPairs = [
    ["让交流跨越语言的边界。", "Let every conversation flow naturally."],
    ["技术可以让世界更紧密地连接。", "Technology can bring the world closer together."],
    ["很高兴认识你。", "Nice to meet you."],
    ["我们希望在下个季度启动这个项目。", "We hope to launch this project next quarter."],
    ["目前的重点是提升用户体验。", "Our current focus is improving the user experience."],
    ["这个方案需要进一步讨论。", "This plan needs further discussion."],
    ["我们会尽快给出反馈。", "We will provide feedback as soon as possible."],
    ["感谢你的时间和分享。", "Thank you for your time and insights."],
    ["下一步可以安排一次更详细的会议。", "Next, we can schedule a more detailed meeting."],
    ["期待未来有更多合作机会。", "We look forward to more opportunities to work together."],
    ["先让一个小团队试用。", "Let a small team try it first."],
    ["试点优先考虑跨语言会议。", "The pilot should focus on multilingual meetings."],
    ["请保留原文，便于核对术语。", "Please retain the original text for terminology checks."],
    ["字幕字号应该允许调整。", "Subtitle text size should be adjustable."],
    ["我们需要一份明确的行动清单。", "We need a clear list of action items."],
    ["整理试点的时间与范围。", "Outline the pilot timeline and scope."],
    ["会后请汇总尚未解决的问题。", "Please summarize unresolved questions after the meeting."],
    ["有些术语需要统一表达。", "Some terms need consistent translations."],
    ["先观察实际使用中的延迟。", "First, observe latency in practical use."],
    ["反馈要区分建议与故障。", "Feedback should distinguish suggestions from failures."],
    ["我们还没有确定最终预算。", "We have not finalized the budget."],
    ["可以先准备一份演示材料。", "We can prepare a demonstration first."],
    ["请确认双方的联系人。", "Please confirm the contacts on both teams."],
    ["行业术语整理需要业务参与。", "Domain terminology requires input from the business team."],
    ["会议摘要不应遗漏待确认事项。", "The meeting summary should retain open questions."],
    ["情绪判断需要结合上下文。", "Sentiment should be considered in context."],
    ["不要将建议记录为既定承诺。", "Do not record a suggestion as a commitment."],
    ["下次会议一起审阅试点反馈。", "Let’s review pilot feedback at the next meeting."],
    ["保持联系，逐步完善方案。", "Let’s stay in touch and refine the plan."],
    ["这真是一个很棒的想法。", "That’s a wonderful idea."]
  ];
  const initialHistory = historyPairs.map(([original, translated], index) => ({
    original, translated, source: "mic", time: `10:${String(24 - Math.floor(index * 24 / 30)).padStart(2, "0")}:${String(59 - index).padStart(2, "0")}`
  }));
  const reportSections = [
    ["会议概况", ["会议围绕跨语言沟通产品的试点与合作展开。", "讨论覆盖产品定位、目标场景与下一步安排。", "以跨语言会议作为首批试用场景。", "先由一个小团队收集实际使用反馈。", "保留原文与译文，方便逐句核对。", "会议记录中的建议与承诺需要分别整理。"]],
    ["核心共识", ["认可自然表达与多语沟通的实际价值。", "同意先试点，再评估是否扩大使用范围。", "阅读字号与字幕显示应当可调整。", "术语整理需要业务团队共同参与。", "会后保留仍待澄清的问题。", "反馈用于完善方案，不直接作为最终结论。"]],
    ["讨论要点", ["市场定位：优先观察跨语言会议的需求。", "合作方式：从小范围试用建立沟通机制。", "产品呈现：双语并列，降低核对成本。", "音频来源：区分麦克风与系统音频输入。", "使用体验：记录实际阅读节奏与响应延迟。", "后续安排：召开更详细的试点讨论会议。"]],
    ["客户反馈与知识点", ["字幕字号的调整是明确的体验建议。", "术语表达一致性有助于减少沟通误解。", "摘要应涵盖共识、行动项与待确认事项。", "建议与故障应分开收集，便于分类处理。", "人物身份不能从音源标签中推断。", "情绪描述应保留上下文，避免过度判断。"]],
    ["风险与待确认", ["最终预算尚未确定，不能记作批准。", "试点时间与范围仍需明确。", "实际云端连接状态需要另行验证。", "没有具体承诺的建议不能写成既定计划。", "联系人与业务分工需要后续确认。", "当前材料不足以推断所有场景的效果。"]],
    ["行动项", ["整理试点时间与范围，作为下次议题。", "准备一份双语演示材料。", "确认双方联系人与反馈渠道。", "邀请业务团队整理行业术语。", "分类汇总体验建议与实际故障。", "在下次会议中审阅试点反馈。"]],
    ["会后跟进", ["保留这次会议的原文与译文示例。", "将待确认事项单列，避免遗漏。", "按实际反馈逐步完善方案。", "核对行动项，不擅自补充负责人或日期。", "持续沟通，明确下一次讨论的材料。", "以小范围验证形成可复盘的推进节奏。"]]
  ];
  const focusLines = {
    knowledge: ["知识点提取", "双语核对保留原文、术语和上下文。", "试点先限定场景与规模，再收集反馈。", "音频输入来源与人物身份是不同概念。", "摘要区分事实、建议与待确认事项。", "阅读字号和字幕显示属于体验层设置。"],
    complaints: ["客户投诉识别", "现有示例没有明确的投诉或索赔。", "调整字幕字号属于体验建议。", "关注响应延迟并不等于已经确认故障。", "术语不一致需要核对原句再分类。", "以下相关反馈用于观察识别结果的排版。"],
    actions: ["行动项提取", "准备双语演示材料。", "整理试点的时间与范围。", "确认联系人和反馈渠道。", "安排更详细的试点讨论会议。", "示例未确认负责人和截止日，保留待定。"],
    sentiment: ["情绪分析", "整体表达积极，并希望保持沟通。", "认可想法，但仍希望继续讨论细节。", "预算与范围未定，态度需结合上下文解读。", "现有示例不支持精确情绪评分。", "音源不是人物标签，不将情绪分配给猜测身份。"]
  };
  const defaults = {
    system: "基于会议材料梳理事实、共识与待确认事项，不臆造人物身份或承诺。",
    template: "会议内容：\n{history}\n\n用户问题：{question}", model: "本地演示模型", presets: [
      { id: "summary", name: "会议摘要", prompt: "总结会议概况、核心共识、风险与行动项。", kind: "summary" },
      { id: "knowledge", name: "知识点提取", prompt: "提取可复用知识点，并保留上下文。", kind: "knowledge" },
      { id: "complaints", name: "客户投诉识别", prompt: "区分明确投诉、体验建议与待核对故障。", kind: "complaints" },
      { id: "actions", name: "行动项提取", prompt: "提取行动项，不补充未确认的负责人或日期。", kind: "actions" },
      { id: "sentiment", name: "情绪分析", prompt: "结合上下文描述情绪，不猜人物身份。", kind: "sentiment" }
    ]
  };
  const autoDefaults = { enabled: true, mode: "interval", interval: 30, prompt: "总结会议进展、共识与下一步行动。", buffer: true };
  let config = structuredClone(defaults);
  let autoConfig = { ...autoDefaults };
  let configDraft = null;
  let editingPreset = "summary";
  let selectedPreset = "summary";
  let presetSequence = 0;
  let historyCleared = false;
  let session = "idle";
  let connectionTimer = null;
  let generationTimer = null;
  let generationToken = 0;
  let generationActive = false;
  let previousResult = null;
  let result = null;
  let autoTimer = null;
  let autoRunning = false;
  let lastAutoAt = 0;
  let revision = 0;
  let consumedRevision = 0;
  let refreshCount = 0;
  let allHistory = false;
  let activeTab = "insight";
  let floatSize = 18;
  const readingPositions = { history: 0, insight: 0 };
  const dialogTriggers = new WeakMap();
  const sourceLabels = { mic: "麦克风", system: "系统音频", both: "麦克风 + 系统音频" };
  const sourceLabel = (source) => sourceLabels[source] || "音频输入";
  const textElement = (tag, text, className = "") => {
    const element = document.createElement(tag);
    element.textContent = text;
    element.className = className;
    return element;
  };
  const notice = (text) => { $("#page-notice").textContent = text; };

  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "icon");
    svg.setAttribute("aria-hidden", "true");
    const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", `#i-${name}`);
    svg.append(use);
    return svg;
  }

  function fillWave(wave) {
    const heights = [6, 10, 17, 9, 28, 17, 46, 30, 16, 10, 20, 12, 6];
    heights.forEach((height, i) => {
      const bar = document.createElement("span");
      bar.style.setProperty("--bar-height", `${height}px`);
      bar.style.setProperty("--bar-opacity", String(.38 + (1 - Math.abs(i - 6) / 7) * .6));
      bar.style.setProperty("--bar-duration", `${.45 + (i % 5) * .12}s`);
      bar.style.setProperty("--bar-delay", `${-i * .08}s`);
      wave.append(bar);
    });
  }

  function renderDialogue(fixture = "reference") {
    const messages = fixture === "long" ? longDialogue : reference;
    transcript.replaceChildren();
    transcript.classList.toggle("long-content", fixture === "long");
    messages.forEach((message, index) => {
      const article = document.createElement("article");
      article.className = "utterance";
      article.dataset.source = message.source;
      article.setAttribute("aria-label", `${sourceLabel(message.source)} ${message.time}`);
      const header = document.createElement("header");
      header.className = "utterance-header";
      const inputIcon = document.createElement("span");
      inputIcon.className = "source-icon";
      inputIcon.setAttribute("aria-hidden", "true");
      if (message.source === "mic" || message.source === "system") inputIcon.append(icon(message.source === "system" ? "screen" : "mic"));
      else inputIcon.textContent = "≈";
      const sourceTitle = document.createElement("strong");
      sourceTitle.textContent = sourceLabel(message.source);
      const wave = document.createElement("span");
      wave.className = "wave";
      wave.setAttribute("aria-hidden", "true");
      fillWave(wave);
      const time = document.createElement("time");
      time.dateTime = `2026-11-05T${message.time}`;
      time.textContent = message.time;
      header.append(inputIcon, sourceTitle, wave, time);
      const original = document.createElement("div");
      original.className = "original-line";
      const originalText = document.createElement("p");
      originalText.textContent = message.original;
      original.append(originalText);
      const translated = document.createElement("div");
      translated.className = "translation-line";
      const translatedText = document.createElement("p");
      translatedText.lang = "en";
      translatedText.textContent = message.translated;
      translated.append(translatedText);
      article.dataset.message = String(index);
      article.append(header, original, translated);
      transcript.append(article);
    });
    transcript.scrollTop = 0;
  }

  function buildHistory() {
    const content = $("#history-content");
    content.replaceChildren();
    initialHistory.forEach((entry, index) => {
      const row = document.createElement("details");
      row.className = "history-entry";
      row.dataset.index = String(index);
      const summary = document.createElement("summary");
      summary.setAttribute("aria-label", `${entry.time} ${sourceLabel(entry.source)}，展开或收起上下文`);
      const time = document.createElement("time");
      time.textContent = entry.time;
      time.dateTime = `2026-11-05T${entry.time}`;
      const text = document.createElement("div");
      text.className = "history-text";
      const original = document.createElement("p");
      original.textContent = entry.original;
      const translated = document.createElement("p");
      translated.lang = "en";
      translated.textContent = entry.translated;
      text.append(original, translated);
      summary.append(time, text);
      const context = textElement("div", "", "history-context");
      context.append(textElement("strong", `${sourceLabel(entry.source)} · 本地会议示例`));
      [index - 1, index, index + 1].filter((i) => i >= 0 && i < initialHistory.length).forEach((i) => {
        context.append(textElement("p", `${i === index ? "本句" : i < index ? "较近记录" : "较早记录"}：${initialHistory[i].original}`));
        const translation = textElement("p", initialHistory[i].translated);
        translation.lang = "en";
        context.append(translation);
      });
      row.append(summary, context);
      content.append(row);
    });
    updateHistory();
  }

  function updateHistory() {
    $$(".history-entry").forEach((row, index) => { row.hidden = !allHistory && index >= 15; });
    $("#history-content").hidden = historyCleared;
    $("#history-empty").hidden = !historyCleared;
    $("#history-count").textContent = historyCleared ? "0 条 · 本地示例已清空" : `${allHistory ? "全部 30" : "最近 15"} 条 / 共 30 条`;
    $("#history-all").textContent = allHistory ? "最近 15 条" : "查看全部";
    $("#history-all").setAttribute("aria-pressed", String(allHistory));
    $("#history-all").disabled = historyCleared;
    $("#history-clear").disabled = historyCleared;
    $("#history-restore").hidden = !historyCleared;
  }

  function buildResult(title = "会议洞察报告", kind = "summary", question = "") {
    const sections = reportSections.map(([heading, lines]) => ({ heading, lines: [...lines] }));
    if (focusLines[kind]) {
      const [heading, ...lines] = focusLines[kind];
      sections.unshift({ heading, lines });
    } else if (question) {
      sections.unshift({ heading: "针对你的提问", lines: [
        `提问：${question}`, "优先核对试点范围、预算与下一步安排。", "当前材料保留了相关讨论，详细内容如下。", "未确定的责任分工与日期保持待确认。", "此处为本地模拟反馈，可修改提示词观察配置应用。"
      ] });
    }
    const historyText = historyCleared ? "（本地历史为空）" : initialHistory.map((entry) => entry.original).join("\n");
    return { title, sections, time: new Date().toLocaleTimeString("zh-CN", { hour12: false }), question, system: config.system,
      content: config.template.replaceAll("{history}", historyText).replaceAll("{question}", question), model: config.model };
  }

  function renderResult(partial = null) {
    const shown = partial || result;
    const scroll = $("#insight-scroll");
    const readingPosition = activeTab === "insight" ? scroll.scrollTop : readingPositions.insight;
    const output = $("#insight-result");
    output.replaceChildren();
    output.hidden = !shown;
    $("#insight-empty").hidden = Boolean(shown);
    $("#empty-insight").checked = !shown;
    if (shown) {
      output.append(textElement("h2", shown.title), textElement("p", `生成时间 ${shown.time} · 本地会议示例 · ${shown.model}`, "report-meta"));
      shown.sections.forEach((section, index) => {
        const group = document.createElement("section");
        group.append(textElement("h3", `${index + 1} · ${section.heading}`));
        section.lines.forEach((line) => group.append(textElement("p", line, "report-line")));
        output.append(group);
      });
      const parameters = document.createElement("details");
      parameters.className = "result-parameters";
      parameters.append(textElement("summary", "查看本次本地提示词与内容模板"), textElement("p", shown.system), textElement("pre", shown.content));
      output.append(parameters);
    }
    if (activeTab === "insight") scroll.scrollTop = readingPosition;
    // The floating paper is a second view of the same state, never a new request.
    const floating = $("#float-result");
    const position = floating.scrollTop;
    floating.replaceChildren(shown ? output.cloneNode(true) : textElement("p", "尚无洞察。可在主书页选择预设或提问。"));
    if (shown) floating.firstElementChild.removeAttribute("id");
    floating.scrollTop = position;
  }

  function generationStatus(text) {
    $("#insight-state").textContent = text;
    $("#generation-stop").hidden = !generationActive;
    $("#question-send").disabled = generationActive;
    $("#insight-result").setAttribute("aria-busy", String(generationActive));
  }

  function cancelGeneration(text = "已停止生成 · 保留原结果") {
    ++generationToken;
    clearTimeout(generationTimer);
    generationTimer = null;
    if (generationActive) result = previousResult;
    generationActive = false;
    previousResult = null;
    renderResult();
    generationStatus(text);
  }

  function generate(title, kind, question, buffer = true) {
    if (generationActive) cancelGeneration();
    previousResult = result;
    generationActive = true;
    const token = ++generationToken;
    const candidate = buildResult(title, kind, question);
    generationStatus(`正在生成 · ${title}`);
    if (!buffer) {
      generationTimer = setTimeout(() => {
        if (token !== generationToken) return;
        renderResult({ ...candidate, sections: candidate.sections.slice(0, 2) });
        generationStatus("生成中 · 部分输出，停止可撤回");
        generationTimer = setTimeout(commit, 650);
      }, 400);
    } else generationTimer = setTimeout(commit, 1050);
    function commit() {
      if (token !== generationToken) return;
      result = candidate;
      previousResult = null;
      generationActive = false;
      generationTimer = null;
      renderResult();
      // New result replaces the text without resetting the user's reading position.
      generationStatus(`已完成 · ${title}`);
    }
  }

  function fitCanvas() {
    const scale = $("#actual-size").checked ? 1 : Math.min(window.innerWidth / 2560, window.innerHeight / 1440);
    document.documentElement.style.setProperty("--scale", String(scale));
    viewport.style.width = `${2560 * scale}px`;
    viewport.style.height = `${1440 * scale}px`;
    clampFloat();
  }

  function setDisplay(mode) {
    stage.dataset.display = mode;
    $$(".segment-control button").forEach((button) => {
      const selected = button.dataset.display === mode;
      button.setAttribute("aria-selected", String(selected));
      button.tabIndex = selected ? 0 : -1;
    });
    transcript.setAttribute("aria-labelledby", `display-${mode}`);
  }

  function setSession(next) {
    session = next;
    stage.dataset.session = next;
    $("#start-button").setAttribute("aria-pressed", String(next !== "idle"));
    $("#start-label").textContent = next === "idle" ? "开始翻译" : next === "connecting" ? "取消连接" : "停止翻译";
    $("#connection-state").textContent = next === "idle" ? "待开始" : next === "connecting" ? "连接中…" : "运行中";
    $$("[data-session-lock]").forEach((control) => { control.disabled = next !== "idle"; });
    $("#settings-note").textContent = next === "idle" ? "资源已配置，不代表云端连通；设备与路由均为本地示例。" : "本地模拟会话中 · 资源、语言和音频路由已锁定，字号与字幕仍可调整。";
  }

  function updateSettings() {
    const selectedText = (id) => $(id).selectedOptions[0].textContent;
    $("#settings-summary-text").textContent = `${selectedText("#region")} | ${sourceLabel($("#recognition-source").value)} | ${selectedText("#source-language")} → ${selectedText("#target-language")} | ${selectedText("#font-size")}`;
    $("#source-summary").textContent = `识别：${sourceLabel($("#recognition-source").value)} · 录制：${sourceLabel($("#recording-source").value)}`;
    const value = Number($("#font-size").value);
    stage.style.setProperty("--dialogue-size", `${36 + (value - 16) * 1.5}px`);
    stage.style.setProperty("--translation-size", `${32 + (value - 16) * 1.5}px`);
    stage.style.setProperty("--reading-size", `${26 + (value - 16)}px`);
  }

  function toggleSettings(open = $("#translation-settings").hidden) {
    $("#translation-settings").hidden = !open;
    $("#settings-toggle").setAttribute("aria-expanded", String(open));
    $("#settings-toggle-label").textContent = open ? "收起 ▴" : "展开 ▾";
    clampFloat();
  }

  function setReadingTab(name) {
    const oldScroll = activeTab === "history" ? $("#history-content") : $("#insight-scroll");
    readingPositions[activeTab] = oldScroll.scrollTop;
    activeTab = name;
    closeMenu();
    $$(".history-tabs button").forEach((tab) => {
      const selected = tab.dataset.tab === name;
      tab.setAttribute("aria-selected", String(selected));
      tab.tabIndex = selected ? 0 : -1;
    });
    ["history", "insight"].forEach((type) => {
      $(`#${type}-panel`).hidden = type !== name;
      $(`#${type}-actions`).hidden = type !== name;
    });
    (name === "history" ? $("#history-content") : $("#insight-scroll")).scrollTop = readingPositions[name];
  }

  function wireTabs(selector, activate) {
    const tabs = $$(selector);
    tabs.forEach((tab, index) => {
      tab.addEventListener("click", () => activate(tab));
      tab.addEventListener("keydown", (event) => {
        let destination;
        if (event.key === "ArrowRight") destination = (index + 1) % tabs.length;
        if (event.key === "ArrowLeft") destination = (index - 1 + tabs.length) % tabs.length;
        if (event.key === "Home") destination = 0;
        if (event.key === "End") destination = tabs.length - 1;
        if (destination === undefined) return;
        event.preventDefault();
        tabs[destination].focus();
        activate(tabs[destination]);
      });
    });
  }

  function togglePrototype(open) {
    const show = open === undefined ? $("#prototype-controls").hidden : open;
    $("#prototype-controls").hidden = !show;
    $("#prototype-toggle").setAttribute("aria-expanded", String(show));
    if (show) $("#prototype-close").focus();
    else $("#prototype-toggle").focus();
  }

  function openDialog(id, trigger) {
    closeMenu();
    const dialog = $(id);
    dialogTriggers.set(dialog, trigger || document.activeElement);
    dialog.showModal();
  }

  function showInfo(title, paragraphs, trigger) {
    $("#info-title").textContent = title;
    $("#info-content").replaceChildren(...paragraphs.map((text) => textElement("p", text)));
    openDialog("#info-dialog", trigger);
  }

  function closeMenu(restoreFocus = false) {
    const wasOpen = !$("#preset-menu").hidden;
    $("#preset-menu").hidden = true;
    $("#preset-toggle").setAttribute("aria-expanded", "false");
    if (restoreFocus && wasOpen) $("#preset-toggle").focus();
  }

  function renderPresets() {
    $("#preset-toggle").disabled = config.presets.length === 0;
    $("#preset-toggle").title = config.presets.find((item) => item.id === selectedPreset)?.name || "尚无预设，请在高级配置中添加";
    $("#preset-menu").replaceChildren(...config.presets.map((preset) => {
      const button = textElement("button", preset.name);
      button.type = "button";
      button.dataset.preset = preset.id;
      button.setAttribute("role", "menuitemradio");
      button.setAttribute("aria-checked", String(preset.id === selectedPreset));
      button.addEventListener("click", () => {
        selectedPreset = preset.id;
        closeMenu(true);
        renderPresets();
        generate(preset.name, preset.kind, preset.prompt);
      });
      return button;
    }));
  }

  function updateAutoState() {
    const text = !autoRunning ? "自动分析未启动" : autoConfig.mode === "interval" ? `自动 · 每 ${autoConfig.interval} 秒` : `自动 · 等待新数据（≥${autoConfig.interval}秒）`;
    $("#auto-state").textContent = text;
    $("#auto-dialog-state").textContent = text;
    $("#auto-start").textContent = autoRunning ? "停止自动" : "启动自动";
    $("#auto-open").textContent = autoRunning ? "自动分析 · 开" : "自动分析";
    $("#auto-open").classList.toggle("auto-on", autoRunning);
  }

  function stopAuto() {
    autoRunning = false;
    clearTimeout(autoTimer);
    autoTimer = null;
    updateAutoState();
  }

  function scheduleAuto() {
    clearTimeout(autoTimer);
    autoTimer = null;
    if (!autoRunning) return;
    if (autoConfig.mode === "data" && consumedRevision === revision) return;
    const delay = Math.max(0, lastAutoAt + autoConfig.interval * 1000 - Date.now());
    autoTimer = setTimeout(() => {
      autoTimer = null;
      if (!autoRunning) return;
      if (generationActive) {
        // Never overwrite an in-flight manual answer; retry at the configured low frequency.
        lastAutoAt = Date.now();
        scheduleAuto();
        return;
      }
      runAuto();
    }, delay);
  }

  function runAuto() {
    if (!autoRunning) return;
    lastAutoAt = Date.now();
    consumedRevision = revision;
    generate("自动会议分析", "summary", autoConfig.prompt, autoConfig.buffer);
    updateAutoState();
    scheduleAuto();
  }

  function signalData() {
    ++revision;
    $("#auto-event-note").textContent = `已触发第 ${revision} 个本地信号 · 不写入历史${autoRunning && autoConfig.mode === "data" ? "，按最短间隔合并分析" : ""}`;
    if (autoRunning && autoConfig.mode === "data") scheduleAuto();
  }

  function fillAutoForm() {
    $("#auto-enabled").checked = autoConfig.enabled;
    $("#auto-mode").value = autoConfig.mode;
    $("#auto-interval").value = String(autoConfig.interval);
    $("#auto-prompt").value = autoConfig.prompt;
    $("#auto-buffer").checked = autoConfig.buffer;
    updateAutoState();
  }

  function applyAutoForm() {
    if (!$("#auto-form").reportValidity()) return false;
    if (!$("#auto-prompt").value.trim()) {
      $("#auto-event-note").textContent = "请填写自动提示词。";
      $("#auto-prompt").focus();
      return false;
    }
    autoConfig = { enabled: $("#auto-enabled").checked, mode: $("#auto-mode").value,
      interval: Math.max(10, Number($("#auto-interval").value)), prompt: $("#auto-prompt").value.trim(), buffer: $("#auto-buffer").checked };
    if (!autoConfig.enabled) stopAuto();
    else if (autoRunning) scheduleAuto();
    updateAutoState();
    return true;
  }

  function setConfigTab(name) {
    $$(".config-tabs button").forEach((tab) => {
      const selected = tab.dataset.config === name;
      tab.setAttribute("aria-selected", String(selected));
      tab.tabIndex = selected ? 0 : -1;
      $(`#config-${tab.dataset.config}`).hidden = !selected;
    });
  }

  function savePresetDraft() {
    const preset = configDraft?.presets.find((item) => item.id === editingPreset);
    if (!preset) return;
    preset.name = $("#preset-name").value;
    preset.prompt = $("#preset-prompt").value;
  }

  function fillPresetEditor(id) {
    editingPreset = id;
    const selector = $("#preset-editor-select");
    selector.replaceChildren(...configDraft.presets.map((item) => new Option(item.name || "未命名预设", item.id)));
    selector.value = id || "";
    const item = configDraft.presets.find((preset) => preset.id === id);
    $("#preset-name").value = item?.name || "";
    $("#preset-prompt").value = item?.prompt || "";
    ["#preset-editor-select", "#preset-name", "#preset-prompt", "#preset-delete"].forEach((name) => { $(name).disabled = !item; });
  }

  function openAdvanced(trigger) {
    configDraft = structuredClone(config);
    $("#system-prompt").value = configDraft.system;
    $("#user-template").value = configDraft.template;
    $("#model-name").value = configDraft.model;
    fillPresetEditor(configDraft.presets.find((item) => item.id === selectedPreset)?.id || configDraft.presets[0]?.id);
    $("#config-state").textContent = "修改仅在保存后应用；取消不提交。";
    setConfigTab("prompts");
    openDialog("#advanced-dialog", trigger);
    $("#system-prompt").focus();
  }

  function saveAdvanced() {
    savePresetDraft();
    const system = $("#system-prompt").value.trim();
    const template = $("#user-template").value;
    const model = $("#model-name").value.trim();
    if (!system || !template.includes("{history}") || !template.includes("{question}")) {
      setConfigTab("prompts");
      $("#config-state").textContent = "请填写系统提示词，并在模板中保留 {history} 与 {question}。";
      (!system ? $("#system-prompt") : $("#user-template")).focus();
      return;
    }
    if (!model) {
      setConfigTab("model");
      $("#config-state").textContent = "请填写本地模型占位名称。";
      $("#model-name").focus();
      return;
    }
    const incomplete = configDraft.presets.find((item) => !item.name.trim() || !item.prompt.trim());
    if (incomplete) {
      setConfigTab("presets");
      fillPresetEditor(incomplete.id);
      $("#config-state").textContent = "每项预设需要名称与提示词，或删除空白预设。";
      $("#preset-name").focus();
      return;
    }
    config = { ...configDraft, system, template, model };
    if (!config.presets.some((item) => item.id === selectedPreset)) selectedPreset = config.presets[0]?.id;
    renderPresets();
    $("#advanced-dialog").close("save");
    notice("洞察配置已保存 · 下次生成应用");
  }

  function clampFloat(x, y) {
    const paper = $("#insight-float");
    if (paper.hidden) return;
    // All float geometry uses viewport pixels, not the 2560px scaled stage.
    const anchor = $("#reading-workspace").getBoundingClientRect();
    const defaultTop = Math.max(12, anchor.top + 12);
    const safeBottom = Math.min(window.innerHeight - 12, $("#session-controls").getBoundingClientRect().top - 12);
    const height = Math.max(80, Math.min(480, safeBottom - defaultTop));
    paper.style.width = `${Math.min(560, Math.max(300, anchor.width - 24), window.innerWidth - 24)}px`;
    paper.style.height = `${height}px`;
    const width = paper.getBoundingClientRect().width;
    const left = x ?? (parseFloat(paper.style.left) || anchor.right - width - 12);
    const top = y ?? (parseFloat(paper.style.top) || defaultTop);
    paper.style.left = `${Math.max(12, Math.min(window.innerWidth - width - 12, left))}px`;
    paper.style.top = `${Math.max(12, Math.min(safeBottom - height, top))}px`;
  }

  function closeFloat() {
    $("#insight-float").hidden = true;
    $("#float-open").setAttribute("aria-expanded", "false");
    (activeTab === "insight" ? $("#float-open") : $("#history-tab")).focus();
  }

  function updateFloatSize(delta) {
    floatSize = Math.max(14, Math.min(28, floatSize + delta));
    $("#insight-float").style.setProperty("--float-size", `${floatSize}px`);
    $("#float-smaller").disabled = floatSize === 14;
    $("#float-larger").disabled = floatSize === 28;
  }

  function resetPrototype() {
    stopAuto();
    drag = null;
    cancelGeneration("已恢复会议摘要示例");
    clearTimeout(connectionTimer);
    connectionTimer = null;
    $$("dialog[open]").forEach((dialog) => dialog.close("reset"));
    config = structuredClone(defaults);
    configDraft = null;
    autoConfig = { ...autoDefaults };
    selectedPreset = "summary";
    revision = consumedRevision = refreshCount = 0;
    lastAutoAt = 0;
    allHistory = historyCleared = false;
    $("#translation-settings").reset();
    $("#question-form").reset();
    $("input[name=fixture][value=reference]").checked = true;
    $("#actual-size").checked = false;
    $("#subtitle-toggle").checked = false;
    $("#subtitle-preview").hidden = true;
    $("#insight-float").hidden = true;
    $("#insight-float").classList.remove("pinned");
    $("#float-pin").setAttribute("aria-pressed", "false");
    $("#float-open").setAttribute("aria-expanded", "false");
    $("#insight-float").style.removeProperty("left");
    $("#insight-float").style.removeProperty("top");
    floatSize = 18;
    updateFloatSize(0);
    stage.classList.remove("night-paper");
    $("#theme-toggle").setAttribute("aria-pressed", "false");
    $$(".bookmark").forEach((button) => {
      button.classList.toggle("selected", button.dataset.page === "实时翻译");
      button.removeAttribute("aria-current");
    });
    $(".bookmark[data-page='实时翻译']").setAttribute("aria-current", "page");
    setSession("idle");
    toggleSettings(false);
    updateSettings();
    renderPresets();
    fillAutoForm();
    buildHistory();
    result = buildResult();
    renderResult();
    $("#history-content").scrollTop = $("#insight-scroll").scrollTop = 0;
    readingPositions.history = readingPositions.insight = 0;
    setReadingTab("insight");
    setDisplay("bilingual");
    renderDialogue();
    fitCanvas();
    notice("已恢复默认内容与状态");
  }

  wireTabs(".segment-control button", (button) => setDisplay(button.dataset.display));
  wireTabs(".history-tabs button", (button) => setReadingTab(button.dataset.tab));
  wireTabs(".config-tabs button", (button) => setConfigTab(button.dataset.config));
  $("#start-button").addEventListener("click", () => {
    if (session === "idle") {
      setSession("connecting");
      notice("模拟连接 · 可取消，不请求云端");
      connectionTimer = setTimeout(() => { setSession("running"); connectionTimer = null; notice("本地会话运行中"); }, 850);
    } else {
      clearTimeout(connectionTimer);
      connectionTimer = null;
      notice(session === "connecting" ? "已取消连接" : "已停止翻译 · 洞察独立运行");
      setSession("idle");
    }
  });
  $("#settings-toggle").addEventListener("click", () => toggleSettings());
  $("#translation-settings").addEventListener("change", () => { updateSettings(); notice("已更新本地设置 · 示例语句不改写"); });
  $("#translation-settings").addEventListener("submit", (event) => event.preventDefault());
  $("#refresh-devices").addEventListener("click", () => {
    ++refreshCount;
    $("#settings-note").textContent = `本地示例设备列表已刷新 ${refreshCount} 次 · 麦克风 2 项、系统回环 2 项；不枚举真实设备。`;
  });
  $("#subtitle-toggle").addEventListener("change", () => { $("#subtitle-preview").hidden = !$("#subtitle-toggle").checked; });
  $("#subtitle-close").addEventListener("click", () => { $("#subtitle-toggle").checked = false; $("#subtitle-preview").hidden = true; $("#subtitle-toggle").focus(); });
  $("#history-all").addEventListener("click", () => {
    allHistory = !allHistory;
    updateHistory();
  });
  $("#history-clear").addEventListener("click", () => openDialog("#history-clear-dialog", $("#history-clear")));
  $("#history-confirm-clear").addEventListener("click", () => { historyCleared = true; updateHistory(); $("#history-clear-dialog").close(); notice("只清空了本地演示历史"); });
  $("#history-restore").addEventListener("click", () => { historyCleared = false; updateHistory(); notice("已恢复 30 条示例"); });
  $("#history-folder").addEventListener("click", () => showInfo("演示历史文件列表", ["2026-11-05 · 跨语言合作会议 · 30 条双语记录", "2026-11-04 · 试点准备会议 · 示例条目", "这里仅展示本地模拟文件列表，不打开系统目录，也不读取真实历史文件。"], $("#history-folder")));
  $("#demo-info").addEventListener("click", () => showInfo("本地交互演示", ["本页使用预置会议、双语记录和计时器模拟状态，不连接云端，不录制或播放音频，不保存用户配置。", "实时示例按麦克风或系统音频标注输入来源，不识别人；听析中心的文件分人能力不在本页演示范围内。", "浮动洞察与字幕是页面内窗口，不是系统浮窗。刷新页面恢复默认。"], $("#demo-info")));
  $("#preset-toggle").addEventListener("click", () => {
    if (!$("#preset-menu").hidden) { closeMenu(true); return; }
    $("#preset-menu").hidden = false;
    $("#preset-toggle").setAttribute("aria-expanded", "true");
    $("#preset-menu button").focus();
  });
  $("#preset-menu").addEventListener("keydown", (event) => {
    const buttons = $$("#preset-menu button");
    const index = buttons.indexOf(document.activeElement);
    const positions = { ArrowDown: (index + 1) % buttons.length, ArrowUp: (index - 1 + buttons.length) % buttons.length, Home: 0, End: buttons.length - 1 };
    if (event.key in positions) { event.preventDefault(); buttons[positions[event.key]].focus(); }
    if (event.key === "Tab") {
      event.preventDefault();
      closeMenu();
      (event.shiftKey ? $("#preset-toggle") : $("#auto-open")).focus();
    }
  });
  document.addEventListener("pointerdown", (event) => {
    if (!event.target.closest("#preset-menu, #preset-toggle")) closeMenu();
  });
  $("#question-form").addEventListener("submit", (event) => {
    event.preventDefault();
    const question = $("#insight-question").value.trim();
    if (!question) { generationStatus("请先写下问题"); $("#insight-question").focus(); return; }
    if (!generationActive) generate("自定义提问反馈", "custom", question);
  });
  $("#generation-stop").addEventListener("click", () => cancelGeneration());
  $("#insight-clear").addEventListener("click", () => {
    cancelGeneration();
    result = null;
    $("#insight-question").value = "";
    renderResult();
    generationStatus("结果与提问已清除 · 历史、自动模式不变");
  });
  $("#auto-open").addEventListener("click", () => { fillAutoForm(); openDialog("#auto-dialog", $("#auto-open")); });
  $("#auto-form").addEventListener("submit", (event) => { event.preventDefault(); if (applyAutoForm()) { $("#auto-dialog").close("save"); notice("自动配置已保存"); } });
  $("#auto-start").addEventListener("click", () => {
    if (autoRunning) { stopAuto(); notice("自动调度已停止 · 当前生成不取消"); return; }
    if (!applyAutoForm()) return;
    if (!autoConfig.enabled) { $("#auto-event-note").textContent = "请先勾选启用自动洞察。"; return; }
    autoRunning = true;
    runAuto();
    $("#auto-event-note").textContent = "已应用配置并立即分析一次 · 关闭浮纸查看结果。";
  });
  $("#simulate-data").addEventListener("click", signalData);
  $("#advanced-open").addEventListener("click", () => openAdvanced($("#advanced-open")));
  $("#advanced-form").noValidate = true;
  $("#advanced-form").addEventListener("submit", (event) => { event.preventDefault(); saveAdvanced(); });
  $("#preset-editor-select").addEventListener("change", () => { savePresetDraft(); fillPresetEditor($("#preset-editor-select").value); });
  ["#preset-name", "#preset-prompt"].forEach((id) => $(id).addEventListener("input", savePresetDraft));
  $("#preset-add").addEventListener("click", () => {
    savePresetDraft();
    const id = `custom-${++presetSequence}`;
    configDraft.presets.push({ id, name: "新预设", prompt: "围绕会议内容整理需要关注的问题。", kind: "custom" });
    fillPresetEditor(id);
    $("#preset-name").focus();
  });
  $("#preset-delete").addEventListener("click", () => {
    configDraft.presets = configDraft.presets.filter((item) => item.id !== editingPreset);
    fillPresetEditor(configDraft.presets[0]?.id);
    $("#config-state").textContent = "所选预设已从草稿移除，保存后生效。";
  });
  $$("[data-close-dialog]").forEach((button) => button.addEventListener("click", () => button.closest("dialog").close("cancel")));
  $$("dialog").forEach((dialog) => dialog.addEventListener("close", () => {
    if (dialog.id === "advanced-dialog") configDraft = null;
    const trigger = dialogTriggers.get(dialog);
    if (trigger && trigger.getClientRects().length && !trigger.disabled) trigger.focus();
    else (activeTab === "history" ? $("#history-tab") : $("#insight-tab")).focus();
  }));
  $("#float-open").addEventListener("click", () => {
    if (!$("#insight-float").hidden) { closeFloat(); return; }
    $("#insight-float").hidden = false;
    $("#float-open").setAttribute("aria-expanded", "true");
    updateFloatSize(0);
    clampFloat();
    $("#float-close").focus();
  });
  $("#float-close").addEventListener("click", closeFloat);
  $("#float-pin").addEventListener("click", () => {
    const pinned = $("#insight-float").classList.toggle("pinned");
    $("#float-pin").setAttribute("aria-pressed", String(pinned));
  });
  $("#float-smaller").addEventListener("click", () => updateFloatSize(-2));
  $("#float-larger").addEventListener("click", () => updateFloatSize(2));
  let drag = null;
  $("#float-drag").addEventListener("pointerdown", (event) => {
    if (event.target.closest("button") || event.button !== 0) return;
    const rect = $("#insight-float").getBoundingClientRect();
    drag = { x: event.clientX - rect.left, y: event.clientY - rect.top };
    $("#float-drag").setPointerCapture(event.pointerId);
    event.preventDefault();
  });
  $("#float-drag").addEventListener("pointermove", (event) => { if (drag) clampFloat(event.clientX - drag.x, event.clientY - drag.y); });
  $("#float-drag").addEventListener("pointerup", () => { drag = null; });
  $("#float-drag").addEventListener("lostpointercapture", () => { drag = null; });
  $$("input[name=fixture]").forEach((input) => input.addEventListener("change", () => { renderDialogue(input.value); signalData(); }));
  $("#empty-insight").addEventListener("change", () => {
    const empty = $("#empty-insight").checked;
    cancelGeneration();
    result = empty ? null : buildResult();
    renderResult();
    generationStatus(empty ? "空洞察示例" : "已恢复会议摘要示例");
  });
  $("#actual-size").addEventListener("change", fitCanvas);
  $("#prototype-toggle").addEventListener("click", () => togglePrototype());
  $("#prototype-close").addEventListener("click", () => togglePrototype(false));
  $("#settings-link").addEventListener("click", () => { toggleSettings(true); $("#font-size").focus(); notice("译事设置 · 本页演示"); });
  $("#theme-toggle").addEventListener("click", () => {
    const enabled = stage.classList.toggle("night-paper");
    $("#theme-toggle").setAttribute("aria-pressed", String(enabled));
    notice(enabled ? "暮纸预览" : "暖纸预览");
  });
  $$(".bookmark").forEach((button) => button.addEventListener("click", () => {
    $$(".bookmark").forEach((item) => { item.classList.remove("selected"); item.removeAttribute("aria-current"); });
    button.classList.add("selected");
    if (button.dataset.page === "实时翻译") button.setAttribute("aria-current", "page");
    else $(".bookmark[data-page='实时翻译']").setAttribute("aria-current", "page");
    notice(button.dataset.page === "实时翻译" ? "实时翻译 · 本地阅读页" : `${button.dataset.page}：只演示书签状态，未切换业务页面`);
  }));
  $$("[data-window]").forEach((button) => button.addEventListener("click", () => notice("窗口按钮仅外观模拟，不操作系统窗口")));
  $("#reset-prototype").addEventListener("click", resetPrototype);
  document.addEventListener("keydown", (event) => {
    if (event.ctrlKey && event.shiftKey && event.code === "KeyP") { event.preventDefault(); togglePrototype(); }
    if (event.key !== "Escape" || $("dialog[open]")) return;
    if (!$("#preset-menu").hidden) closeMenu(true);
    else if (!$("#insight-float").hidden) closeFloat();
    else if (!$("#prototype-controls").hidden) togglePrototype(false);
    else if (!$("#translation-settings").hidden) { toggleSettings(false); $("#settings-toggle").focus(); }
  });
  window.addEventListener("resize", fitCanvas);
  window.addEventListener("scroll", () => clampFloat(), { passive: true });
  window.addEventListener("pagehide", () => { stopAuto(); clearTimeout(generationTimer); clearTimeout(connectionTimer); ++generationToken; });
  fillWave($(".seal-wave"));
  renderDialogue();
  buildHistory();
  result = buildResult();
  renderResult();
  renderPresets();
  updateSettings();
  updateAutoState();
  setSession("idle");
  setDisplay("bilingual");
  fitCanvas();
})();