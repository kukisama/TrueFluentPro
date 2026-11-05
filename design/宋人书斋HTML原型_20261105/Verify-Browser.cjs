"use strict";
// Explicit opt-in, local-file Edge check. Not run during shared-browser handoff.
// Only closes its owned browser; new outputs never overwrite the original reference PNG.
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const assert = require("node:assert/strict");

const output = path.join(__dirname, "verification");
fs.mkdirSync(output, { recursive: true });
const profile = fs.mkdtempSync(path.join(output, "edge-profile-"));
const executable = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const checks = [];
const observedRequests = [];
const errors = [];
let browser;
let socket;
let sessionId;
let sequence = 0;
const pending = new Map();
const eventListeners = new Set();

function send(method, params = {}, useSession = true) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 15000);
    pending.set(id, { resolve, reject, timeout });
    socket.send(JSON.stringify({ id, method, params, ...(useSession && sessionId ? { sessionId } : {}) }));
  });
}

function waitEvent(method) {
  return new Promise((resolve, reject) => {
    const listener = (event) => {
      if (event.method !== method || event.sessionId !== sessionId) return;
      clearTimeout(timer);
      eventListeners.delete(listener);
      resolve(event.params);
    };
    const timer = setTimeout(() => { eventListeners.delete(listener); reject(new Error(`Event timeout: ${method}`)); }, 15000);
    eventListeners.add(listener);
  });
}

async function evaluate(expression) {
  const result = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}

async function point(selector) {
  return evaluate(`(() => { const el=document.querySelector(${JSON.stringify(selector)}); if(!el) throw new Error('Missing element'); const r=el.getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}; })()`);
}

async function mouse(type, p, extra = {}) { return send("Input.dispatchMouseEvent", { type, x: p.x, y: p.y, ...extra }); }
async function click(selector) {
  const p = await point(selector);
  await mouse("mouseMoved", p);
  await mouse("mousePressed", p, { button: "left", buttons: 1, clickCount: 1 });
  await mouse("mouseReleased", p, { button: "left", buttons: 0, clickCount: 1 });
}
async function key(keyName, code, vk, modifiers = 0) {
  await send("Input.dispatchKeyEvent", { type: "rawKeyDown", key: keyName, code, windowsVirtualKeyCode: vk, modifiers });
  await send("Input.dispatchKeyEvent", { type: "keyUp", key: keyName, code, windowsVirtualKeyCode: vk, modifiers });
}
async function settle() { await evaluate("new Promise(resolve => setTimeout(() => requestAnimationFrame(resolve), 290))"); }
async function check(name, expression, predicate = Boolean) {
  const actual = await evaluate(expression);
  assert.ok(predicate(actual), `${name}: ${JSON.stringify(actual)}`);
  checks.push({ name, status: "pass", actual });
}
async function screenshot(name) {
  const result = await send("Page.captureScreenshot", { format: "png", fromSurface: true, captureBeyondViewport: false });
  const file = path.join(output, name);
  fs.writeFileSync(file, Buffer.from(result.data, "base64"));
  return file;
}

