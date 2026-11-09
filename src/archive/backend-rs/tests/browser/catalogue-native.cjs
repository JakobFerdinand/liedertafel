// #223: native Rust + real PG/private Azurite + unchanged static UI, normal
// email/CSRF. Node's official SDK creates fixtures only, never app authorization.
const assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{once}=require('node:events'),{randomUUID}=require('node:crypto');
const net=require('node:net'),http2=require('node:http2'),path=require('node:path');
const {Client}=require('pg'),{chromium,expect}=require('@playwright/test'),{BlobServiceClient,newPipeline,StorageSharedKeyCredential}=require('@azure/storage-blob');
const telemetry=require('./telemetry-capture.cjs').createCapture('catalogue-native');
telemetry.marker('email','member-email','mitglied@liedertafel.test');telemetry.marker('email','editor-email','redaktion@liedertafel.test');
telemetry.marker('query','search','nachtigallen');telemetry.marker('object','private-object-name','secret-object-marker');
const binary=process.env.ARCHIVE_BINARY||path.resolve(__dirname,'../../target/debug/archive-server');
const server=process.env.ARCHIVE_TEST_POSTGRES,connection=process.env.ARCHIVE_TEST_AZURITE;
assert(server&&connection&&process.env.ARCHIVE_STATIC_ROOT,'explicit local PG/private Azurite/static export required');
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(server).hostname));assert(connection.includes('BlobEndpoint=http://127.0.0.1:'));
const database=`catalogue_native_${randomUUID().replaceAll('-','')}`,container=`native-${randomUUID()}`,databaseUrl=new URL(server);databaseUrl.pathname=database;
const children=[],messages=[],signals={};
async function listen(listener){listener.listen(0,'127.0.0.1');await once(listener,'listening');return listener.address().port;}
const smtp=net.createServer(socket=>{socket.setEncoding('utf8');socket.write('220 synthetic SMTP\r\n');let buffer='',data=false,mail='';socket.on('data',chunk=>{buffer+=chunk;let end;while((end=buffer.indexOf('\r\n'))>=0){const line=buffer.slice(0,end);buffer=buffer.slice(end+2);if(data){if(line==='.'){messages.push(mail);data=false;socket.write('250 accepted\r\n');}else mail+=line+'\n';continue;}if(/^(EHLO|HELO)/.test(line))socket.write('250 synthetic\r\n');else if(line==='DATA'){data=true;mail='';socket.write('354 send\r\n');}else if(line==='QUIT')socket.end('221 bye\r\n');else socket.write('250 ok\r\n');}});});
const collector=http2.createServer();collector.on('stream',(stream,headers)=>{const chunks=[];stream.on('data',c=>chunks.push(c));stream.on('end',()=>{const route=headers[':path'];telemetry.signal(route,Buffer.concat(chunks));signals[route]=Buffer.concat([signals[route]||Buffer.alloc(0),...chunks]);stream.respond({':status':200,'content-type':'application/grpc'},{waitForTrailers:true});stream.on('wantTrailers',()=>stream.sendTrailers({'grpc-status':'0'}));stream.end(Buffer.from([0,0,0,0,0]));});});
async function stop(child){if(child.exitCode===null){child.kill('SIGTERM');const [code]=await once(child,'exit');assert.equal(code,0);}}
async function main(){
 const admin=new Client({connectionString:server});await admin.connect();await admin.query(`CREATE DATABASE ${database}`);let db,browser;
 const field=name=>connection.match(new RegExp(`(?:^|;)${name}=([^;]+)`))[1];const pipeline=newPipeline(new StorageSharedKeyCredential(field('AccountName'),field('AccountKey')));
 telemetry.marker('key','storage-account-key',field('AccountKey'));
 // Official legacy-policy extension runs before Sign. Avoid additionalPolicies:
 // this JS SDK reapplies those to its cached pipeline for every derived client.
 pipeline.factories.push({create(next){return {sendRequest(request){request.headers.set('x-ms-version','2025-11-05');return next.sendRequest(request);}}}});
 const service=new BlobServiceClient(field('BlobEndpoint'),pipeline);
 const blobs=service.getContainerClient(container);
 try{
  await blobs.create();await service.setProperties({cors:[{allowedOrigins:'*',allowedMethods:'GET,HEAD,PUT,OPTIONS',allowedHeaders:'*',exposedHeaders:'*',maxAgeInSeconds:3600}]});
  const mailPort=await listen(smtp),otlpPort=await listen(collector),reserve=net.createServer(),port=await listen(reserve);await new Promise(r=>reserve.close(r));const base=`http://localhost:${port}`;
  const env={...process.env,ARCHIVE_ENVIRONMENT:'Development',PORT:String(port),RUST_LOG:'trace',OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${otlpPort}`,OTEL_BSP_SCHEDULE_DELAY:'100',OTEL_BLRP_SCHEDULE_DELAY:'100',OTEL_METRIC_EXPORT_INTERVAL:'100',Mail__Provider:'Smtp',Mail__Host:'127.0.0.1',Mail__Port:String(mailPort),'ConnectionStrings__archive-db':databaseUrl.href,'ConnectionStrings__archive-migrations':databaseUrl.href,'ConnectionStrings__archive-blobs':connection,Archive__Assets__ContainerName:container};
  let processNumber=0;
  async function command(args){const child=spawn(binary,args,{env,stdio:['ignore','pipe','pipe'],signal:AbortSignal.timeout(30000)});telemetry.watch(child,`process-${processNumber++}`);const [code]=await once(child,'exit');assert.equal(code,0,'native command failed; inspect private replay');}
  await command(['--migrate']);await command(['--seed-dev-auth']);db=new Client({connectionString:databaseUrl.href});await db.connect();
  const lied=randomUUID(),score=randomUUID(),original=randomUUID(),copy=randomUUID(),actor='00000000-0000-0000-0000-000000000000';
  await db.query('INSERT INTO lieder ("Id","Title","Composer","VoiceConfiguration","MusicalKey","Lyrics","Occasion","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,\'Der Mond ist aufgegangen\',\'Müller\',\'TTBB\',\'F-Dur\',\'Im grünen Walde ruht der Tag\',\'Abendlied\',now(),now(),$2,$2,1)',[lied,actor]);
  for(const [id,kind,type,partitur] of [[score,'noten','application/pdf',true],[original,'aufnahme','video/quicktime',false],[copy,'aufnahme','video/mp4',false]]){
   await db.query('INSERT INTO dateien ("Id","LiedId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","OriginalFileName","CreatedByAccountId","CreatedAt","RowVersion") VALUES ($1,$2,$3,$4,$5,$6,128,\'partitur.pdf\',$7,now(),1)',[id,lied,kind,partitur,`dateien/${id}/secret-object-marker`,type,actor]);
   await blobs.getBlockBlobClient(`dateien/${id}/secret-object-marker`).uploadData(Buffer.from('%PDF-1.4\n% synthetic private fixture\n'),{blobHTTPHeaders:{blobContentType:'application/xml'}});
  }
  await db.query('INSERT INTO extraction_jobs ("DateiId","Status","Text","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion","TextSource") VALUES ($1,2,\'Nachtigallen im Tannenwald\',0,$2,now(),now(),1,1)',[score,actor]);
  await db.query('INSERT INTO video_jobs ("DateiId","Status","PlaybackDateiId","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,2,$2,0,$3,now(),now(),1)',[original,copy,actor]);
  async function start(){const child=spawn(binary,[],{env,stdio:['ignore','pipe','pipe']});children.push(child);telemetry.watch(child,`process-${processNumber++}`);for(let i=0;i<200;i++){if(child.exitCode!==null)throw Error('native startup failed; inspect private replay');try{if((await fetch(base+'/alive')).ok)return child;}catch{}await new Promise(r=>setTimeout(r,25));}throw Error('startup timeout');}
  let origin=await start();browser=await chromium.launch({executablePath:process.env.ARCHIVE_CHROMIUM});const context=await browser.newContext(),editorContext=await browser.newContext(),page=await context.newPage(),editor=await editorContext.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  const flushMember=telemetry.browser(context,'member'),flushEditor=telemetry.browser(editorContext,'editor');
  function code(email){const mail=messages.findLast(m=>m.includes(email));assert(mail);const code=mail.match(/Ihr Anmeldecode lautet:\s*(\d{6})/)[1];telemetry.marker('otp','login-code',code);return code;}
  async function login(p,email){await p.goto(base+'/anmelden/');await p.getByLabel('E-Mail-Adresse',{exact:true}).fill(email);await p.getByRole('button',{name:'Code anfordern'}).click();await expect(p.getByLabel('Code aus der E-Mail')).toBeVisible();await p.getByLabel('Code aus der E-Mail').fill(code(email));await p.getByRole('button',{name:'Anmelden',exact:true}).click();await expect(p).toHaveURL(/\/archiv\//);}
  await login(page,'mitglied@liedertafel.test');await page.goto(base+'/lieder/');await expect(page.getByRole('link',{name:'Der Mond ist aufgegangen',exact:true})).toBeVisible();await page.getByLabel('Lieder suchen',{exact:true}).fill('nachtigallen');await page.getByRole('button',{name:'Suchen',exact:true}).click();await expect(page.getByText('Gefunden: in den Noten',{exact:true})).toBeVisible();await page.getByRole('link',{name:'Der Mond ist aufgegangen',exact:true}).click();await expect(page.getByRole('heading',{name:'Der Mond ist aufgegangen',level:1})).toBeVisible();await expect(page.getByText('Besetzung: TTBB')).toBeVisible();
  const scoreArticle=page.getByRole('article',{name:'Noten · Partitur'});const read=page.waitForResponse(r=>r.url().includes(container)&&r.url().includes('rscd=inline'));await scoreArticle.getByRole('button',{name:'Noten anzeigen'}).click();assert.equal((await read).status(),200);const iframe=scoreArticle.locator('iframe');await expect(iframe).toBeVisible();const view=await iframe.getAttribute('src'),download=await scoreArticle.getByRole('link',{name:'Herunterladen'}).getAttribute('href');assert(new URL(view).origin!==base);assert.equal((await context.request.head(download)).headers()['content-disposition'],'attachment');
  telemetry.marker('signature','view-sas',new URL(view).searchParams.get('sig'));telemetry.marker('signature','download-sas',new URL(download).searchParams.get('sig'));
  const signed=await page.evaluate(async url=>{const r=await fetch(url,{method:'HEAD'});return {status:r.status,type:r.headers.get('content-type')};},view);assert.deepEqual(signed,{status:200,type:'application/pdf'});
  const access=await (await context.request.get(`${base}/api/dateien/${original}/access`)).json();assert.equal(access.playbackDateiId,copy);assert(access.viewUrl.includes(copy));assert(access.downloadUrl.includes(original));
  await login(editor,'redaktion@liedertafel.test');
  async function tier(p,value){return p.evaluate(async({id,value})=>{const csrf=await (await fetch('/api/antiforgery')).json();const r=await fetch(`/api/dateien/${id}/storage-tier`,{method:'POST',headers:{'content-type':'application/json','X-CSRF-TOKEN':csrf.token},body:JSON.stringify(value)});return {status:r.status,body:await r.json()};},{id:original,value});}
  assert.equal((await tier(page,{tier:'cold'})).status,403);assert.equal((await tier(editor,{tier:'cold',rowVersion:1})).status,200);assert.equal((await blobs.getBlobClient(`dateien/${original}/secret-object-marker`).getProperties()).accessTier,'Cold');
  await stop(origin);origin=await start();const fresh=await (await context.request.get(`${base}/api/dateien/${original}/access`)).json();assert.equal(fresh.playbackDateiId,copy);const ranged=await context.request.get(fresh.downloadUrl,{headers:{range:'bytes=0-3'}});assert.equal(ranged.status(),206);assert.equal((await ranged.body()).toString(),'%PDF');
  await blobs.getBlobClient(`dateien/${original}/secret-object-marker`).delete();assert.equal((await tier(editor,{tier:'cold',rowVersion:2})).status,200);const fail=await tier(editor,{tier:'hot',rowVersion:2});assert.equal(fail.status,502);assert.equal(fail.body.title,'Speicherdienst nicht erreichbar.');assert.equal((await db.query('SELECT "StorageTier" FROM dateien WHERE "Id"=$1',[original])).rows[0].StorageTier,'cold');
  await flushMember();await flushEditor();assert.deepEqual(errors,[]);await browser.close();browser=undefined;await stop(origin);
  const traces=Object.entries(signals).filter(([k])=>k.includes('TraceService')).map(([,v])=>v.toString()).join(''),logs=Object.entries(signals).filter(([k])=>k.includes('LogsService')).map(([,v])=>v.toString()).join('');
  for(const name of ['archive.assets.read_ticket','archive.assets.set_access_tier'])assert(traces.includes(name),`missing storage span ${name}`);
  for(const mail of messages)telemetry.marker('otp','delivered-code',mail.match(/Ihr Anmeldecode lautet:\s*(\d{6})/)[1]);telemetry.assertClean();
  assert(logs.length>0);console.log('PASS: native unchanged catalogue/search/Noten UI, real auth/CSRF, private direct Blob GET/HEAD/range/overrides, seeded video copy/tier policy, provider-first 502 and restart, storage OTLP and trace-level redaction');
 }finally{if(browser)await browser.close();for(const child of children)await stop(child);if(db)await db.end();smtp.close();collector.close();await blobs.deleteIfExists();await admin.query(`DROP DATABASE ${database} WITH (FORCE)`);await admin.end();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
