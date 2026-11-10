// Offline test executable only; no product model endpoint or paid raw probe.
const assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{once}=require('node:events'),{randomUUID}=require('node:crypto'),http2=require('node:http2');
const {Client}=require('pg');
const telemetry=require('./telemetry-capture.cjs').createCapture('ai-foundation-native');
const server=process.env.ARCHIVE_TEST_POSTGRES,binary=process.env.ARCHIVE_BINARY,probe=process.env.ARCHIVE_AI_PROBE;
assert(server&&binary&&probe);assert(['localhost','127.0.0.1'].includes(new URL(server).hostname));
const database=`ai228_native_${randomUUID().replaceAll('-','')}`,url=new URL(server);url.pathname=database;
const signals={};const collector=telemetry.collector(http2.createServer());
collector.on('stream',(stream,headers)=>{const chunks=[];stream.on('data',c=>chunks.push(c));stream.on('end',()=>{const route=headers[':path'],bytes=Buffer.concat(chunks);telemetry.signal(route,bytes);signals[route]=Buffer.concat([signals[route]||Buffer.alloc(0),bytes]);stream.respond({':status':200,'content-type':'application/grpc'},{waitForTrailers:true});stream.on('wantTrailers',()=>stream.sendTrailers({'grpc-status':'0'}));stream.end(Buffer.from([0,0,0,0,0]));});});
async function main(){
 const admin=new Client({connectionString:server});await admin.connect();await admin.query(`CREATE DATABASE ${database}`);
 collector.listen(0,'127.0.0.1');await once(collector,'listening');
 for(const [type,value] of [['key','synthetic-ai-token-228'],['object','private-prompt-228'],['object','private-answer-228'],['object','private-tool-payload-228'],['object','private-settlement-error-228'],['email','redaktion-228@example.test'],['signature','private-signature-228']])telemetry.marker(type,'ai-private',value);
 const env={...process.env,ARCHIVE_ENVIRONMENT:'Development',ARCHIVE_TEST_AI_DATABASE:url.href,'ConnectionStrings__archive-migrations':url.href,OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${collector.address().port}`,RUST_LOG:'trace',OTEL_BSP_SCHEDULE_DELAY:'100',OTEL_BLRP_SCHEDULE_DELAY:'100',OTEL_METRIC_EXPORT_INTERVAL:'100'};
 async function run(executable,args,label,extra={},expected=0){const child=spawn(executable,args,{env:{...env,...extra},stdio:['ignore','pipe','pipe'],signal:AbortSignal.timeout(30000)});telemetry.watch(child,label,expected);assert.equal((await telemetry.closed(child))[0],expected);}
 try {
  await run(binary,['--migrate'],'ai-migrate');await run(probe,[],'ai-finite-provider');
  const db=new Client({connectionString:url.href});await db.connect();const rows=(await db.query('SELECT * FROM ai_usage_entries')).rows;assert.equal(rows.length,1);assert.equal(rows[0].Calls,2);assert.equal(rows[0].ReservedMicroEur,'0');assert(rows[0].AccountId);
  await db.query('TRUNCATE ai_usage_entries,ai_budget_months');await run(probe,[],'ai-finite-durable-failure',{ARCHIVE_TEST_AI_SETTLEMENT_FAILURE:'offline-228'},1);const failed=(await db.query('SELECT * FROM ai_usage_entries')).rows;assert.equal(failed.length,1);assert.equal(failed[0].Calls,1);assert(BigInt(failed[0].ReservedMicroEur)>0n);assert.equal(failed[0].CostMicroEur,'0');await db.end();
  await telemetry.drain();for(const name of ['TraceService','MetricsService','LogsService'])assert(Object.keys(signals).some(r=>r.includes(name)));
  const bytes=Buffer.concat(Object.values(signals));for(const signal of ['Liedertafel.Archive.Ai','gen_ai.request.model','archive.ai.feature','archive.ai.operation.id','gen_ai.agent.name','archive.ai.tokens','archive.ai.cost','archive.ai.budget.refusals','archive.ai.budget.settlement_failures','gen_ai.client.token.usage'])assert(bytes.includes(Buffer.from(signal)),signal);
  telemetry.assertClean();console.log('PASS: offline native AOAI REST/SSE/fake credential, real PostgreSQL accounting, consumer-break cancellation, cap refusal, supervised finite drain and actual OTLP privacy');
 } finally { await telemetry.drain();await admin.query(`DROP DATABASE ${database} WITH (FORCE)`);await admin.end(); }
}
main().catch(()=>{console.error('AI foundation native gate failed; inspect private telemetry replay');process.exitCode=1;});
