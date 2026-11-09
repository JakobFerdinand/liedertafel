// Raw synthetic evidence is private local replay data, never a public log.
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const { analyzeCapture } = require("./telemetry-redaction.cjs");
function createCapture(name) {
  assert(/^[a-z-]+$/.test(name));
  const directory = fs.mkdtempSync(path.join("/tmp/opencode", `223-${name}-telemetry-`));
  fs.chmodSync(directory, 0o700);
  const evidence = path.join(directory, "replay.jsonl");
  const fd = fs.openSync(evidence, "wx", 0o600);
  console.log(`Private telemetry capture: ${directory}`);
  const capture = { signals: [], console: [] }, markers = [];
  const children=new Map();
  const collectors=[],failures=[];
  let generation=0,checkedGeneration=-1;
  let writeOffset=0,completionOffset=null;
  function append(event) {
    const afterTerminal=completionOffset!==null&&!event.complete;
    if(afterTerminal){
      // A later synchronous exit listener can still call the capture API. Keep
      // its bytes before one authoritative terminal record, never after a stale
      // successful completion. This touches only this capture's own private FD.
      process.exitCode=Number(process.exitCode)||1;
      fs.ftruncateSync(fd,completionOffset);writeOffset=completionOffset;completionOffset=null;
    }
    if(event.marker||event.signal||event.console||event.lifecycle)generation++;
    const bytes=Buffer.from(JSON.stringify(event)+'\n');let written=0;
    while(written<bytes.length){
      const count=fs.writeSync(fd,bytes,written,bytes.length-written,writeOffset);
      assert(count>0,'private capture write failed');written+=count;writeOffset+=count;
    }
    if(afterTerminal)finish(process.exitCode);
  }
  append({ format: "archive-native-telemetry-v1" });
  function marker(type, label, value) {
    assert(/^[a-z-]+$/.test(type) && /^[a-z0-9-]+$/.test(label), "static diagnostic labels required");
    assert.equal(typeof value, "string");
    if (value.length && !markers.some(m => m.type === type && m.label === label && m.value === value)) {
      const item = { type, label, value }; markers.push(item); append({ marker: item });
    }
  }
  function signal(route, body) { capture.signals.push({ route, body }); append({ signal: { route, body: body.toString("base64") } }); }
  function watch(child, source, expectedExitCode=0) {
    assert(!children.has(child),'child already watched');
    const state={index:children.size,closed:false,code:null,signal:null,expectedExitCode};
    children.set(child,state);append({lifecycle:{event:'child-watch',index:state.index}});
    state.done=new Promise(resolve=>child.once('close',(code,signal)=>{
      state.closed=true;state.code=code;state.signal=signal;
      append({lifecycle:{event:'child-close',index:state.index}});resolve([code,signal]);
    }));
    child.on('error',()=>{state.failed=true;});
    for (const channel of ["stdout", "stderr"]) child[channel].on("data", body => {
      capture.console.push({ source, channel, body }); append({ console: { source, channel, body: body.toString("base64") } });
    });
  }
  function closed(child){assert(children.has(child),'unwatched child');return children.get(child).done;}
  function failure(index,part,reason){
    failures.push({channel:'lifecycle',field:`collector[${index}].${part}`,reason});
    append({lifecycle:{event:'collector-error',index}});
  }
  function collector(server){
    const state={index:collectors.length,server,sessions:new Set(),streams:new Set(),closures:[],closed:false,draining:false};
    collectors.push(state);append({lifecycle:{event:'collector-watch',index:state.index}});
    server.on('session',session=>{
      state.sessions.add(session);append({lifecycle:{event:'collector-session',index:state.index}});
      session.on('error',()=>failure(state.index,'session','collector-session-error'));
      state.closures.push(new Promise(resolve=>session.once('close',()=>{state.sessions.delete(session);append({lifecycle:{event:'collector-session-close',index:state.index}});resolve();})));
      if(state.draining)session.close();
    });
    server.on('stream',stream=>{
      state.streams.add(stream);append({lifecycle:{event:'collector-stream',index:state.index}});
      stream.on('error',()=>failure(state.index,'request','collector-stream-error'));
      state.closures.push(new Promise(resolve=>stream.once('close',()=>{
        if(!stream.readableEnded)failure(state.index,'request','incomplete-collector-request');
        state.streams.delete(stream);append({lifecycle:{event:'collector-stream-close',index:state.index}});resolve();
      })));
    });
    server.on('error',()=>failure(state.index,'close','collector-server-error'));
    server.once('close',()=>{state.closed=true;append({lifecycle:{event:'collector-close',index:state.index}});});
    return server;
  }
  async function drain(){
    let timer;
    const work=(async()=>{
      await Promise.all([...children.values()].map(state=>state.done));
      await Promise.all(collectors.map(state=>{
        if(state.closed)return;
        if(!state.done){
          state.draining=true;
          state.done=new Promise(resolve=>{
            state.server.close(error=>{
              if(error&&error.code!=='ERR_SERVER_NOT_RUNNING')failure(state.index,'close','collector-server-error');
              if(!state.closed){state.closed=true;append({lifecycle:{event:'collector-close',index:state.index}});}
              resolve();
            });
            // server.close alone leaves persistent HTTP2 sessions open. GOAWAY
            // admits no new requests, but lets every existing request finish.
            for(const session of state.sessions)session.close();
          });
        }
        return state.done;
      }));
      await Promise.all(collectors.flatMap(state=>state.closures));
    })();
    try{
      await Promise.race([work,new Promise((_,reject)=>{timer=setTimeout(()=>{
        for(const state of collectors){failure(state.index,'close','collector-drain-timeout');for(const session of state.sessions)session.destroy();}
        reject(Error('native telemetry drain timed out'));
      },10000);})]);
    }finally{clearTimeout(timer);}
  }
  function lifecycleErrors(){
    return [...failures,...[...children.values()].flatMap(state=>{
      const reason=!state.closed?'unclosed-child-pipes':state.failed||state.signal||state.code!==state.expectedExitCode?'unexpected-child-exit':null;
      return reason?[{channel:'lifecycle',field:`child[${state.index}].close`,reason}]:[];
    }),...collectors.filter(state=>!state.closed||state.sessions.size||state.streams.size).map(state=>({channel:'lifecycle',field:`collector[${state.index}].close`,reason:'undrained-collector'}))];
  }
  function browser(context, label) {
    const pending = [];let failed=false;
    context.on("response", response => {
      pending.push((async () => {
        if (!new URL(response.url()).pathname.startsWith("/api/")) return;
        for (const header of await response.headersArray()) if (header.name.toLowerCase() === "set-cookie") {
          const pair = header.value.split(";")[0], at = pair.indexOf("=");
          marker("cookie", `${label}-${pair.startsWith('archive.auth=')?'session-cookie':'csrf-cookie'}`, pair.slice(at + 1));
        }
        if (new URL(response.url()).pathname === "/api/antiforgery" && response.ok()) {
          marker("cookie", `${label}-csrf-token`, (await response.json()).token);
        }
      })().catch(() => { failed=true; }));
    });
    return async () => {
      await Promise.all(pending);
      assert(!failed,"browser secret capture failed; refusing a partial secrecy check");
      for(const suffix of ['session-cookie','csrf-cookie','csrf-token'])assert(markers.some(m=>m.label===`${label}-${suffix}`),`missing ${label}-${suffix} secrecy marker`);
    };
  }
  function report(completion) {
    const result = analyzeCapture(capture, markers);
    result.errors.push(...lifecycleErrors());
    const summary = { ...result, requests: capture.signals.length, markers: markers.length, secrecyChecked:checkedGeneration===generation, ...(completion?{completion}:{}) };
    // Labels are caller-controlled static names; field paths are schema-derived,
    // not attribute keys. No marker values, snippets or hashes in this report.
    fs.writeFileSync(path.join(directory, "report.json"), JSON.stringify(summary, null, 2) + "\n", { mode: 0o600 });
    return summary;
  }
  function finish(nativeExitCode){
    // No async shutdown here. Re-evaluate every retained byte synchronously and
    // make the actual exit agree with the final report, not an earlier snapshot.
    const result=report();
    const failed=!result.secrecyChecked||result.findings.length||result.errors.length;
    const effectiveCode=Number(process.exitCode||nativeExitCode)|| (failed?1:0);
    process.exitCode=effectiveCode;
    const completion={nativeExitCode:effectiveCode,secrecyChecked:result.secrecyChecked,errors:lifecycleErrors()};
    completionOffset=writeOffset;append({complete:completion});report(completion);
  }
  // The OS closes the FD after every synchronous exit listener. Keeping it open
  // until then lets append() repair a completion if a later listener adds data.
  process.once("exit",finish);
  function assertClean() {
    checkedGeneration=generation;
    const result = report();
    console.log(`Telemetry diagnostics: ${directory}/report.json (private replay retained)`);
    assert.equal(result.errors.length + result.findings.length, 0, `telemetry secrecy/protocol failure: ${JSON.stringify({ findings: result.findings, errors: result.errors })}`);
    return result;
  }
  return { marker, signal, watch, closed, collector, drain, browser, report, assertClean, directory };
}
function replay(file) {
  const capture = { signals: [], console: [] }, markers = [];
  let completion;
  const lines=fs.readFileSync(file,"utf8").trim().split("\n");
  assert.equal(JSON.parse(lines.shift()).format,"archive-native-telemetry-v1","unsupported-private-replay");
  for (const line of lines) {
    const event = JSON.parse(line);
    assert(!completion,'data-after-private-replay-completion');
    assert.equal(Object.keys(event).length,1,"invalid-private-replay-event");
    if(event.complete){
      assert(typeof event.complete.nativeExitCode==='number'&&typeof event.complete.secrecyChecked==='boolean','invalid-private-replay-completion');
      completion=event.complete;
    } else if(event.lifecycle){
      assert(['child-watch','child-close','collector-watch','collector-session','collector-session-close','collector-stream','collector-stream-close','collector-error','collector-close'].includes(event.lifecycle.event)&&Number.isInteger(event.lifecycle.index),'invalid-private-lifecycle');
    } else if (event.marker) {
      assert(/^[a-z-]+$/.test(event.marker.type)&&/^[a-z0-9-]+$/.test(event.marker.label)&&typeof event.marker.value==='string',"invalid-private-marker");
      markers.push(event.marker);
    } else {
      const kind=event.signal?'signal':'console',item=event[kind];
      assert(item&&typeof item.body==='string',"invalid-private-replay-event");
      const body=Buffer.from(item.body,'base64');assert.equal(body.toString('base64'),item.body,'invalid-private-replay-base64');
      assert(kind==='signal'?typeof item.route==='string':typeof item.source==='string'&&['stdout','stderr'].includes(item.channel),'invalid-private-replay-channel');
      capture[kind==='signal'?'signals':'console'].push({...item,body});
    }
  }
  assert(markers.length,'missing-private-replay-markers');
  const result=analyzeCapture(capture, markers);
  if(completion?.errors){
    for(const error of completion.errors){
      assert(error.channel==='lifecycle'&&/^(?:child|collector)\[\d+\]\.(?:close|session|request)$/.test(error.field)&&['unclosed-child-pipes','unexpected-child-exit','undrained-collector','collector-session-error','collector-stream-error','incomplete-collector-request','collector-server-error','collector-drain-timeout'].includes(error.reason),'invalid-private-lifecycle-error');
      result.errors.push(error);
    }
  }
  if(!completion||completion.nativeExitCode!==0||!completion.secrecyChecked)result.errors.push({channel:'capture',field:'capture.completion',reason:'incomplete-or-failed-native-run'});
  return result;
}
if (require.main === module) {
  try {
    const result = replay(process.argv[2]); console.log(JSON.stringify(result, null, 2));
    process.exitCode = result.errors.length || result.findings.length ? 1 : 0;
  } catch {
    console.error('Invalid private replay; refusing partial inspection.');process.exitCode=1;
  }
}
module.exports = { createCapture, replay };
