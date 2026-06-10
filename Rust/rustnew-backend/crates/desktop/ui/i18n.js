/* ============================================================
   i18n 字典 (中 / 英)
   后端用 config.general.language 持久化当前语言。
   ============================================================ */
export const I18N = {
  zh: {
    "app.title": "译见 <span class='pro'>Pro</span>",
    "nav.live": "实时翻译", "nav.studio": "创作工坊", "nav.media": "媒体中心",
    "nav.audiolab": "听析中心", "nav.tasks": "任务监控", "nav.theme": "主题", "nav.settings": "设置",
    "lt.speechRes": "语音资源:", "lt.resName": "东南亚 (国际版)", "lt.srcLang": "源语言:", "lt.tgtLang": "目标语言:",
    "lang.auto": "自动识别", "lang.zh": "中文", "lt.stop": "停止翻译", "lt.start": "开始翻译",
    "lt.audioDevice": "音频设备", "lt.pipeline": "麦克风 + 环回 (识别中)",
    "lt.liveTitle": "实时翻译", "lt.viewOrig": "原文", "lt.viewTrans": "译文", "lt.viewBi": "双语",
    "src.system": "系统音", "src.mic": "麦克风",
    "lt.tabHistory": "历史记录", "lt.tabInsight": "洞察", "lt.aiReady": "AI 服务已就绪",
    "preset.summary": "会议摘要", "preset.knowledge": "知识点提取", "preset.complaint": "客户投诉识别",
    "preset.actions": "行动项提取", "preset.emotion": "情绪分析",
    "lt.askPh": "输入自定义分析问题...", "lt.send": "发送",
    "insight.h": "会议摘要",
    "insight.p1": "本季度业绩超预期，主要由亚太区驱动",
    "insight.p2": "亚太区增长来自新签三家企业客户",
    "insight.p3": "下季度战略重点：客户留存优先于拉新",
    "lt.autoInsight": "自动洞察:", "lt.autoTimer": "定时循环", "lt.interval": "间隔(秒):", "lt.enable": "开启",
    "lt.clearHist": "清空历史", "lt.viewHist": "查看历史", "lt.subtitle": "字幕", "lt.floatInsight": "浮动洞察",
    "lt.statusMsg": "识别中 · 已处理 12 段 · 东南亚 (southeastasia)",
    "lt.statusIdle": "空闲", "lt.statusRunning": "识别中…", "lt.refreshHist": "刷新历史",
    "lt.emptyHint": "点击「开始翻译」后，识别与翻译结果会实时显示在这里。",
    "lt.histEmpty": "暂无历史记录。", "lt.insightSoon": "AI 洞察分析功能将在后续版本接入。",
    "lt.noRes": "未配置语音资源", "lt.resInvalid": "凭据不完整（需密钥 + 区域或终结点）",
    "set.general": "通用", "set.uiLang": "界面语言", "set.langZh": "中文", "set.langEn": "English",
    "set.theme": "主题", "set.themeLight": "浅色", "set.themeDark": "深色",
    "set.filterParticles": "过滤句末语气词", "set.speechRes": "语音资源", "set.addRes": "新增",
    "set.noRes": "尚未配置语音资源。点击「新增」添加 Microsoft 语音密钥。",
    "set.resName": "名称", "set.resNamePh": "例如：东南亚 (国际版)", "set.resKey": "订阅密钥",
    "set.resRegion": "区域", "set.resEndpoint": "终结点 (可选)", "set.save": "保存", "set.cancel": "取消",
    "set.activate": "设为当前", "set.inUse": "使用中", "set.edit": "编辑", "set.delete": "删除",
    "al.audioList": "音频列表", "al.loadFile": "从文件加载", "al.breadcrumb": "媒体库 › 会议 › 2026-06-10",
    "al.tabSummary": "总结", "al.tabTranscript": "转录", "al.tabMindmap": "导图", "al.tabInsight": "顿悟",
    "al.tabResearch": "研究", "al.tabPodcast": "播客", "al.tabTranslate": "翻译", "al.openFile": "打开文件",
    "al.speaker1": "发言人 1", "al.speaker2": "发言人 2",
    "al.lifecycle": "生命周期", "al.stage1": "转录", "al.stage2": "AI 总结", "al.stage3": "思维导图",
    "al.stage4": "顿悟分析", "al.stage5": "深度研究", "al.stage6": "播客台本", "al.stage7": "翻译",
    "al.autofill": "自动补齐缺失", "al.podcastVoice": "播客语音", "al.language": "语言",
    "al.allLang": "全部语言", "al.synth": "合成播客音频", "al.statusReady": "就绪 · 转录 / 总结 / 导图 已完成",
    "tm.title": "任务分类", "tm.pending": "待处理", "tm.running": "进行中", "tm.done": "已完成", "tm.failed": "失败",
    "tm.concurrency": "并发设置", "tm.transcribe": "转录", "tm.timeout": "超时设置", "tm.min": "分钟",
    "tm.tokenUsage": "Token 用量", "tm.execStat": "执行 64 次 · 计费 31 次", "tm.tokenStat": "总耗: 248,512 tokens",
    "tm.colId": "任务ID", "tm.colAudio": "音频", "tm.colStage": "阶段", "tm.colStatus": "状态",
    "tm.colTime": "发起时间", "tm.colElapsed": "耗时",
    "tm.stageTranscribe": "转录", "tm.stageAi": "AI总结", "tm.stageDone": "完成",
    "tm.stRunning": "进行中", "tm.stSuccess": "成功", "tm.stError": "失败",
    "tm.dId": "任务ID：", "tm.dAudio": "音频：", "tm.dStage": "阶段 / 状态：", "tm.dTime": "时间：",
    "tm.retry": "重试: 0", "tm.submit": "提交", "tm.start": "开始", "tm.elapsed": "耗时",
    "tm.retryBtn": "重试", "tm.cancelBtn": "取消",
    "stub.studio": "创作工坊（图像/视频生成）— 后续实现", "stub.media": "媒体中心 — 后续实现"
  },
  en: {
    "app.title": "Yijian <span class='pro'>Pro</span>",
    "nav.live": "Live", "nav.studio": "Studio", "nav.media": "Media",
    "nav.audiolab": "Audio Lab", "nav.tasks": "Tasks", "nav.theme": "Theme", "nav.settings": "Settings",
    "lt.speechRes": "Speech:", "lt.resName": "Southeast Asia (Global)", "lt.srcLang": "Source:", "lt.tgtLang": "Target:",
    "lang.auto": "Auto Detect", "lang.zh": "Chinese", "lt.stop": "Stop", "lt.start": "Start",
    "lt.audioDevice": "Audio Devices", "lt.pipeline": "Mic + Loopback (listening)",
    "lt.liveTitle": "Live Translation", "lt.viewOrig": "Source", "lt.viewTrans": "Target", "lt.viewBi": "Bilingual",
    "src.system": "System", "src.mic": "Mic",
    "lt.tabHistory": "History", "lt.tabInsight": "Insights", "lt.aiReady": "AI service ready",
    "preset.summary": "Meeting Summary", "preset.knowledge": "Key Points", "preset.complaint": "Complaints",
    "preset.actions": "Action Items", "preset.emotion": "Sentiment",
    "lt.askPh": "Ask a custom question...", "lt.send": "Send",
    "insight.h": "Meeting Summary",
    "insight.p1": "Quarterly results beat expectations, driven mainly by APAC",
    "insight.p2": "APAC growth came from three newly signed enterprise clients",
    "insight.p3": "Next quarter focus: retention over acquisition",
    "lt.autoInsight": "Auto Insight:", "lt.autoTimer": "Interval Loop", "lt.interval": "Every (s):", "lt.enable": "On",
    "lt.clearHist": "Clear", "lt.viewHist": "Open Folder", "lt.subtitle": "Captions", "lt.floatInsight": "Float Insight",
    "lt.statusMsg": "Listening · 12 segments · Southeast Asia (southeastasia)",
    "lt.statusIdle": "Idle", "lt.statusRunning": "Listening…", "lt.refreshHist": "Refresh",
    "lt.emptyHint": "Click \"Start\" — recognition and translation will stream here in real time.",
    "lt.histEmpty": "No history yet.", "lt.insightSoon": "AI insight analysis is coming in a future release.",
    "lt.noRes": "No speech resource", "lt.resInvalid": "Incomplete credentials (need key + region or endpoint)",
    "set.general": "General", "set.uiLang": "Interface Language", "set.langZh": "中文", "set.langEn": "English",
    "set.theme": "Theme", "set.themeLight": "Light", "set.themeDark": "Dark",
    "set.filterParticles": "Filter trailing particles", "set.speechRes": "Speech Resources", "set.addRes": "Add",
    "set.noRes": "No speech resource configured. Click \"Add\" to enter a Microsoft Speech key.",
    "set.resName": "Name", "set.resNamePh": "e.g. Southeast Asia (Global)", "set.resKey": "Subscription Key",
    "set.resRegion": "Region", "set.resEndpoint": "Endpoint (optional)", "set.save": "Save", "set.cancel": "Cancel",
    "set.activate": "Set Active", "set.inUse": "In Use", "set.edit": "Edit", "set.delete": "Delete",
    "al.audioList": "Audio Files", "al.loadFile": "Load File", "al.breadcrumb": "Library › Meetings › 2026-06-10",
    "al.tabSummary": "Summary", "al.tabTranscript": "Transcript", "al.tabMindmap": "Mind Map", "al.tabInsight": "Insight",
    "al.tabResearch": "Research", "al.tabPodcast": "Podcast", "al.tabTranslate": "Translate", "al.openFile": "Open File",
    "al.speaker1": "Speaker 1", "al.speaker2": "Speaker 2",
    "al.lifecycle": "Lifecycle", "al.stage1": "Transcribe", "al.stage2": "AI Summary", "al.stage3": "Mind Map",
    "al.stage4": "Insight", "al.stage5": "Research", "al.stage6": "Podcast Script", "al.stage7": "Translate",
    "al.autofill": "Auto-fill Missing", "al.podcastVoice": "Podcast Voice", "al.language": "Language",
    "al.allLang": "All Languages", "al.synth": "Synthesize Audio", "al.statusReady": "Ready · Transcript / Summary / Map done",
    "tm.title": "Task Buckets", "tm.pending": "Pending", "tm.running": "Running", "tm.done": "Completed", "tm.failed": "Failed",
    "tm.concurrency": "Concurrency", "tm.transcribe": "STT", "tm.timeout": "Timeout", "tm.min": "min",
    "tm.tokenUsage": "Token Usage", "tm.execStat": "64 runs · 31 billed", "tm.tokenStat": "Total: 248,512 tokens",
    "tm.colId": "Task ID", "tm.colAudio": "Audio", "tm.colStage": "Stage", "tm.colStatus": "Status",
    "tm.colTime": "Submitted", "tm.colElapsed": "Elapsed",
    "tm.stageTranscribe": "STT", "tm.stageAi": "AI Sum", "tm.stageDone": "Done",
    "tm.stRunning": "Running", "tm.stSuccess": "Success", "tm.stError": "Failed",
    "tm.dId": "Task ID:", "tm.dAudio": "Audio:", "tm.dStage": "Stage / Status:", "tm.dTime": "Time:",
    "tm.retry": "Retries: 0", "tm.submit": "Submit", "tm.start": "Start", "tm.elapsed": "Elapsed",
    "tm.retryBtn": "Retry", "tm.cancelBtn": "Cancel",
    "stub.studio": "Studio (image/video generation) — coming soon", "stub.media": "Media Center — coming soon"
  }
};

export function applyLang(lang) {
  document.documentElement.setAttribute("data-lang", lang);
  document.documentElement.setAttribute("lang", lang === "zh" ? "zh-CN" : "en");
  const dict = I18N[lang] || I18N.zh;
  document.querySelectorAll("[data-i18n]").forEach(el => {
    const key = el.getAttribute("data-i18n");
    if (dict[key] !== undefined) el.innerHTML = dict[key];
  });
  document.querySelectorAll("[data-i18n-ph]").forEach(el => {
    const key = el.getAttribute("data-i18n-ph");
    if (dict[key] !== undefined) el.setAttribute("placeholder", dict[key]);
  });
}

export function applyTheme(theme) {
  document.documentElement.setAttribute("data-theme", theme);
}
