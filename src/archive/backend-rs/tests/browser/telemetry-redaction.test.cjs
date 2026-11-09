const { test } = require("node:test");
const assert = require("node:assert/strict");
const { scanCapture } = require("./telemetry-redaction.cjs");
const { analyzeCapture, root } = require("./telemetry-redaction.cjs");
const otp = { type: "otp", label: "synthetic-code", value: "123456" };
test("ordinary console timestamp metadata is not an OTP disclosure", () => {
  const capture = { signals: [], console: [{ channel: "stdout", source: "native-1", body: Buffer.from("2026-11-09T12:00:00.123456Z INFO safe event\n") }] };
  // Exact old predicate is deterministically red on unrelated metadata. This
  // fixture does not force the product's random login code or explain the lost run.
  assert(capture.console[0].body.toString().includes(otp.value));
  assert.deepEqual(scanCapture(capture, [otp]), []);
});
test("an actual OTP in the console payload still fails", () => {
  const capture = { signals: [], console: [{ channel: "stderr", source: "native-1", body: Buffer.from("2026-11-09T12:00:00.654321Z ERROR code=123456\n") }] };
  assert.equal(scanCapture(capture, [otp]).length, 1);
});
test("actual native ANSI formatter clock collision is classified, not a disclosure", () => {
  const body = Buffer.from("\x1b[2m2026-11-09T12:00:00.123456Z\x1b[0m \x1b[32m INFO\x1b[0m safe event\n");
  const capture = { signals: [], console: [{ source: "one", channel: "stdout", body }] };
  const result = analyzeCapture(capture, [otp]);
  assert.deepEqual(result.findings, []); assert.equal(result.metadata[0].field, "console.formatter.timestamp");
  assert.deepEqual(result.legacyMatches, [{ type: "otp", label: "synthetic-code" }]);
  const leaked = { ...capture, console: [{ ...capture.console[0], body: Buffer.concat([body.subarray(0, -1), Buffer.from(" code=123456\n")]) }] };
  assert.equal(scanCapture(leaked, [otp]).length, 1);
});
function frame(kind, message) {
  const service = kind[0].toUpperCase() + kind.slice(1);
  const type = root.lookupType(`opentelemetry.proto.collector.${kind}.v1.Export${service}ServiceRequest`);
  const body = type.encode(type.fromObject(message)).finish();
  const prefix = Buffer.alloc(5); prefix.writeUInt32BE(body.length, 1);
  return { route: `/opentelemetry.proto.collector.${kind}.v1.${service}Service/Export`, body: Buffer.concat([prefix, body]) };
}
const kv = value => ({ key: "private", value });
function message(kind, surface, value) {
  const resource = { attributes: [] }, scope = { attributes: [] };
  const record = { name: "safe", attributes: [] };
  if (surface === "resource") resource.attributes.push(kv(value));
  if (surface === "scope") scope.attributes.push(kv(value));
  if (surface === "attribute") record.attributes.push(kv(value));
  if (surface === "body") record.body = value;
  if (surface === "event") record.events = [{ name: "safe", attributes: [kv(value)] }];
  if (surface === "link") record.links = [{ attributes: [kv(value)] }];
  if (surface === "status") record.status = { message: value.stringValue };
  if (surface === "name") record.name = value.stringValue;
  if (kind === "trace") return { resourceSpans: [{ resource, scopeSpans: [{ scope, spans: [record] }] }] };
  if (kind === "logs") return { resourceLogs: [{ resource, scopeLogs: [{ scope, logRecords: [record] }] }] };
  return { resourceMetrics: [{ resource, scopeMetrics: [{ scope, metrics: [{ name: "safe", gauge: { dataPoints: [record] } }] }] }] };
}
const secrets = [otp, { type: "email", label: "synthetic-email", value: "sensitive@example.test" },
  { type: "cookie", label: "synthetic-session", value: "session-secret_opaque_abcdefghijklmnopqrstuvwxyz" },
  { type: "credential", label: "synthetic-passkey", value: "credential-secret-abcdefghijklmnopqrstuvwxyz" }];