async function launch() {
  browser = spawn(executable, ["--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-sync", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
  const endpoint = await new Promise((resolve, reject) => {
    let text = "";
    const timer = setTimeout(() => reject(new Error("Owned Edge did not expose CDP within 25 seconds")), 25000);
    browser.once("error", (error) => { clearTimeout(timer); reject(error); });
    browser.stderr.on("data", (data) => {
      text += data.toString();
      const match = text.match(/DevTools listening on (ws:\/\/127\.0\.0\.1:\d+\/devtools\/browser\/\S+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
  });
  console.log(`Owned browser CDP: ${endpoint.split("/devtools/")[0]}; no page HTTP server.`);
  socket = new WebSocket(endpoint);
  socket.addEventListener("message", (message) => {
    const event = JSON.parse(message.data);
    if (event.id && pending.has(event.id)) {
      const request = pending.get(event.id);
      pending.delete(event.id);
      clearTimeout(request.timeout);
      if (event.error) request.reject(new Error(JSON.stringify(event.error)));
      else request.resolve(event.result);
    } else {
      eventListeners.forEach((listener) => listener(event));
      if (event.sessionId !== sessionId) return;
      if (event.method === "Network.requestWillBeSent") observedRequests.push(event.params.request.url);
      if (event.method === "Network.loadingFailed") errors.push({ kind: "resource", error: event.params.errorText });
      if (event.method === "Runtime.exceptionThrown") errors.push({ kind: "runtime", error: event.params.exceptionDetails.text });
    }
  });
  await once(socket, "open");
  const target = await send("Target.createTarget", { url: "about:blank" }, false);
  const attached = await send("Target.attachToTarget", { targetId: target.targetId, flatten: true }, false);
  sessionId = attached.sessionId;
  await send("Page.enable");
  await send("Runtime.enable");
  await send("Network.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: 2560, height: 1440, deviceScaleFactor: 1, mobile: false });
  await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "no-preference" }] });
  const loaded = waitEvent("Page.loadEventFired");
  const navigation = await send("Page.navigate", { url: pathToFileURL(path.join(__dirname, "index.html")).href });
  if (navigation.errorText) throw new Error(navigation.errorText);
  await loaded;
  await evaluate("document.fonts.ready.then(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))");
}

(async () => {
  let baseline;
  let alternate;
  let version;
  try {
    await launch();
    version = await send("Browser.getVersion", {}, false);
    await check("local image resources loaded", "Array.from(document.images).map(i=>({src:i.getAttribute('src'),loaded:i.complete&&i.naturalWidth>0,width:i.naturalWidth,height:i.naturalHeight}))", (images) => images.length > 0 && images.every((image) => image.loaded));
    const cssAssets = Array.from(new Set(Array.from(fs.readFileSync(path.join(__dirname, "styles.css"), "utf8").matchAll(/url\("(assets\/[^\"]+)"\)/g), (match) => match[1])));
    assert.ok(cssAssets.every((file) => fs.existsSync(path.join(__dirname, file))), "CSS asset files exist");
    await check("CSS background resources decode", `Promise.all(${JSON.stringify(cssAssets)}.map(src=>new Promise(resolve=>{const i=new Image(); i.onload=()=>resolve({src,loaded:true});i.onerror=()=>resolve({src,loaded:false});i.src=src;})))`, (images) => images.every((image) => image.loaded));
    await check("canvas and mutual reading workspace", "['#stage','.conversation-panel','#right-page','#reading-workspace','#session-controls'].map(s=>{const r=document.querySelector(s).getBoundingClientRect();return {selector:s,x:r.x,y:r.y,width:r.width,height:r.height};})", (rects) => rects[0].width === 2560 && rects[0].height === 1440 && rects[1].x === 406 && rects[2].x === 1425 && rects[4].height === 132);
    await check("two microphone utterances and substantive fixture", "({sources:Array.from(document.querySelectorAll('.utterance')).map(e=>e.dataset.source),history:document.querySelectorAll('.history-entry').length,lines:document.querySelectorAll('#insight-result .report-line').length,english:document.querySelector('.translation-line p').textContent})", (state) => state.sources.length === 2 && state.sources.every((source) => source === "mic") && state.history === 30 && state.lines === 42 && state.english === "Let every conversation flow naturally.");
    await check("consistent bilingual default and folded settings", "document.querySelector('#stage').dataset.display==='bilingual'&&document.querySelector('#display-bilingual').getAttribute('aria-selected')==='true'&&document.querySelector('#translation-settings').hidden&&document.querySelector('#history-panel').hidden&&!document.querySelector('#insight-panel').hidden");
    await check("long result actually scrolls", "({content:document.querySelector('#insight-scroll').scrollHeight,visible:document.querySelector('#insight-scroll').clientHeight})", (state) => state.content > state.visible && state.visible > 600);
    baseline = await screenshot("prototype-mutual-tabs-baseline-2560x1440.png");
    await mouse("mouseMoved", { x: 420, y: 230 });
    await settle();
    const restingBookmark = await evaluate("({y:document.querySelector('.bookmark').getBoundingClientRect().y,filter:getComputedStyle(document.querySelector('.bookmark')).filter})");
    await mouse("mouseMoved", await point(".bookmark.selected"));
    await settle();
    const hoveringBookmark = await evaluate("({y:document.querySelector('.bookmark').getBoundingClientRect().y,filter:getComputedStyle(document.querySelector('.bookmark')).filter})");
    assert.ok(hoveringBookmark.y < restingBookmark.y - 4 && hoveringBookmark.filter !== restingBookmark.filter, "Bookmark actual hover lifts and strengthens shadow");
    checks.push({ name: "bookmark actual hover", status: "pass", actual: { restingBookmark, hoveringBookmark } });
    await click("#display-original");
    await check("original segmentation", "getComputedStyle(document.querySelector('.translation-line')).display==='none'&&getComputedStyle(document.querySelector('.original-line')).display!=='none'");
    await key("ArrowRight", "ArrowRight", 39);
    await check("keyboard translated segmentation and focus", "document.querySelector('#stage').dataset.display==='translated'&&document.activeElement.id==='display-translated'&&getComputedStyle(document.querySelector('.original-line')).display==='none'");
    await key("ArrowRight", "ArrowRight", 39);
    await key("Tab", "Tab", 9);
    await check("keyboard focus-visible", "({id:document.activeElement.id,width:getComputedStyle(document.activeElement).outlineWidth,style:getComputedStyle(document.activeElement).outlineStyle})", (state) => state.id === "conversation-scroll" && state.width === "3px" && state.style === "solid");
    const sealPoint = await point("#start-button");
    await mouse("mouseMoved", sealPoint);
    await settle();
    const hoverY = await evaluate("document.querySelector('#start-button').getBoundingClientRect().y");
    await mouse("mousePressed", sealPoint, { button: "left", buttons: 1, clickCount: 1 });
    await settle();
    const pressedY = await evaluate("document.querySelector('#start-button').getBoundingClientRect().y");
    assert.ok(pressedY > hoverY + 4, "Seal press sinks");
    checks.push({ name: "seal hover/pressed", status: "pass", actual: { hoverY, pressedY } });
    await mouse("mouseReleased", sealPoint, { button: "left", buttons: 0, clickCount: 1 });
    await check("connecting locks route but permits cancel", "document.querySelector('#stage').dataset.session==='connecting'&&!document.querySelector('#start-button').disabled&&document.querySelector('#region').disabled&&document.querySelector('#start-label').textContent==='取消连接'");
    await evaluate("new Promise((resolve,reject)=>{const stage=document.querySelector('#stage');const timer=setTimeout(()=>{observer.disconnect();reject(new Error('No running state'));},2500);const observer=new MutationObserver(()=>{if(stage.dataset.session==='running'){clearTimeout(timer);observer.disconnect();resolve(true);}});observer.observe(stage,{attributes:true,attributeFilter:['data-session']});if(stage.dataset.session==='running'){clearTimeout(timer);observer.disconnect();resolve(true);}})");
    await check("running waveform", "({label:document.querySelector('#start-label').textContent,animation:getComputedStyle(document.querySelector('.utterance .wave span')).animationName})", (state) => state.label === "停止翻译" && state.animation === "waveform");
    await click("#start-button");
    await check("stop restores initial", "document.querySelector('#stage').dataset.session==='idle'&&!document.querySelector('#region').disabled&&document.querySelector('#start-label').textContent==='开始翻译'&&getComputedStyle(document.querySelector('.wave span')).animationName==='none'");
    await click("#settings-toggle");
    await check("expanded settings retain independent footer", "({scroll:document.querySelector('#insight-scroll').clientHeight,workspace:document.querySelector('#reading-workspace').getBoundingClientRect().bottom,settingsTop:document.querySelector('.settings-panel').getBoundingClientRect().top,settingsBottom:document.querySelector('.settings-panel').getBoundingClientRect().bottom,footer:document.querySelector('#session-controls').getBoundingClientRect().top,hit:document.elementFromPoint(...(()=>{const r=document.querySelector('#start-button').getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]})()).closest('#start-button')!==null})", (state) => state.scroll > 250 && state.workspace <= state.settingsTop && state.settingsBottom < state.footer && state.hit);
    await evaluate("document.querySelector('#font-size').focus()");
    await key("ArrowDown", "ArrowDown", 40);
    await key("Tab", "Tab", 9);
    await check("font actual native selection", "({value:document.querySelector('#font-size').value,size:getComputedStyle(document.querySelector('.original-line p')).fontSize})", (state) => state.value === "20" && state.size === "42px");
    await click("#subtitle-toggle");
    await check("subtitle switch", "document.querySelector('#subtitle-toggle').checked&&!document.querySelector('#subtitle-preview').hidden");
    await click("#subtitle-toggle");
    await click("#settings-toggle");
    await click("#history-tab");
    await click("#history-all");
    await check("history all keeps stable 30 rows", "document.querySelectorAll('.history-entry:not([hidden])').length===30");
    await click(".history-entry summary");
    await check("history context opens", "document.querySelector('.history-entry').open");
    await click("#history-clear");
    await key("Escape", "Escape", 27);
    await check("clear cancel preserves rows and restores focus", "!document.querySelector('#history-clear-dialog').open&&!document.querySelector('#history-content').hidden&&document.activeElement.id==='history-clear'");
    await click("#history-clear");
    await click("#history-confirm-clear");
    await check("clear only fixture", "!document.querySelector('#history-empty').hidden&&!document.querySelector('#history-restore').hidden&&document.querySelector('#insight-result .report-line')!==null");
    await click("#history-restore");
    await click("#insight-tab");
    await check("mutually exclusive insight tab", "document.querySelector('#history-panel').hidden&&!document.querySelector('#insight-panel').hidden&&document.querySelector('#history-actions').hidden");
    const originalResult = await evaluate("document.querySelector('#insight-result').textContent");
    await click("#preset-toggle");
    await click("[data-preset='knowledge']");
    await check("generation exposes stop", "!document.querySelector('#generation-stop').hidden&&document.querySelector('#question-send').disabled");
    await click("#generation-stop");
    await evaluate("new Promise(resolve=>setTimeout(resolve,1150))");
    await check("cancel does not late-write", "document.querySelector('#insight-result').textContent", (text) => text === originalResult);
    await click("#preset-toggle");
    await click("[data-preset='knowledge']");
    await evaluate("new Promise(resolve=>setTimeout(resolve,1150))");
    await check("preset renders different long analysis", "document.querySelector('#insight-result h2').textContent==='知识点提取'&&document.querySelectorAll('#insight-result .report-line').length>=40");
    await click("#prototype-toggle");
    await click("input[name=fixture][value=long]");
    await click("#empty-insight");
    await click("#prototype-close");
    await check("long dialogue and empty insight", "({messages:document.querySelectorAll('.utterance').length,scroll:document.querySelector('#conversation-scroll').scrollHeight,height:document.querySelector('#conversation-scroll').clientHeight,empty:!document.querySelector('#insight-empty').hidden})", (state) => state.messages === 5 && state.scroll > state.height && state.empty);
    await mouse("mouseMoved", { x: 420, y: 230 });
    alternate = await screenshot("prototype-mutual-tabs-long-empty-2560x1440.png");
    await mouse("mouseWheel", await point("#conversation-scroll"), { deltaY: 650, deltaX: 0 });
    await settle();
    await check("long dialogue real wheel scroll", "document.querySelector('#conversation-scroll').scrollTop>0");
    await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
    await mouse("mouseMoved", await point(".bookmark.selected"));
    await check("reduced motion removes lift", "getComputedStyle(document.querySelector('.bookmark.selected')).transform==='none'");
    await send("Emulation.setDeviceMetricsOverride", { width: 1280, height: 720, deviceScaleFactor: 1, mobile: false });
    await settle();
    await check("proportional 1280 viewport", "({width:document.querySelector('#stage').getBoundingClientRect().width,height:document.querySelector('#stage').getBoundingClientRect().height,scroll:document.documentElement.scrollWidth})", (state) => state.width === 1280 && state.height === 720 && state.scroll === 1280);
    assert.equal(errors.length, 0, "No runtime/resource failures");
    assert.ok(observedRequests.every((url) => url.startsWith("file:") || url.startsWith("data:")), "No page external requests");
    checks.push({ name: "no page external requests or errors", status: "pass", actual: { requests: observedRequests.length, errors } });
    fs.writeFileSync(path.join(output, "browser-mutual-tabs-result.json"), JSON.stringify({ status: "pass", browser: version.product, javascript: version.jsVersion, viewport: [2560,1440], mode: "owned headless Edge, real renderer/CDP input, file://; not desktop physical input", screenshots: [baseline, alternate], checks, observedRequests, errors }, null, 2));
    console.log(`PASS ${checks.length} focused checks; ${version.product}; screenshots: ${baseline}, ${alternate}`);
  } catch (error) {
    fs.writeFileSync(path.join(output, "browser-mutual-tabs-result.json"), JSON.stringify({ status: "fail", error: String(error), checks, observedRequests, errors, screenshots: [baseline, alternate].filter(Boolean) }, null, 2));
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (socket && socket.readyState === WebSocket.OPEN) {
      try { await send("Browser.close", {}, false); } catch { /* Browser close may race transport closure. */ }
      socket.close();
    }
    if (browser && browser.exitCode === null) {
      const exited = new Promise((resolve) => browser.once("exit", resolve));
      const timer = setTimeout(() => browser.kill(), 3000);
      await exited;
      clearTimeout(timer);
    }
    pending.forEach((request) => { clearTimeout(request.timeout); request.reject(new Error("Owned browser closed")); });
    if (!browser || browser.exitCode !== null) {
      try { fs.rmSync(profile, { recursive: true, force: true }); }
      catch (error) { console.warn(`Only this run's profile cleanup was incomplete: ${error.code}`); }
    }
  }
})();