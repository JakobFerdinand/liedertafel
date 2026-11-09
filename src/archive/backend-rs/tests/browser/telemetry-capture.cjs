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
  let generation=0,checkedGeneration=-1;
  function append(event) {
    if(event.marker||event.signal||event.console)generation++;
    fs.writeSync(fd, JSON.stringify(event) + "\n");
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
  function watch(child, source) {
    for (const channel of ["stdout", "stderr"]) child[channel].on("data", body => {
      capture.console.push({ source, channel, body }); append({ console: { source, channel, body: body.toString("base64") } });
    });
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
  function report() {
    const result = analyzeCapture(capture, markers);
    const summary = { ...result, requests: capture.signals.length, markers: markers.length, secrecyChecked:checkedGeneration===generation };
    // Labels are caller-controlled static names; field paths are schema-derived,
    // not attribute keys. No marker values, snippets or hashes in this report.
    fs.writeFileSync(path.join(directory, "report.json"), JSON.stringify(summary, null, 2) + "\n", { mode: 0o600 });
    return summary;
  }
  process.once("exit", nativeExitCode => {
    append({complete:{nativeExitCode,secrecyChecked:checkedGeneration===generation}});
    report(); fs.closeSync(fd);
  });
  function assertClean() {
    checkedGeneration=generation;
    const result = report();
    console.log(`Telemetry diagnostics: ${directory}/report.json (private replay retained)`);
    assert.equal(result.errors.length + result.findings.length, 0, `telemetry secrecy/protocol failure: ${JSON.stringify({ findings: result.findings, errors: result.errors })}`);
    return result;
  }
  return { marker, signal, watch, browser, report, assertClean, directory };
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