for (const kind of ["trace", "logs", "metrics"]) {
  for (const surface of ["resource", "scope", "attribute", ...(kind === "trace" ? ["event", "link", "status", "name"] : kind === "logs" ? ["body"] : [])]) {
    for (const secret of secrets) test(`${kind} ${surface}: genuine ${secret.type} disclosure fails with schema location`, () => {
      const capture = { signals: [frame(kind, message(kind, surface, { stringValue: secret.value }))], console: [] };
      const result = analyzeCapture(capture, [secret]);
      assert.deepEqual(result.errors, []);
      assert.equal(result.findings.length, 1);
      assert.equal(result.findings[0].channel, kind);
      const suffix={resource:'resource.attributes[0].value.stringValue',scope:'scope.attributes[0].value.stringValue',attribute:'attributes[0].value.stringValue',body:'body.stringValue',event:'events[0].attributes[0].value.stringValue',link:'links[0].attributes[0].value.stringValue',status:'status.message',name:'.name'}[surface];
      assert(result.findings[0].field.endsWith(suffix));
      assert(!JSON.stringify(result).includes(secret.value));
    });
  }
  for (const [name, value] of Object.entries({
    bytes: { bytesValue: Buffer.from(otp.value) }, integer: { intValue: 123456 },
    double: { doubleValue: 123456 }, array: { arrayValue: { values: [{ stringValue: otp.value }] } },
    kvlist: { kvlistValue: { values: [kv({ stringValue: otp.value })] } },
  })) test(`${kind} nested ${name} payloads are not metadata-whitelisted`, () => {
    const result = analyzeCapture({ signals: [frame(kind, message(kind, "resource", value))], console: [] }, [otp]);
    assert.deepEqual(result.errors, []); assert.equal(result.findings.length, 1);
  });
}
test("metric numeric measurements and packed buckets remain secret-bearing", () => {
  for (const data of [{ gauge: { dataPoints: [{ asInt: 123456 }] } }, { histogram: { dataPoints: [{ bucketCounts: [123456] }] } }]) {
    const result = analyzeCapture({ signals: [frame("metrics", { resourceMetrics: [{ scopeMetrics: [{ metrics: [{ name: "safe", ...data }] }] }] })], console: [] }, [otp]);
    assert.deepEqual(result.errors, []); assert.equal(result.findings.length, 1);
  }
});
test("numeric OTP payloads cannot hide by losing a leading zero",()=>{
  const marker={type:'otp',label:'leading-zero-code',value:'012345'};
  for(const kind of ['trace','logs','metrics']){
    assert.equal(scanCapture({signals:[frame(kind,message(kind,'resource',{intValue:12345}))],console:[]},[marker]).length,1);
  }
});
test("log and metric clocks have narrow typed metadata treatment", () => {
  const signals=[frame('logs',{resourceLogs:[{scopeLogs:[{logRecords:[{timeUnixNano:'1761234560000000000',observedTimeUnixNano:'1761234560000000001'}]}]}]}),frame('metrics',{resourceMetrics:[{scopeMetrics:[{metrics:[{name:'safe',gauge:{dataPoints:[{timeUnixNano:'1761234560000000000',startTimeUnixNano:'1761234560000000001',asInt:10}]}}]}]}]})];
  const result=analyzeCapture({signals,console:[]},[otp]);
  assert.deepEqual(result.findings,[]);assert.deepEqual(result.errors,[]);assert.equal(result.metadata.length,4);
});
test("official fixed64 clocks are metadata, while timestamp-looking attribute text is payload", () => {
  const span = { name: "safe", startTimeUnixNano: "1761234560000000000", endTimeUnixNano: "1761234560000000001", traceId: Buffer.from("ab123456cd123456ab123456cd123456",'hex'), spanId: Buffer.alloc(8, 0xcd) };
  const capture = { signals: [frame("trace", { resourceSpans: [{ scopeSpans: [{ spans: [span] }] }] })], console: [] };
  const result = analyzeCapture(capture, [otp]);
  assert.deepEqual(result.errors, []); assert.deepEqual(result.findings, []); assert.equal(result.metadata.length, 2);
  span.attributes = [kv({ stringValue: "2026-11-09T12:00:00.123456Z" })];
  assert.equal(scanCapture({ signals: [frame("trace", { resourceSpans: [{ scopeSpans: [{ spans: [span] }] }] })], console: [] }, [otp]).length, 1);
});
test("exact legacy raw-binary timestamp collision is classified by official schema field", () => {
  const bytes=Buffer.from([49,50,51,52,53,54,0,0]);
  const signal=frame('trace',{resourceSpans:[{scopeSpans:[{spans:[{startTimeUnixNano:bytes.readBigUInt64LE().toString()}]}]}]});
  assert(signal.body.toString().includes(otp.value));
  const result=analyzeCapture({signals:[signal],console:[]},[otp]);
  assert.deepEqual(result.findings,[]);assert.deepEqual(result.errors,[]);
  assert.equal(result.metadata[0].field,'.opentelemetry.proto.collector.trace.v1.ExportTraceServiceRequest.resourceSpans[0].scopeSpans[0].spans[0].startTimeUnixNano');
  assert.equal(result.metadata[0].encoding,'fixed64-wire');
  assert.equal(result.legacyMatches.length,1);
});
test("credential bytes match their API base64, base64url and hex encodings", () => {
  const raw = Buffer.from([255, 254, 253, 252, 251, 250, 249, 248, 247, 246]);
  for (const encoding of ["base64", "base64url", "hex"]) {
    const marker = { type: "credential", label: "encoded-passkey", value: raw.toString(encoding) };
    assert.equal(scanCapture({ signals: [frame("logs", message("logs", "body", { bytesValue: raw }))], console: [] }, [marker]).length, 1);
  }
});
test("public-key bytes in every OTLP channel remain inspected",()=>{
  const raw=Buffer.from([255,254,253,252,251,250,249,248,247,246]);
  const marker={type:'public-key',label:'registered-public-key',value:raw.toString('base64')};
  for(const kind of ['trace','logs','metrics'])assert.equal(scanCapture({signals:[frame(kind,message(kind,'resource',{bytesValue:raw}))],console:[]},[marker]).length,1);
});
test("base64 and base64url credential spellings cannot evade string checks", () => {
  const raw = Buffer.from([255, 254, 253, 252, 251, 250, 249, 248, 247, 246]);
  for(const kind of ['trace','logs','metrics'])for(const [stored,exported] of [['base64','base64url'],['base64url','base64']]) {
    const marker={type:'public-key',label:'encoded-key',value:raw.toString(stored)};
    assert.equal(scanCapture({signals:[frame(kind,message(kind,'resource',{stringValue:raw.toString(exported)}))],console:[]},[marker]).length,1);
  }
});
test("binary protocol boundaries do not manufacture matches between string fields", () => {
  const capture={signals:[frame('logs',{resourceLogs:[{scopeLogs:[{logRecords:[{body:{stringValue:'123'}},{body:{stringValue:'456'}}]}]}]})],console:[]};
  assert.deepEqual(scanCapture(capture,[otp]),[]);
  // A raw identifier containing the literal bytes is still an actual disclosure.
  assert.equal(scanCapture({signals:[frame('trace',{resourceSpans:[{scopeSpans:[{spans:[{traceId:Buffer.from('123456abcdefghij')}]}]}]})],console:[]},[otp]).length,1);
});
test("invalid UTF-8 cannot silently remove markers from string or console inspection", () => {
  assert.throws(()=>scanCapture({signals:[],console:[{source:'one',channel:'stderr',body:Buffer.from([255])}]},[otp]),/invalid-or-unsupported-capture/);
  // Official LogsService -> ResourceLogs -> schema_url (UTF-8 string).
  const invalid={route:frame('logs',{}).route,body:Buffer.from([0,0,0,0,5,10,3,26,1,255])};
  assert.throws(()=>scanCapture({signals:[invalid],console:[]},[otp]),/invalid-or-unsupported-capture/);
});
for (const channel of ["stdout", "stderr"]) test(`${channel} chunks preserve real leaks but not cross-process/channel artifacts`, () => {
  for (const secret of secrets) {
    const split = Math.floor(secret.value.length / 2);
    const chunks = [secret.value.slice(0, split), secret.value.slice(split)].map(body => ({ source: "one", channel, body: Buffer.from(body) }));
    assert.equal(scanCapture({ signals: [], console: chunks }, [secret]).length, 1);
    assert.equal(scanCapture({ signals: [], console: [chunks[0], { ...chunks[1], source: "two" }] }, [secret]).length, 0);
    assert.equal(scanCapture({ signals: [], console: [chunks[0], { ...chunks[1], channel: channel === "stdout" ? "stderr" : "stdout" }] }, [secret]).length, 0);
  }
});
test("console timestamps only exempt the native anchored formatter, not payload context", () => {
  for (const text of ["code=123456", "2026-11-09T12:00:00.123456Z", "prefix 2026-11-09T12:00:00.123456Z INFO safe", "2026-11-09T12:00:00.123456Z INFO body code=123456", "2026-99-99T12:00:00.123456Z INFO malformed"]) {
    assert.equal(scanCapture({ signals: [], console: [{ source: "one", channel: "stdout", body: Buffer.from(text) }] }, [otp]).length, 1);
  }
});
test("each gRPC frame is decoded independently, including coalesced frames", () => {
  const safe = frame("logs", message("logs", "body", { stringValue: "safe" }));
  const leak = frame("logs", message("logs", "body", { stringValue: otp.value }));
  const result = analyzeCapture({ signals: [{ ...safe, body: Buffer.concat([safe.body, leak.body]) }], console: [] }, [otp]);
  assert.deepEqual(result.errors, []); assert.equal(result.findings[0].frame, 1);
});
test("unknown/malformed/truncated/compressed protocol captures fail closed", () => {
  assert.throws(()=>scanCapture({signals:[],console:[]},[otp]),/invalid-or-unsupported-capture/);
  const valid = frame("logs", message("logs", "body", { stringValue: "safe" }));
  const compressed = Buffer.from(valid.body); compressed[0] = 1;
  for (const signal of [{ ...valid, route: "/unknown" }, { ...valid, body: Buffer.alloc(0) }, { ...valid, body: valid.body.subarray(0, 3) }, { ...valid, body: valid.body.subarray(0, -1) }, { ...valid, body: compressed }, { ...valid, body: Buffer.from([0, 0, 0, 0, 2, 120, 1]) }]) {
    assert.throws(() => scanCapture({ signals: [signal], console: [] }, [otp]), /invalid-or-unsupported-capture/);
  }
});
test("native capture persists private evidence and replayable value-free failure diagnostics", async () => {
  const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process'),{once}=require('node:events');
  const {createCapture,replay}=require('./telemetry-capture.cjs');
  const capture=createCapture('detector-control');
  capture.marker('otp','injected-code',otp.value);
  const child=spawn(process.execPath,['-e',"process.stderr.write('code=123456\\n')"],{stdio:['ignore','pipe','pipe']});
  capture.watch(child,'one');await once(child,'close');
  assert.throws(()=>capture.assertClean(),error=>{
    assert(error.message.includes('console.payload'));assert(error.message.includes('stderr'));
    assert(!error.message.includes(otp.value));return true;
  });
  const report=JSON.parse(fs.readFileSync(path.join(capture.directory,'report.json'),'utf8'));
  assert.equal(report.findings[0].label,'injected-code');assert.equal(report.findings[0].marker,0);
  assert(!JSON.stringify(report).includes(otp.value));
  assert.equal(fs.statSync(capture.directory).mode&0o777,0o700);
  for(const file of ['report.json','replay.jsonl'])assert.equal(fs.statSync(path.join(capture.directory,file)).mode&0o777,0o600);
  const partial=replay(path.join(capture.directory,'replay.jsonl'));
  assert.deepEqual(partial.findings,report.findings);
  assert.equal(partial.errors.at(-1).reason,'incomplete-or-failed-native-run');
  // CLI parsing failures must not publish JSON.parse snippets containing secrets.
  const corrupt=path.join(capture.directory,'corrupt.jsonl');fs.writeFileSync(corrupt,'{"marker":"'+otp.value,{mode:0o600});
  const {spawnSync}=require('node:child_process');
  const failure=spawnSync(process.execPath,[require.resolve('./telemetry-capture.cjs'),corrupt],{encoding:'utf8'});
  assert.equal(failure.status,1);assert(!`${failure.stdout}${failure.stderr}`.includes(otp.value));
});
test("the native no-secret assertion goes red for actual protocol leaks on every channel",()=>{
  const {createCapture}=require('./telemetry-capture.cjs');
  for(const kind of ['trace','logs','metrics']){
    const capture=createCapture(`protocol-${kind}-control`);
    for(const secret of secrets){
      capture.marker(secret.type,secret.label,secret.value);
      const signal=frame(kind,message(kind,'resource',{stringValue:secret.value}));capture.signal(signal.route,signal.body);
    }
    assert.throws(()=>capture.assertClean(),error=>{
      for(const secret of secrets){assert(error.message.includes(secret.type));assert(!error.message.includes(secret.value));}
      return true;
    });
  }
});
