// Actual subprocess/pipe/HTTP2 controls. All marker material is synthetic.
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const http2 = require("node:http2");
const fs = require("node:fs");
const path = require("node:path");
const { createCapture } = require("../telemetry-capture.cjs");
const { root } = require("../telemetry-redaction.cjs");
const mode = process.argv[2];
const capture = createCapture("lifecycle-control");
capture.marker("otp", "injected-code", "123456");
const route = "/opentelemetry.proto.collector.logs.v1.LogsService/Export";
function frame(text) {
  const type = root.lookupType("opentelemetry.proto.collector.logs.v1.ExportLogsServiceRequest");
  const body = type.encode({ resourceLogs: [{ scopeLogs: [{ logRecords: [{ body: { stringValue: text } }] }] }] }).finish();
  const header = Buffer.alloc(5); header.writeUInt32BE(body.length, 1);
  return Buffer.concat([header, body]);
}
async function sendLate(body) {
  const server = http2.createServer();
  server.on("stream", (stream, headers) => {
    const chunks = []; stream.on("data", chunk => chunks.push(chunk));
    stream.on("end", () => { capture.signal(headers[":path"], Buffer.concat(chunks)); stream.respond({ ":status": 200 }); stream.end(); });
  });
  server.listen(0, "127.0.0.1"); await once(server, "listening");
  const client = http2.connect(`http://127.0.0.1:${server.address().port}`);
  const request = client.request({ ":path": route, ":method": "POST" });
  request.resume(); request.end(body); await once(request, "end");
  const closed = once(client, "close"); client.close(); await closed;
  await new Promise(resolve => server.close(resolve));
}
async function main() {
  capture.signal(route, frame("safe initial event"));
  if(mode.startsWith('protocol-')){
    const kind=mode.slice('protocol-'.length),service=kind[0].toUpperCase()+kind.slice(1);
    const type=root.lookupType(`opentelemetry.proto.collector.${kind}.v1.Export${service}ServiceRequest`);
    const values=[['otp','123456'],['email','sensitive@example.test'],['cookie','session-secret_opaque_abcdefghijklmnopqrstuvwxyz'],['credential','credential-secret-abcdefghijklmnopqrstuvwxyz']];
    const resource={attributes:values.map(([type,value])=>{capture.marker(type,`injected-${type}`,value);return {key:'private',value:{stringValue:value}};})};
    const body=type.encode({[kind==='trace'?'resourceSpans':kind==='logs'?'resourceLogs':'resourceMetrics']:[{resource}]}).finish();
    const header=Buffer.alloc(5);header.writeUInt32BE(body.length,1);
    capture.signal(`/opentelemetry.proto.collector.${kind}.v1.${service}Service/Export`,Buffer.concat([header,body]));
    capture.assertClean();console.log('PASS protocol');return;
  }
  if(mode.startsWith('collector-')){
    const server=capture.collector(http2.createServer());
    server.on('stream',(stream,headers)=>{
      const chunks=[];stream.on('data',chunk=>chunks.push(chunk));stream.on('end',()=>{
        capture.signal(headers[':path'],Buffer.concat(chunks));stream.respond({':status':200});stream.end();
      });
    });
    server.listen(0,'127.0.0.1');await once(server,'listening');
    const client=http2.connect(`http://127.0.0.1:${server.address().port}`);
    const done=once(client,'close');
    const request=client.request({':path':route,':method':'POST'});request.resume();
    const body=mode==='collector-malformed'?Buffer.from([0,0,0,0,8,1]):frame(mode==='collector-leak'?'code=123456':'safe final export');
    request.write(body.subarray(0,3));await once(server,'stream');
    const drained=capture.drain();setImmediate(()=>request.end(body.subarray(3)));
    await drained;client.close();await done;
    capture.assertClean();console.log('PASS drained collector');return;
  }
  if(mode==='child-nonzero'||mode==='child-expected'){
    const child=spawn(process.execPath,['-e',"process.stdout.write('safe output');process.exitCode=3"],{stdio:['ignore','pipe','pipe']});
    capture.watch(child,'finite-control',mode==='child-expected'?3:0);await capture.closed(child);
    capture.assertClean();console.log('PASS expected child');return;
  }
  if(mode==='pipe-clean'||mode==='pipe-leak'){
    const release=path.join(capture.directory,'release-tail');
    const tail=mode==='pipe-leak'?'code=123456':'safe trailing console event';
    const grandchild=`const fs=require('node:fs');const timer=setInterval(()=>{if(fs.existsSync(${JSON.stringify(release)})){clearInterval(timer);process.stdout.write(${JSON.stringify(tail)});}},5);`;
    const script=`require('node:child_process').spawn(process.execPath,['-e',${JSON.stringify(grandchild)}],{stdio:['ignore',1,2]});process.exit(0);`;
    const child=spawn(process.execPath,['-e',script],{stdio:['ignore','pipe','pipe']});
    capture.watch(child,'inherited-pipe');await once(child,'exit');
    console.log('CHILD_EXIT_BEFORE_TAIL');fs.writeFileSync(release,'go',{mode:0o600});
    await capture.closed(child);capture.assertClean();console.log('PASS drained pipe');return;
  }
  capture.assertClean();
  if (mode === "clean") return;
  if (mode === "nonzero") { process.exitCode = 7; return; }
  if(mode==='exit-callback-otlp'){
    // Registered after the capture's exit handler: no asynchronous work in exit.
    process.once('exit',()=>capture.signal(route,frame('code=123456')));return;
  }
  if(mode==='caught-finding'){
    capture.signal(route,frame('code=123456'));try{capture.assertClean();}catch{}
    return;
  }
  await new Promise(resolve => setImmediate(resolve));
  if (mode === "late-stdout" || mode === "late-stderr") {
    const channel = mode.slice(5);
    const child = spawn(process.execPath, ["-e", `process.${channel}.write('code=123456\\n')`], { stdio: ["ignore", "pipe", "pipe"] });
    capture.watch(child, "late-child"); await once(child, "close");
  } else if (mode === "late-otlp") await sendLate(frame("code=123456"));
  else if (mode === "late-malformed") await sendLate(Buffer.from([0, 0, 0, 0, 8, 1]));
  else if (mode === "stale-safe"||mode==='forced-zero') {await sendLate(frame("safe but unchecked event"));if(mode==='forced-zero')process.exit(0);}
  else throw Error("unknown control");
}
main().catch(() => { console.error("Lifecycle control failed; inspect private report."); process.exitCode = 1; });
