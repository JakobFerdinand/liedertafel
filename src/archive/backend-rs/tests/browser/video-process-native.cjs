// #233 real CLI tree/pipe/deadline/SIGTERM/privacy, not a fake transcoder test.
const assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{once}=require('node:events'),{randomUUID}=require('node:crypto'),http2=require('node:http2'),fs=require('node:fs'),path=require('node:path');
const {Client}=require('pg'),{BlobServiceClient,newPipeline,StorageSharedKeyCredential}=require('@azure/storage-blob'),{QueueServiceClient,newPipeline:queuePipeline,StorageSharedKeyCredential:QueueCredential}=require('@azure/storage-queue');
const telemetry=require('./telemetry-capture.cjs').createCapture('video-process-native');
const binary=process.env.ARCHIVE_BINARY,server=process.env.ARCHIVE_TEST_POSTGRES,connection=process.env.ARCHIVE_TEST_AZURITE,queues=process.env.ARCHIVE_TEST_QUEUE;assert(binary&&server&&connection&&queues);assert(['localhost','127.0.0.1'].includes(new URL(server).hostname));
const database=`video233_process_${randomUUID().replaceAll('-','')}`,container=`video233-${randomUUID()}`,queueName=`video233-${randomUUID()}`,url=new URL(server);url.pathname=database;
const signals={};let serial=0;
const collector=telemetry.collector(http2.createServer());collector.on('stream',(stream,headers)=>{const chunks=[];stream.on('data',c=>chunks.push(c));stream.on('end',()=>{const route=headers[':path'];telemetry.signal(route,Buffer.concat(chunks));signals[route]=Buffer.concat([signals[route]||Buffer.alloc(0),...chunks]);stream.respond({':status':200,'content-type':'application/grpc'},{waitForTrailers:true});stream.on('wantTrailers',()=>stream.sendTrailers({'grpc-status':'0'}));stream.end(Buffer.from([0,0,0,0,0]));});});
async function pauseControls(env){
 const invalid='invalid-video-maintenance-provider-233';telemetry.marker('object','paused-provider',invalid);
 for(const [command,message] of [['transcode-queue','Video worker paused: maintenance mode is enabled'],['dispatch-videos','Video dispatch paused: maintenance mode is enabled']]){
  const child=spawn(binary,['--'+command],{env:{...env,Archive__MaintenanceMode:' true ','ConnectionStrings__archive-db':invalid,'ConnectionStrings__archive-queues':invalid,'ConnectionStrings__archive-blobs':invalid},stdio:['ignore','pipe','pipe'],signal:AbortSignal.timeout(10000)});telemetry.watch(child,`paused-${command}`,0);assert.equal((await telemetry.closed(child))[0],0);
  const spans=require('./http-w3c-controls.cjs').decode(Buffer.concat(Object.entries(signals).filter(([r])=>r.includes('TraceService')).map(([,b])=>b))),span=spans.find(s=>s.name===command);assert(span,'paused command span');assert.notEqual(span.status?.code,2,'pause must not mark command as error');
  const type=require('./telemetry-redaction.cjs').root.lookupType('opentelemetry.proto.collector.logs.v1.ExportLogsServiceRequest');const logs=[];
  for(const [route,bytes] of Object.entries(signals).filter(([r])=>r.includes('LogsService')))for(let at=0;at<bytes.length;){assert.equal(bytes[at],0);const n=bytes.readUInt32BE(at+1),request=type.decode(bytes.subarray(at+5,at+5+n));at+=5+n;for(const resource of request.resourceLogs||[])for(const scope of resource.scopeLogs||[])for(const record of scope.logRecords||[])logs.push({category:scope.scope?.name,record});}
  const pause=logs.find(l=>l.record.body?.stringValue===message);assert(pause,'exact command-specific exported pause message');assert.equal(pause.category,'Archive.Videos','exported maintenance category');
  const completion=logs.find(l=>l.record.body?.stringValue===`Archive job ${command} completed`);assert(completion,'normal exported completion');
  for(const log of [pause,completion]){assert(Buffer.from(log.record.traceId).equals(Buffer.from(span.traceId)));assert(Buffer.from(log.record.spanId).equals(Buffer.from(span.spanId)));}
 }
}
async function main(){
 if(process.env.ARCHIVE_VIDEO_PAUSE_ONLY==='1'){
  collector.listen(0,'127.0.0.1');await once(collector,'listening');
  try{await pauseControls({...process.env,ARCHIVE_ENVIRONMENT:'Development',OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${collector.address().port}`,RUST_LOG:'trace'});}finally{await telemetry.drain();}
  telemetry.assertClean();console.log('PASS: both actual maintenance commands exit0 before providers with exact exported pause category/message, command span and correlated normal completion');return;
 }
 const admin=new Client({connectionString:server});await admin.connect();await admin.query(`CREATE DATABASE ${database}`);let db;
 const field=name=>connection.match(new RegExp(`(?:^|;)${name}=([^;]+)`))[1];telemetry.marker('key','storage-key',field('AccountKey'));
 const pipeline=newPipeline(new StorageSharedKeyCredential(field('AccountName'),field('AccountKey')));pipeline.factories.push({create(next){return{sendRequest(request){request.headers.set('x-ms-version','2025-11-05');return next.sendRequest(request);}}}});const service=new BlobServiceClient(field('BlobEndpoint'),pipeline),blobs=service.getContainerClient(container);
 const qp=queuePipeline(new QueueCredential(field('AccountName'),field('AccountKey')));qp.factories.push({create(next){return{sendRequest(request){request.headers.set('x-ms-version','2025-11-05');return next.sendRequest(request);}}}});const queueService=new QueueServiceClient(queues.match(/QueueEndpoint=([^;]+)/)[1],qp),queue=queueService.getQueueClient(queueName);
 const directory=fs.mkdtempSync(path.join(process.env.TMPDIR||'/tmp/opencode','video233-process-'));fs.chmodSync(directory,0o700);
 try{
  await blobs.create();await queue.create();collector.listen(0,'127.0.0.1');await once(collector,'listening');
  const env={...process.env,ARCHIVE_ENVIRONMENT:'Development',RUST_LOG:'trace',OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${collector.address().port}`,OTEL_BSP_SCHEDULE_DELAY:'100',OTEL_BLRP_SCHEDULE_DELAY:'100',OTEL_METRIC_EXPORT_INTERVAL:'100','ConnectionStrings__archive-db':url.href,'ConnectionStrings__archive-migrations':url.href,'ConnectionStrings__archive-blobs':connection,'ConnectionStrings__archive-queues':queues,Archive__Assets__ContainerName:container,Archive__Video__QueueName:queueName,TMPDIR:directory};
  function start(args,extra={},expected=0){const child=spawn(binary,args,{env:{...env,...extra},stdio:['ignore','pipe','pipe'],signal:AbortSignal.timeout(30000)});telemetry.watch(child,`finite-${serial++}`,expected);return child;}
  async function command(args,extra={},expected=0){assert.equal((await telemetry.closed(start(args,extra,expected)))[0],expected);}
   await pauseControls(env);await command(['--migrate']);db=new Client({connectionString:url.href});await db.connect();
  const owner=randomUUID(),id=randomUUID(),actor=randomUUID(),blob=`dateien/${id}/original`;telemetry.marker('object','original-blob',blob);
  await db.query('INSERT INTO lieder ("Id","Title","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,\'Synthetic\',now(),now(),$2,$2,1)',[owner,actor]);
  const bytes=fs.readFileSync(path.resolve(__dirname,'../../../../../tests/archive/apphost/TestAssets/kurzfilm.mp4'));await blobs.getBlockBlobClient(blob).uploadData(bytes,{blobHTTPHeaders:{blobContentType:'video/mp4'}});
  await db.query('INSERT INTO dateien ("Id","LiedId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","CreatedByAccountId","CreatedAt","RowVersion") VALUES ($1,$2,\'aufnahme\',false,$3,\'video/mp4\',$4,$5,now(),1)',[id,owner,blob,bytes.length,actor]);
  for(const parentCancel of [false,true]){
   const marker=`NativeToolOutput233${randomUUID().replaceAll('-','')}`,tool=path.join(directory,`native tool ${randomUUID()}.py`),witness=tool+'.pids';telemetry.marker('object','tool-output',marker);telemetry.marker('object','tool-path',tool);
   fs.writeFileSync(tool,`#!/usr/bin/python3\nimport os,sys,subprocess\nchild=subprocess.Popen([sys.executable,'-c',${JSON.stringify(`import os,time;os.write(1,${JSON.stringify(marker)}.encode()*10000);os.write(2,${JSON.stringify(marker)}.encode()*10000);time.sleep(60)`).replace(/^"|"$/g,'"')}])\nwith open(${JSON.stringify(witness)},'w') as f:f.write(str(os.getpid())+' '+str(child.pid))\nchild.wait()\n`,{mode:0o700});
   const body=JSON.stringify({type:'transcode',dateiId:id,traceParent:'00-23312331233123312331233123312331-2331233123312331-01'});telemetry.marker('object','queue-envelope',body);const sent=await queue.sendMessage(body);telemetry.marker('signature','send-receipt',sent.popReceipt);
   const child=start(['--transcode-queue'],{Archive__Video__FfprobePath:tool,Archive__Video__TimeBudget:parentCancel?'00:00:10':'00:00:00.5'},parentCancel?1:0);
   for(let i=0;i<500&&!fs.existsSync(witness);i++)await new Promise(r=>setTimeout(r,10));assert(fs.existsSync(witness));const pids=fs.readFileSync(witness,'utf8').split(' ');if(parentCancel)child.kill('SIGTERM');assert.equal((await telemetry.closed(child))[0],parentCancel?1:0);
   for(const pid of pids)assert.equal(fs.existsSync(`/proc/${pid}`),false,'actual native tree fully gone/reaped');
   const job=(await db.query('SELECT * FROM video_jobs WHERE "DateiId"=$1',[id])).rows[0];assert.equal(job.Status,parentCancel?1:0);assert.equal(job.AttemptCount,parentCancel?2:1);assert.equal(job.FailureReason,null);
   fs.unlinkSync(tool);fs.unlinkSync(witness);assert.deepEqual(fs.readdirSync(directory),[],'private spools removed after cancellation');
  }
  await command(['--dispatch-videos'],{'ConnectionStrings__archive-db':'invalid'},1);
  await telemetry.drain();for(const name of ['TraceService','MetricsService','LogsService'])assert(Object.keys(signals).some(k=>k.includes(name)));const traces=Buffer.concat(Object.entries(signals).filter(([k])=>k.includes('TraceService')).map(([,b])=>b));assert(traces.includes(Buffer.from('23312331233123312331233123312331','hex')));assert(traces.includes(Buffer.from('2331233123312331','hex')));telemetry.assertClean();console.log('PASS: actual descendant/large simultaneous tool pipes, deadline recovery, parent SIGTERM/reap/temp cleanup, finite failure, W3C/three signals/no tool-output disclosure');
 }finally{if(db)await db.end();await telemetry.drain();await queue.deleteIfExists();await blobs.deleteIfExists();await admin.query(`DROP DATABASE ${database} WITH (FORCE)`);await admin.end();}
}
main().catch(error=>{fs.writeFileSync(path.join(process.env.TMPDIR||'/tmp/opencode','video233-process-native-failure.txt'),String(error.stack),{mode:0o600});console.error('Video process native gate failed; inspect private video233-process-native-failure.txt');process.exitCode=1;});
