// ============================================================================
// usage-view.js — 用量统计页签（1:1 复刻 VoiceImeUsagePage：今日/7天/30天 + 5 子页签 + 原文分页）
// ============================================================================

import { invokeCmd, formatNumber, formatDuration, escapeHtml } from './api.js';

export function createUsageController() {
  let currentDays = 7;
  let selectedSheet = 0; // 0:概览 1:每日趋势 2:小时分布 3:统计分布 4:会话记录
  let hourlyBySentences = false;
  let latestReport = null;
  let selectedSessionId = null;
  let nextSequence = 0;

  const sheetContainer = () => document.getElementById('usage-sheet-host');
  const statusBanner = () => document.getElementById('usage-status-banner');
  const rangeCaption = () => document.getElementById('usage-range-caption');
  const footerText = () => document.getElementById('usage-footer-status');

  function bindEvents() {
    document.querySelectorAll('[data-usage-days]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const d = Number(btn.dataset.usageDays);
        if (d !== currentDays) {
          currentDays = d;
          syncRangeButtons();
          refresh();
        }
      });
    });

    document.getElementById('usage-refresh-btn').addEventListener('click', () => refresh());

    document.querySelectorAll('[data-usage-sheet]').forEach((btn) => {
      btn.addEventListener('click', () => {
        selectedSheet = Number(btn.dataset.usageSheet);
        syncSheetButtons();
        renderActiveSheet();
      });
    });

    syncRangeButtons();
    syncSheetButtons();
  }

  function syncRangeButtons() {
    document.querySelectorAll('[data-usage-days]').forEach((btn) => {
      btn.classList.toggle('selected', Number(btn.dataset.usageDays) === currentDays);
    });
  }

  function syncSheetButtons() {
    document.querySelectorAll('[data-usage-sheet]').forEach((btn) => {
      btn.classList.toggle('accent', Number(btn.dataset.usageSheet) === selectedSheet);
    });
  }

  function showStatus(msg, isError = false) {
    const el = statusBanner();
    if (!msg) {
      el.style.display = 'none';
      return;
    }
    el.style.display = 'block';
    el.className = isError ? 'status-banner error' : 'status-banner';
    el.textContent = msg;
  }

  async function refresh() {
    selectedSessionId = null;
    nextSequence = 0;
    rangeCaption().textContent = currentDays === 1 ? '今日 · 本地自然日' : `最近 ${currentDays} 天 · 含今日`;
    showStatus('正在读取本地统计…', false);
    sheetContainer().innerHTML = '';

    try {
      const report = await invokeCmd('load_usage_report', { days: currentDays });
      latestReport = report;
      rangeCaption().textContent = `${report.startDate.replaceAll('-', '.')} — ${report.endDate.replaceAll('-', '.')}`;
      footerText().textContent = `更新于 ${report.capturedAtTime} · 汇总不读取正文 · 选中会话自动展开已保存原文`;

      if (report.availability === 'databaseMissing') {
        showStatus('还没有本地用量数据库。使用语音输入后，点击刷新即可查看；报表不会主动创建数据库。');
        return;
      }
      if (report.availability === 'schemaMissing') {
        showStatus('当前数据库尚未包含输入法用量表。请使用语音输入后刷新；报表不会主动迁移数据库。');
        return;
      }
      if (report.matchingSessions === 0 && report.utterances === 0) {
        showStatus('这段时间还没有语音输入记录。按 F8 开始一次语音输入，让表达留下刻度。');
      } else {
        showStatus('');
      }
      renderActiveSheet();
    } catch (err) {
      showStatus(String(err), true);
      footerText().textContent = '读取失败 · 未修改数据，也未中断语音会话';
    }
  }

  function renderActiveSheet() {
    const host = sheetContainer();
    if (!latestReport || latestReport.availability !== 'ready') {
      host.innerHTML = '';
      return;
    }
    switch (selectedSheet) {
      case 0:
        host.innerHTML = buildOverviewHtml(latestReport);
        break;
      case 1:
        host.innerHTML = buildDailyHtml(latestReport);
        break;
      case 2:
        host.innerHTML = buildHourlyHtml(latestReport);
        document.getElementById('hourly-mode-select')?.addEventListener('change', (e) => {
          hourlyBySentences = e.target.value === 'utterances';
          renderActiveSheet();
        });
        break;
      case 3:
        host.innerHTML = buildDistributionsHtml(latestReport);
        break;
      case 4:
        host.innerHTML = buildSessionsHtml(latestReport);
        bindSessionRowEvents();
        break;
    }
  }

  function buildOverviewHtml(r) {
    const prev = r.previousPeriod;
    const cmpLine = prev
      ? `<div class="metric-note" style="margin-top:4px">对比上期 ${escapeHtml(prev.startDate)} — ${escapeHtml(prev.endDate)} · 等长完整自然日</div>`
      : '';
    return `
      <div style="display:flex;flex-direction:column;gap:16px">
        <div class="metrics-grid-4">
          ${metricCard('识别定稿字数', formatNumber(r.characters), '字', '按定稿当天统计', prev?.charactersChangeLabel, r.characters > (prev?.characters ?? 0), true)}
          ${metricCard('录音时长', formatDuration(r.recordingMilliseconds), '', '按会话创建日归属', prev?.recordingChangeLabel, r.recordingMilliseconds > (prev?.recordingMilliseconds ?? 0), false)}
          ${metricCard('启动会话', formatNumber(r.sessionCount), '次', '含未开始录音的连接', prev?.sessionsChangeLabel, r.sessionCount > (prev?.sessions ?? 0), false)}
          ${metricCard('识别定稿句数', formatNumber(r.utterances), '句', '不含临时识别结果', null, false, false)}
        </div>
        ${cmpLine}
        <div class="ime-card">
          <div class="activity-grid-3">
            <div><div class="metric-note">活跃日</div><div style="font-size:20px;font-weight:600;margin-top:6px">${r.activeDays} / ${r.days.length} 天</div></div>
            <div><div class="metric-note">最长连续</div><div style="font-size:20px;font-weight:600;margin-top:6px">${r.longestActiveStreak} 天</div></div>
            <div><div class="metric-note">日均定稿</div><div style="font-size:20px;font-weight:600;margin-top:6px">${Number(r.dailyAverageCharacters).toFixed(1)} 字</div></div>
          </div>
        </div>
        <details class="ime-expander">
          <summary>统计口径</summary>
          <div style="display:flex;flex-direction:column;gap:6px;margin-top:10px;font-size:12px;color:var(--muted);line-height:1.55">
            <p>1. 定稿包含识别确认的句子及手动停止收尾时保存的末句，不含临时识别结果。字数采用可见字素簇计数，不含空白与标点。</p>
            <p>2. 字数、句数按定稿本地日期归属；录音时长和会话数按会话创建日归属，跨午夜不拆分。</p>
            <p>3. 关闭原文保存仍统计全部维度；汇总查询不读取正文，已授权保存的历史原文可在「会话记录」页签点选查看。</p>
          </div>
        </details>
      </div>
    `;
  }

  function metricCard(title, val, unit, note, cmp, isUp, primary) {
    return `
      <div class="metric-card">
        <div class="metric-note">${escapeHtml(title)}</div>
        <div class="metric-value-row">
          <span class="metric-value ${primary ? 'primary' : ''}">${escapeHtml(val)}</span>
          ${unit ? `<span class="metric-unit">${escapeHtml(unit)}</span>` : ''}
        </div>
        <div class="metric-note">${escapeHtml(note)}</div>
        ${cmp ? `<div class="metric-compare ${isUp ? 'up' : ''}">${escapeHtml(cmp)}</div>` : ''}
      </div>
    `;
  }

  function buildDailyHtml(r) {
    const peak = Math.max(0, ...r.days.map((d) => d.characters));
    const cols = r.days.length;
    const bars = r.days
      .map((d, idx) => {
        const showLabel = cols <= 7 || idx % 5 === 0 || idx === cols - 1;
        const h = peak === 0 ? 2 : Math.max(2, Math.round((132 * d.characters) / peak));
        const tip = `${d.date} · 定稿 ${formatNumber(d.characters)} 字 / ${formatNumber(d.utterances)} 句 · 会话 ${formatNumber(d.sessions)} 次`;
        return `
          <div class="bar-col" title="${escapeHtml(tip)}">
            ${cols <= 7 ? `<div class="bar-top-val ${d.characters > 0 ? 'has-val' : ''}">${formatNumber(d.characters)}</div>` : ''}
            <div class="bar-track">
              <div class="bar-fill ${d.characters > 0 ? 'active' : ''}" style="height:${h}px"></div>
            </div>
            <div class="bar-date-label">${showLabel ? escapeHtml(d.shortDate) : ''}</div>
          </div>
        `;
      })
      .join('');

    return `
      <div class="ime-card" style="display:flex;flex-direction:column;gap:16px">
        <div class="card-heading-row">
          <div class="card-title">每日定稿产出</div>
          <div class="text-muted" style="font-size:12px">最高 ${formatNumber(peak)} 字 / 天</div>
        </div>
        <div class="activity-strip activity-grid-3">
          <div><div class="metric-note">有字的日子</div><div style="font-size:16px;font-weight:600;margin-top:4px">${r.activeDays} / ${cols} 天</div></div>
          <div><div class="metric-note">选区最长连续</div><div style="font-size:16px;font-weight:600;margin-top:4px">${r.longestActiveStreak} 天 · ${r.activeRuns.length} 段</div></div>
          <div><div class="metric-note">日均定稿产出</div><div style="font-size:16px;font-weight:600;margin-top:4px">${Number(r.dailyAverageCharacters).toFixed(1)} 字</div></div>
        </div>
        <div class="bar-chart-grid" style="grid-template-columns:repeat(${cols},minmax(0,1fr))">${bars}</div>
        <div class="metric-note">按定稿时记录的本地日期 · 无记录日期以零展示 · 悬停柱体查看每日详情</div>
      </div>
    `;
  }

  function buildHourlyHtml(r) {
    const values = r.hours.map((h) => (hourlyBySentences ? h.utterances : h.characters));
    const peak = Math.max(0, ...values);
    const bars = r.hours
      .map((h) => {
        const val = hourlyBySentences ? h.utterances : h.characters;
        const height = peak === 0 ? 2 : Math.max(2, Math.round((108 * val) / peak));
        const label = h.hour % 3 === 0 || h.hour === 23 ? String(h.hour).padStart(2, '0') : '';
        const hh = String(h.hour).padStart(2, '0');
        const tip = `${hh}:00–${hh}:59 · ${formatNumber(h.characters)} 字 / ${formatNumber(h.utterances)} 句`;
        return `
          <div class="bar-col" title="${escapeHtml(tip)}">
            <div class="bar-track hourly">
              <div class="bar-fill ${val > 0 ? 'active' : ''}" style="height:${height}px"></div>
            </div>
            <div class="bar-date-label">${label}</div>
          </div>
        `;
      })
      .join('');

    return `
      <div class="ime-card" style="display:flex;flex-direction:column;gap:14px">
        <div class="card-heading-row">
          <div>
            <div class="card-title">一天里，何时表达最多</div>
            <div class="metric-note" style="margin-top:4px">24 小时定稿分布 · 每条记录自己的本地时间</div>
          </div>
          <select id="hourly-mode-select" style="width:120px">
            <option value="characters" ${!hourlyBySentences ? 'selected' : ''}>按字数</option>
            <option value="utterances" ${hourlyBySentences ? 'selected' : ''}>按句数</option>
          </select>
        </div>
        <div class="bar-chart-grid" style="grid-template-columns:repeat(24,minmax(0,1fr))">${bars}</div>
        <div class="metric-note">最高 ${formatNumber(peak)} ${hourlyBySentences ? '句' : '字'} / 小时 · 悬停同时查看字数与句数</div>
      </div>
    `;
  }

  function buildDistributionsHtml(r) {
    const renderBuckets = (title, note, buckets, total) => {
      const visible = buckets.filter(
        (b) => !(b.count === 0 && ['zero', 'interrupted', 'other'].includes(b.key))
      );
      const rows = visible
        .map((b) => {
          const pct = total === 0 ? 0 : (b.count * 100) / total;
          return `
            <div class="dist-row">
              <div style="font-size:12px">${escapeHtml(b.label)}</div>
              <div class="dist-track"><div class="dist-fill" style="width:${pct.toFixed(1)}%"></div></div>
              <div style="font-size:11px;color:var(--muted);text-align:right">${formatNumber(b.count)} · ${Math.round(pct)}%</div>
            </div>
          `;
        })
        .join('');
      return `
        <div class="ime-card" style="display:flex;flex-direction:column;gap:12px">
          <div>
            <div class="card-title" style="font-size:16px">${escapeHtml(title)}</div>
            <div class="metric-note" style="margin-top:4px">${escapeHtml(note)}</div>
          </div>
          ${rows}
          ${total === 0 ? '<div class="metric-note">这段时间暂无记录</div>' : ''}
        </div>
      `;
    };

    return `
      <div class="two-col-grid">
        ${renderBuckets('表达的长度', '所选日期定稿 · 按保存的字素数分组', r.sentenceLengths, r.utterances)}
        ${renderBuckets('会话如何结束', '按创建日 · 未完成与异常中断独立保留', r.outcomes, r.sessionCount)}
      </div>
    `;
  }

  function buildSessionsHtml(r) {
    const countLabel =
      r.matchingSessions > r.sessions.length
        ? `最近 ${r.sessions.length} / 共 ${formatNumber(r.matchingSessions)} 条`
        : `共 ${formatNumber(r.matchingSessions)} 条`;

    const rows =
      r.sessions.length === 0
        ? `<div style="padding:24px 12px;color:var(--muted);font-size:13px">暂无会话记录</div>`
        : r.sessions
            .map((s) => {
              const dur = s.durationBelongsToRange ? formatDuration(s.recordingMilliseconds) : '—（范围外）';
              const tip = `创建于 ${s.createdAtFull} · 录音归属 ${s.createdLocalDate}`;
              return `
                <div class="session-item-wrap" data-session-wrap="${escapeHtml(s.sessionId)}">
                  <button type="button" class="session-row-btn" data-session-id="${escapeHtml(s.sessionId)}" data-session-created="${escapeHtml(s.createdAtFull)}" title="${escapeHtml(tip)}">
                    <span>${escapeHtml(s.createdAtDisplay)}</span>
                    <span>${formatNumber(s.characters)}</span>
                    <span>${formatNumber(s.utterances)}</span>
                    <span>${escapeHtml(dur)}</span>
                    <span>${escapeHtml(s.stateLabel)}</span>
                  </button>
                </div>
              `;
            })
            .join('');

    return `
      <div class="ime-card" style="display:flex;flex-direction:column;gap:10px">
        <div class="card-heading-row">
          <div class="card-title" style="font-size:16px">会话记录</div>
          <div class="metric-note">${escapeHtml(countLabel)}</div>
        </div>
        <div class="session-table-header">
          <span>会话创建时间</span>
          <span>定稿字数</span>
          <span>定稿句数</span>
          <span>归属录音时长</span>
          <span>状态</span>
        </div>
        <div>${rows}</div>
      </div>
    `;
  }

  function bindSessionRowEvents() {
    document.querySelectorAll('[data-session-id]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const sid = btn.dataset.sessionId;
        const created = btn.dataset.sessionCreated;
        selectSession(sid, created);
      });
    });
  }

  async function selectSession(sid, createdFull) {
    document.querySelectorAll('.session-text-drawer').forEach((el) => el.remove());
    document.querySelectorAll('.session-row-btn').forEach((el) => {
      el.classList.toggle('selected', el.dataset.sessionId === sid);
    });

    if (selectedSessionId === sid) {
      selectedSessionId = null;
      return;
    }
    selectedSessionId = sid;
    nextSequence = 0;

    const wrap = document.querySelector(`[data-session-wrap="${CSS.escape(sid)}"]`);
    if (!wrap) return;

    const drawer = document.createElement('div');
    drawer.className = 'session-text-drawer';
    drawer.innerHTML = `
      <div class="card-heading-row">
        <div style="font-weight:600;font-size:14px">识别原文 · 创建于 ${escapeHtml(createdFull)}</div>
        <button type="button" class="icon-btn" id="session-reload-text-btn" title="刷新原文">↻</button>
      </div>
      <div id="session-text-status" class="metric-note">正在读取此会话的识别原文…</div>
      <div id="session-text-rows" style="display:flex;flex-direction:column;gap:8px;max-height:260px;overflow-y:auto"></div>
      <div id="session-text-actions" style="display:none;gap:8px">
        <button type="button" id="session-next-page-btn">下一批（最多 100 句）</button>
      </div>
    `;
    wrap.appendChild(drawer);

    drawer.querySelector('#session-reload-text-btn').addEventListener('click', () => loadSessionPage(0));
    drawer.querySelector('#session-next-page-btn').addEventListener('click', () => loadSessionPage(nextSequence));

    await loadSessionPage(0);
  }

  async function loadSessionPage(afterSeq) {
    const sid = selectedSessionId;
    if (!sid) return;
    const statusEl = document.getElementById('session-text-status');
    const rowsEl = document.getElementById('session-text-rows');
    const actionsEl = document.getElementById('session-text-actions');
    if (!statusEl || !rowsEl) return;

    if (afterSeq === 0) rowsEl.innerHTML = '';
    statusEl.textContent = '正在读取此会话的识别原文…';
    statusEl.style.display = 'block';

    try {
      const page = await invokeCmd('load_session_original_text', {
        sessionId: sid,
        afterSequence: afterSeq,
      });
      if (selectedSessionId !== sid) return;

      nextSequence = page.nextSequence;
      if (actionsEl) actionsEl.style.display = page.hasMore ? 'flex' : 'none';

      if (page.entries.length === 0) {
        statusEl.textContent = afterSeq === 0 ? '此会话暂无识别定稿记录。' : '此会话已没有更多识别记录。';
        return;
      }
      statusEl.style.display = 'none';

      page.entries.forEach((entry) => {
        const card = document.createElement('div');
        card.className = 'utterance-card';
        const bodyText =
          entry.originalText === null
            ? '<span class="text-muted">当时未保存识别原文；现在开启保存也无法补回。</span>'
            : `<span class="selectable">${escapeHtml(entry.originalText || '（空文本）')}</span>`;
        card.innerHTML = `
          <div class="metric-note">第 ${entry.sequence} 句 · ${escapeHtml(entry.finalizedAtFull)} · 定稿 ${formatNumber(entry.characters)} 字</div>
          <div style="font-size:14px;line-height:1.5">${bodyText}</div>
        `;
        rowsEl.appendChild(card);
      });
    } catch (err) {
      statusEl.textContent = String(err);
      statusEl.style.display = 'block';
    }
  }

  return {
    init: bindEvents,
    onEnter: () => {
      if (!latestReport) refresh();
    },
    flushPending: async () => {},
  };
}
