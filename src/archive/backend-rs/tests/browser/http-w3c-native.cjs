// Actual frozen ASP.NET vs product Rust origins; no model, private input or auth bypass.
const assert=require('node:assert/strict'),http2=require('node:http2'),net=require('node:net'),path=require('node:path');
const {spawn}=require('node:child_process'),{once}=require('node:events');
const capture=require('./telemetry-capture.cjs').createCapture('http-wtc-native');
const controls=require('./http-w3c-controls.cjs');
const server=process.env.ARCHIVE_TEST_POSTGRES;assert(server&&['localhost','127.0.0.1'].includes(new URL(server).hostname));
const signals=[];const collector=capture.collector(http2.createServer());collector.on('stream',(stream,headers)=>{const chunks=[];stream.on('data',c=>chunks.push(c));stream.on('end',()=>{const bytes=Buffer.concat(chunks);capture.signal(headers[':path'],bytes);if(headers[':path'].includes('TraceService'))signals.push(bytes);stream.respond({':status':200,'content-type':'application/grpc'},{waitForTrailers:true});stream.on('wantTrailers',()=>stream.sendTrailers({'grpc-status':'0'}));stream.end(Buffer.alloc(5));});});
async function listen(s){s.listen(0,'127.0.0.1');await once(s,'listening');return s.address().port;}
async function port(){const s=net.createServer(),p=await listen(s);await new Promise(r=>s.close(r));return p;}
async function run(kind,development,otlp){
 const p=await port(),base=`http://127.0.0.1:${p}`,start=signals.length;
 const u=new URL(server),connection=`Host=${u.hostname};Port=${u.port};Database=postgres;Username=${decodeURIComponent(u.username)};Password='${decodeURIComponent(u.password).replaceAll("'","''")}'`;
 const env={...process.env,ARCHIVE_ENVIRONMENT:development?'Development':'Production',ASPNETCORE_ENVIRONMENT:development?'Development':'Testing',DOTNET_ENVIRONMENT:development?'Development':'Testing',PORT:String(p),ASPNETCORE_URLS:base,OTEL_SERVICE_NAME:`w3c-${kind}-${development}`,OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${otlp}`,OTEL_BSP_SCHEDULE_DELAY:'100',OTEL_BLRP_SCHEDULE_DELAY:'100',RUST_LOG:'trace',Logging__LogLevel__Default:'Warning',Archive__Reading__Enabled:'false',Mail__Provider:'Smtp',Mail__Host:'127.0.0.1',Mail__Port:'1',Authentication__AllowEphemeralKeysForTests:'true',Authentication__PasskeyRelyingPartyId:'localhost',Authentication__PasskeyOrigins__0:'https://localhost',Development__KeysPath:path.join(capture.directory,'dotnet-keys'),'ConnectionStrings__archive-db':kind==='dotnet'?connection:server};
 for(const key of ['OTEL_TRACES_SAMPLER','OTEL_TRACES_SAMPLER_ARG','Archive__Chat__Endpoint','Archive__Chat__DeploymentName'])delete env[key];
 // The production sampler needs no auth/database path; keep that origin's
 // dependency-free startup rather than supplying development auth settings.
 if(kind==='rust'&&!development)delete env['ConnectionStrings__archive-db'];
 const frozen=path.resolve(__dirname,'../../../backend');const executable=kind==='dotnet'?'dotnet':development?process.env.ARCHIVE_BINARY:path.join(path.dirname(process.env.ARCHIVE_BINARY),'examples/http-w3c-native');const child=spawn(executable,kind==='dotnet'?[path.join(frozen,'bin/Debug/net10.0/Archive.Backend.dll')]:[],{env,cwd:kind==='dotnet'?frozen:undefined,stdio:['ignore','pipe','pipe']});capture.watch(child,`${kind}-${development}`);
 try{let ready=false;for(let i=0;i<400;i++){assert.equal(child.exitCode,null,'origin startup');try{const response=await fetch(base+'/alive');await response.arrayBuffer();if(response.ok){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,25));}assert(ready);await controls.exercise(base);}
 finally{if(child.exitCode===null)child.kill('SIGTERM');assert.equal((await capture.closed(child))[0],0);}
 return controls.decode(Buffer.concat(signals.slice(start)));
}
(async()=>{capture.marker('key','ignored-baggage','synthetic-baggage-forbidden-230');const otlp=await listen(collector);const results=[];try{for(const kind of ['dotnet','rust'])for(const development of [true,false]){const spans=await run(kind,development,otlp);console.log(`Checking ${kind} Development=${development}`);controls.verify(spans,development);results.push({kind,development,passed:true});}await capture.drain();capture.assertClean();console.log(JSON.stringify({passed:results}));}finally{await capture.drain();}})().catch(error=>{require('node:fs').writeFileSync(path.join(process.env.TMPDIR||'/tmp/opencode','230-http-w3c-failure.txt'),String(error.stack),{mode:0o600});console.error('HTTP W3C native control failed; private diagnostic retained');process.exitCode=1;});
