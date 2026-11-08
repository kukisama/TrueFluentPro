"use strict";
// Only an owned offline Edge renderer; never modifies the authoritative HTML/CSS/JS.
const fs = require("node:fs"), path = require("node:path"), {pathToFileURL} = require("node:url"), {spawn} = require("node:child_process"), {once} = require("node:events");
const root = process.cwd(), fixturePath = path.resolve(process.argv[2]);
const output = path.dirname(fixturePath), profile = fs.mkdtempSync(path.join(output, "html-edge-profile-"));
let browser, socket, session, sequence=0; const pending=new Map(), errors=[];
function send(method, params={}, attached=true) {
  return new Promise((resolve,reject)=>{ const id=++sequence; const timer=setTimeout(()=>{pending.delete(id);reject(new Error(method+" timeout"));},15000); pending.set(id,{resolve,reject,timer}); socket.send(JSON.stringify({id,method,params,...(attached&&session?{sessionId:session}:{})})); });
}
async function evaluate(expression) { const result=await send("Runtime.evaluate",{expression,returnByValue:true,awaitPromise:true}); if(result.exceptionDetails)throw new Error(JSON.stringify(result.exceptionDetails)); return result.result.value; }
async function capture(name) { const shot=await send("Page.captureScreenshot",{format:"png",captureBeyondViewport:false}); fs.writeFileSync(path.join(output,name),Buffer.from(shot.data,"base64")); }
async function click(selector) { const p=await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`); await send("Input.dispatchMouseEvent",{type:"mouseMoved",...p}); await send("Input.dispatchMouseEvent",{type:"mousePressed",...p,button:"left",clickCount:1}); await send("Input.dispatchMouseEvent",{type:"mouseReleased",...p,button:"left",clickCount:1}); }
async function settle(){await evaluate("document.fonts.ready.then(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))))");}
(async()=>{
  let pass=false;
  try {
    browser=spawn("C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",["--headless=new","--remote-debugging-address=127.0.0.1","--remote-debugging-port=0",`--user-data-dir=${profile}`,"--no-first-run","--disable-background-networking","--disable-sync","about:blank"],{stdio:["ignore","ignore","pipe"],windowsHide:true});
    const endpoint=await new Promise((resolve,reject)=>{let text="";const timer=setTimeout(()=>reject(new Error("No owned CDP endpoint")),20000);browser.once("error",reject);browser.stderr.on("data",d=>{text+=d;const match=text.match(/DevTools listening on (ws:\/\/127\.0\.0\.1:\d+\/devtools\/browser\/\S+)/);if(match){clearTimeout(timer);resolve(match[1]);}});});
    socket=new WebSocket(endpoint);socket.addEventListener("message",event=>{const response=JSON.parse(event.data);const p=pending.get(response.id);if(p){clearTimeout(p.timer);pending.delete(response.id);response.error?p.reject(new Error(JSON.stringify(response.error))):p.resolve(response.result);}else if(response.method==="Runtime.exceptionThrown")errors.push(response.params.exceptionDetails);});
    await once(socket,"open");const target=await send("Target.createTarget",{url:"about:blank"},false);session=(await send("Target.attachToTarget",{targetId:target.targetId,flatten:true},false)).sessionId;
    await send("Page.enable");await send("Runtime.enable");await send("Emulation.setDeviceMetricsOverride",{width:1256,height:831,deviceScaleFactor:1,mobile:false});
    await send("Page.navigate",{url:pathToFileURL(path.join(root,"design","宋人书斋HTML原型_20261105","index.html")).href});
    for(let attempt=0;attempt<60;attempt++){if(await evaluate("document.querySelectorAll('.history-entry').length===30"))break;await new Promise(r=>setTimeout(r,50));}
    const fixture=JSON.parse(fs.readFileSync(fixturePath,"utf8"));
    // Text-only fixture synchronization. Preserve the approved page's styles and controls.
    await evaluate(String.raw`(()=>{const fixture=${JSON.stringify(fixture)}; document.querySelectorAll('.history-entry').forEach((row,i)=>{const data=fixture.history[i];row.querySelector('time').textContent=data.time;const texts=row.querySelector('.history-text').children;texts[0].textContent=data.original;texts[1].textContent=data.translated;});const report=document.querySelector('#insight-result');report.replaceChildren();for(const line of fixture.markdown.split(/\r?\n/)){if(!line)continue;const h=line.startsWith('### ')?'h3':line.startsWith('## ')?'h2':'p';const p=document.createElement(h);p.textContent=line.replace(/^#{2,3} |^- /g,'');if(line.startsWith('- '))p.className='report-line';if(line.startsWith('生成时间'))p.className='report-meta';report.append(p);}return true;})()`);
    await settle();await capture("html-insight-default-1256x831.png");
    const geometry=await evaluate("['#stage','#right-page','#reading-workspace','#session-controls','#start-button','.window-strip'].map(selector=>{const r=document.querySelector(selector).getBoundingClientRect();return {selector,x:r.x,y:r.y,width:r.width,height:r.height}})");
    await click("#history-tab");await click("#history-all");await evaluate("document.querySelector('#history-content').scrollTop=400");await settle();await capture("html-history-1256x831.png");
    await click("#insight-tab");await click("#settings-toggle");await settle();await capture("html-settings-expanded-1256x831.png");
    const expanded=await evaluate("(()=>{const r=document.querySelector('#session-controls').getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,readingHeight:document.querySelector('#insight-scroll').clientHeight};})()");
    await click("#settings-toggle");const seal=await evaluate("(()=>{const r=document.querySelector('#start-button').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()");await send("Input.dispatchMouseEvent",{type:"mouseMoved",...seal});await new Promise(r=>setTimeout(r,250));await capture("html-seal-hover-1256x831.png");
    const hover=await evaluate("(()=>{const r=document.querySelector('#start-button').getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height}})()");
    const version=await send("Browser.getVersion",{},false);fs.writeFileSync(path.join(output,"html-comparison.json"),JSON.stringify({browser:version.product,mode:"owned offline headless Edge/CDP screenshot; native application screenshots are separate BitBlt captures",fixture:fixturePath,client:[1256,831],geometry,expanded,hover,errors},null,2));
    if(errors.length)throw new Error("HTML runtime errors");pass=true;console.log("HTML_COMPARISON_EXIT=0; OUTPUT="+output);
  }catch(error){console.error(error);process.exitCode=1;}
  finally{
    if(socket?.readyState===WebSocket.OPEN){try{await send("Browser.close",{},false);}catch{}socket.close();}
    if(browser&&browser.exitCode===null){const done=once(browser,"exit");const timer=setTimeout(()=>browser.kill(),3000);await done;clearTimeout(timer);}
    // Leave the owned profile evidence in artifacts; never delete user/system files.
    for(const p of pending.values()){clearTimeout(p.timer);p.reject(new Error("Closed owned renderer"));}
    if(!pass)console.log("HTML_COMPARISON_EXIT=1");
  }
})();