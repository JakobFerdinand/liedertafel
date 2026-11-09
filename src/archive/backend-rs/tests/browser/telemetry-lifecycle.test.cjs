const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
async function control(mode) {
  const child = spawn(process.execPath, [path.join(__dirname, "fixtures/telemetry-lifecycle.cjs"), mode], { stdio: ["ignore", "pipe", "pipe"] });
  let output = ""; child.stdout.on("data", chunk => output += chunk); child.stderr.resume();
  const [status] = await once(child, "close");
  const directory = /Private telemetry capture: (.+)/.exec(output)?.[1];
  assert(directory, "private diagnostic location is reported");
  const report = JSON.parse(fs.readFileSync(path.join(directory, "report.json"), "utf8"));
  const events = fs.readFileSync(path.join(directory, "replay.jsonl"), "utf8").trim().split("\n").map(line => JSON.parse(line));
  const completion = events.at(-1).complete;
  assert(!JSON.stringify(report).includes("123456"), "final report contains no marker values");
  assert.equal(fs.statSync(directory).mode & 0o777, 0o700);
  for (const file of ["report.json", "replay.jsonl"]) assert.equal(fs.statSync(path.join(directory, file)).mode & 0o777, 0o600);
  return { status, report, completion, output, events, directory };
}
for (const mode of ["late-stdout", "late-stderr", "late-otlp", "late-malformed", "stale-safe"]) {
  test(`${mode}: terminal status and private completion must fail after an earlier clean assertion`, async () => {
    const result = await control(mode);
    assert.notEqual(result.status, 0, "late events cannot leave the process green");
    assert.equal(result.completion.nativeExitCode, result.status, "effective failed exit must be persisted");
    assert.equal(result.completion.secrecyChecked, false);
    assert.equal(result.report.secrecyChecked, false);
    if (mode === "late-malformed") assert(result.report.errors.length > 0);
    else if (mode !== "stale-safe") assert(result.report.findings.length > 0);
    else assert.deepEqual(result.report.findings, []);
  });
}
test("legitimate clean terminal completion remains successful", async () => {
  const result = await control("clean");
  assert.equal(result.status, 0); assert.equal(result.completion.nativeExitCode, 0);
  assert.equal(result.completion.secrecyChecked, true); assert.deepEqual(result.report.errors, []);
});
test("an existing nonzero exit is preserved in private terminal evidence", async () => {
  const result = await control("nonzero");
  assert.equal(result.status, 7); assert.equal(result.completion.nativeExitCode, 7);
});
test('watched pipe close, not child exit, drains inherited trailing output before PASS',async()=>{
  const result=await control('pipe-clean');
  assert.equal(result.status,0);assert(result.output.includes('CHILD_EXIT_BEFORE_TAIL'));
  assert(result.output.includes('PASS drained pipe'));assert.equal(result.report.secrecyChecked,true);
  assert(result.events.some(event=>event.console&&Buffer.from(event.console.body,'base64').toString().includes('safe trailing console event')));
});
test('a disclosure after child exit but before pipe close fails before PASS',async()=>{
  const result=await control('pipe-leak');
  assert.notEqual(result.status,0);assert.equal(result.report.findings[0].channel,'stdout');
  assert(!result.output.includes('PASS drained pipe'));
});
for(const mode of ['collector-clean','collector-leak','collector-malformed'])test(`${mode}: in-flight HTTP2 frames and sessions drain before the final check`,async()=>{
  const result=await control(mode);assert.equal(result.report.requests,2);
  assert.equal(result.completion.secrecyChecked,true);
  if(mode==='collector-clean'){
    assert.equal(result.status,0);assert(result.output.includes('PASS drained collector'));assert.deepEqual(result.report.errors,[]);
  }else{
    assert.notEqual(result.status,0);assert(!result.output.includes('PASS drained collector'));
    if(mode==='collector-leak')assert.equal(result.report.findings[0].channel,'logs');
    else assert(result.report.errors.some(error=>error.channel==='logs'));
  }
});
test('an unexpected watched-child nonzero exit fails even with clean payloads',async()=>{
  const result=await control('child-nonzero');assert.notEqual(result.status,0);
  assert(result.report.errors.some(error=>error.reason==='unexpected-child-exit'));
  assert(!result.output.includes('PASS expected child'));
});
test('a declared handled operator failure remains a legitimate clean gate',async()=>{
  const result=await control('child-expected');assert.equal(result.status,0);assert.equal(result.completion.nativeExitCode,0);
});
test('a caught no-secret assertion failure still makes terminal completion fail',async()=>{
  const result=await control('caught-finding');assert.notEqual(result.status,0);
  assert.equal(result.report.secrecyChecked,true);assert.equal(result.completion.nativeExitCode,result.status);
  assert.equal(result.report.findings.length,1);
});
test('explicit process.exit(0) cannot override an unchecked capture at terminal exit',async()=>{
  const result=await control('forced-zero');assert.notEqual(result.status,0);
  assert.equal(result.completion.nativeExitCode,result.status);assert.equal(result.report.completion.nativeExitCode,result.status);
});
test('a later synchronous exit listener cannot leave an earlier clean completion on disk',async()=>{
  const result=await control('exit-callback-otlp');assert.notEqual(result.status,0);
  assert.equal(result.completion.nativeExitCode,result.status);assert.equal(result.report.completion.nativeExitCode,result.status);
  assert.equal(result.report.secrecyChecked,false);assert.equal(result.report.findings[0].channel,'logs');
  assert.deepEqual(require('./telemetry-capture.cjs').replay(path.join(result.directory,'replay.jsonl')).findings,result.report.findings);
});
for(const kind of ['trace','logs','metrics'])test(`${kind}: actual native assertion and terminal exit reject email/code/cookie/credential payloads`,async()=>{
  const result=await control(`protocol-${kind}`);assert.notEqual(result.status,0);assert(!result.output.includes('PASS protocol'));
  for(const type of ['otp','email','cookie','credential'])assert(result.report.findings.some(f=>f.channel===kind&&f.type===type));
  assert.equal(result.completion.nativeExitCode,result.status);
});
test('private final reports replay failures without leaking values or parser snippets',async()=>{
  const {replay}=require('./telemetry-capture.cjs'),{spawnSync}=require('node:child_process');
  const result=await control('late-stderr');
  assert.equal(result.report.findings[0].marker,0);assert.equal(result.report.findings[0].label,'injected-code');
  assert.deepEqual(replay(path.join(result.directory,'replay.jsonl')).findings,result.report.findings);
  const file=path.join(result.directory,'corrupt.jsonl');fs.writeFileSync(file,'{"marker":"123456',{mode:0o600});
  const failure=spawnSync(process.execPath,[require.resolve('./telemetry-capture.cjs'),file],{encoding:'utf8'});
  assert.equal(failure.status,1);assert(!`${failure.stdout}${failure.stderr}`.includes('123456'));
});
