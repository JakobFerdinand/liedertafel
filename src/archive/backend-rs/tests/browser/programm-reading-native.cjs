// #231: unchanged real UI, real origin/auth/SQL/private Blob, scripted delivery
// BELOW Gateway; separate held HTTP process proves disconnect/SIGTERM/accounting.
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const {once} = require('node:events');
const {randomUUID} = require('node:crypto');
const net = require('node:net'), http2 = require('node:http2');
const {Client} = require('pg');
const {chromium, expect} = require('@playwright/test');
const {BlobServiceClient,newPipeline,StorageSharedKeyCredential} = require('@azure/storage-blob');
const telemetry = require('./telemetry-capture.cjs').createCapture('programm-reading-native');
const binary=process.env.ARCHIVE_BINARY, probe=process.env.ARCHIVE_PROGRAMME_HTTP_PROBE;
const server=process.env.ARCHIVE_TEST_POSTGRES, connection=process.env.ARCHIVE_TEST_AZURITE;
assert(binary && probe && server && connection && process.env.ARCHIVE_STATIC_ROOT);
assert(['localhost','127.0.0.1'].includes(new URL(server).hostname));
const database=`test231_programm_${randomUUID().replaceAll('-','')}`;
const databaseUrl=new URL(server); databaseUrl.pathname=database;
const container=`test231-${randomUUID()}`, children=[], messages=[], signals={};
let number=0;
async function listen(listener) {listener.listen(0,'127.0.0.1');await once(listener,'listening');return listener.address().port;}
const smtp=net.createServer(socket=>{
 socket.setEncoding('utf8');socket.write('220 synthetic SMTP\r\n');let buffer='',data=false,mail='';
 socket.on('data',chunk=>{buffer+=chunk;let end;while((end=buffer.indexOf('\r\n'))>=0){const line=buffer.slice(0,end);buffer=buffer.slice(end+2);if(data){if(line==='.'){messages.push(mail);data=false;socket.write('250 accepted\r\n');}else mail+=line+'\n';continue;}if(/^(EHLO|HELO)/.test(line))socket.write('250 synthetic\r\n');else if(line==='DATA'){data=true;mail='';socket.write('354 send\r\n');}else if(line==='QUIT')socket.end('221 bye\r\n');else socket.write('250 ok\r\n');}});
});
const collector=telemetry.collector(http2.createServer());
collector.on('stream',(stream,headers)=>{const chunks=[];stream.on('data',c=>chunks.push(c));stream.on('end',()=>{const route=headers[':path'],bytes=Buffer.concat(chunks);telemetry.signal(route,bytes);signals[route]=Buffer.concat([signals[route]||Buffer.alloc(0),bytes]);stream.respond({':status':200,'content-type':'application/grpc'},{waitForTrailers:true});stream.on('wantTrailers',()=>stream.sendTrailers({'grpc-status':'0'}));stream.end(Buffer.from([0,0,0,0,0]));});});
async function stop(child,expected=0) {if(child.exitCode===null)child.kill('SIGTERM');assert.equal((await telemetry.closed(child))[0],expected);}
async function wait(check,label) {const deadline=Date.now()+10000;while(Date.now()<deadline){const result=await check();if(result)return result;await new Promise(r=>setTimeout(r,20));}throw Error(`Timed out: ${label}`);}
async function main() {
 const admin=new Client({connectionString:server});await admin.connect();await admin.query(`CREATE DATABASE ${database}`);
 let browser,db;
 const field=name=>connection.match(new RegExp(`(?:^|;)${name}=([^;]+)`))[1];
 telemetry.marker('key','storage-key',field('AccountKey'));
 telemetry.marker('email','editor-email','redaktion@liedertafel.test');
 telemetry.marker('object','synthetic-transcription','private-programme-native-231');
 const pipeline=newPipeline(new StorageSharedKeyCredential(field('AccountName'),field('AccountKey')));
 pipeline.factories.push({create(next){return{sendRequest(request){request.headers.set('x-ms-version','2025-11-05');return next.sendRequest(request);}}}});
 const service=new BlobServiceClient(field('BlobEndpoint'),pipeline),blobs=service.getContainerClient(container);
 try {
  await blobs.create();await service.setProperties({cors:[{allowedOrigins:'*',allowedMethods:'GET,HEAD,PUT,OPTIONS',allowedHeaders:'*',exposedHeaders:'*',maxAgeInSeconds:3600}]});
  const mailPort=await listen(smtp),otlpPort=await listen(collector),reserve=net.createServer(),port=await listen(reserve);await new Promise(r=>reserve.close(r));
  const base=`http://localhost:${port}`;
  const env={...process.env,ARCHIVE_ENVIRONMENT:'Development',PORT:String(port),RUST_LOG:'trace',OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${otlpPort}`,OTEL_BSP_SCHEDULE_DELAY:'100',OTEL_BLRP_SCHEDULE_DELAY:'100',OTEL_METRIC_EXPORT_INTERVAL:'100',Mail__Provider:'Smtp',Mail__Host:'127.0.0.1',Mail__Port:String(mailPort),'ConnectionStrings__archive-db':databaseUrl.href,'ConnectionStrings__archive-migrations':databaseUrl.href,'ConnectionStrings__archive-blobs':connection,Archive__Assets__ContainerName:container,Archive__Reading__Enabled:'true',Archive__Chat__Provider:'Scripted',Archive__Chat__Endpoint:'',Archive__Chat__DeploymentName:''};
  async function command(args) {const child=spawn(binary,args,{env,stdio:['ignore','pipe','pipe'],signal:AbortSignal.timeout(30000)});telemetry.watch(child,`finite-${number++}`);assert.equal((await telemetry.closed(child))[0],0);}
  async function start(executable=binary,expected=0,extra={}) {const child=spawn(executable,[],{env:{...env,...extra},stdio:['ignore','pipe','pipe']});children.push([child,expected]);telemetry.watch(child,`origin-${number++}`,expected);await wait(async()=>{if(child.exitCode!==null)throw Error('origin startup failed');try{return(await fetch(base+'/alive')).ok;}catch{return false;}},'native startup');return child;}
  await command(['--migrate']);await command(['--seed-dev-auth']);
  db=new Client({connectionString:databaseUrl.href});await db.connect();
  let origin=await start();browser=await chromium.launch({executablePath:process.env.ARCHIVE_CHROMIUM});
  const context=await browser.newContext(),page=await context.newPage(),flush=telemetry.browser(context,'editor'),errors=[];
  page.on('pageerror',error=>errors.push(error.message));context.on('request',request=>{const url=new URL(request.url());if(url.searchParams.has('sig'))telemetry.marker('signature','storage-sas',url.searchParams.get('sig'));});
  await page.goto(base+'/anmelden/');await page.getByLabel('E-Mail-Adresse',{exact:true}).fill('redaktion@liedertafel.test');await page.getByRole('button',{name:'Code anfordern'}).click();await expect(page.getByLabel('Code aus der E-Mail')).toBeVisible();
  const code=messages.findLast(m=>m.includes('redaktion@liedertafel.test')).match(/Ihr Anmeldecode lautet:\s*(\d{6})/)[1];telemetry.marker('otp','login-code',code);await page.getByLabel('Code aus der E-Mail').fill(code);await page.getByRole('button',{name:'Anmelden',exact:true}).click();await expect(page).toHaveURL(/\/archiv\//);
  async function mutate(path,data) {const token=(await(await context.request.get(base+'/api/antiforgery')).json()).token;const response=await context.request.post(base+path,{data,headers:{'X-CSRF-TOKEN':token}});assert(response.ok());return response.json();}
  await mutate('/api/lieder',{title:'Ave Maria'});await mutate('/api/lieder',{title:'Der Mond ist aufgegangen'});
  const fixture=require('../fixtures/231-programme-oracle.json').find(row=>row.kind==='photo'&&row.format==='Png');
  const photo={name:'programm.png',mimeType:'image/png',buffer:Buffer.from(fixture.bytes,'base64')};
  // Native transport envelope is distinct from the first named Datei's cap:
  // a small valid first Datei cannot hide an oversized extra section.
  const envelopeToken=(await(await context.request.get(base+'/api/antiforgery')).json()).token;
  const envelopeCookie=(await context.cookies()).map(c=>`${c.name}=${c.value}`).join('; ');
  telemetry.marker('csrf','envelope-token',envelopeToken);
  const envelope=new FormData();envelope.set('entwurfId',randomUUID());envelope.set('anfrageId','1');envelope.set('datei',new Blob([photo.buffer]),'p.png');envelope.set('ignored',new Blob([Buffer.alloc(30000000)]),'extra.bin');
  assert.equal((await fetch(base+'/api/auftritt-lesungen',{method:'POST',headers:{cookie:envelopeCookie,'X-CSRF-TOKEN':envelopeToken},body:envelope})).status,413);
  assert.equal((await db.query('SELECT count(*)::int n FROM ai_usage_entries')).rows[0].n,0);
  await stop(origin);origin=await start(probe,0,{Archive__Reading__MaxPdfBytes:'10'});
  const smallCap=new FormData();smallCap.set('entwurfId',randomUUID());smallCap.set('anfrageId','1');smallCap.set('datei',new Blob([photo.buffer]),'p.png');
  assert.equal((await fetch(base+'/api/auftritt-lesungen',{method:'POST',headers:{cookie:envelopeCookie,'X-CSRF-TOKEN':envelopeToken},body:smallCap})).status,422);
  assert.equal((await db.query('SELECT count(*)::int n FROM ai_usage_entries')).rows[0].n,0);
  await stop(origin);origin=await start();
  await page.goto(base+'/auftritte/');await page.locator('details.auftritt-anlegen > summary').click();await page.getByLabel('Dateien für den Auftritt auswählen').setInputFiles(photo);
  await expect(page.getByLabel('Titel',{exact:true})).toHaveValue('Sängerfest der Liedertafel Mining');await expect(page.getByLabel('Jahr',{exact:true})).toHaveValue('1925');await expect(page.getByLabel('Monat',{exact:true})).toHaveValue('6');await expect(page.getByLabel('Tag',{exact:true})).toHaveValue('14');
  await expect(page.locator('li[data-lied="sicher"]')).toHaveCount(2);await expect(page.locator('li[data-lied="offen"]')).toHaveCount(1);
  assert.equal((await(await context.request.get(base+'/api/events')).json()).events.length,0);
  assert.equal((await(await context.request.get(base+'/api/lieder')).json()).lieder.length,2);
  await page.getByLabel('Titel',{exact:true}).fill('Redaktioneller Sängerfesttitel');await page.getByRole('button',{name:'Waldfahrt als neues Lied anlegen'}).click();
  await expect.poll(async()=>{const b=await(await context.request.get(base+'/api/auftritt-entwuerfe')).json();return b.entwuerfe.some(e=>e.titel==='Redaktioneller Sängerfesttitel');}).toBe(true);
  const beforeResume=Number((await db.query('SELECT sum("Calls")::bigint calls FROM ai_usage_entries')).rows[0].calls);
  await stop(origin);origin=await start();await page.goto(base+'/verwaltung/');await page.getByRole('link',{name:'Redaktioneller Sängerfesttitel fortsetzen'}).click();await expect(page.getByLabel('Titel',{exact:true})).toHaveValue('Redaktioneller Sängerfesttitel');
  assert.equal(Number((await db.query('SELECT sum("Calls")::bigint calls FROM ai_usage_entries')).rows[0].calls),beforeResume);
  await page.getByLabel('Dateien für den Auftritt auswählen').setInputFiles(photo);await expect(page.getByRole('button',{name:'Speichern',exact:true})).toBeEnabled();await page.getByRole('button',{name:'Speichern',exact:true}).click();await expect(page).toHaveURL(/\/auftritt\/\?id=/);
  const event=new URL(page.url()).searchParams.get('id'),detail=(await(await context.request.get(`${base}/api/events/${event}`)).json()).event;
  assert.equal(detail.title,'Redaktioneller Sängerfesttitel');assert.equal(detail.dateDisplay,'14. Juni 1925');assert.equal(detail.programme.items.length,3);assert.equal(detail.documents.length,1);
  assert.equal((await(await context.request.get(base+'/api/auftritt-entwuerfe')).json()).entwuerfe.length,0);
  await stop(origin);
  // The offline host uses exactly the real programme route and shared lifetime
  // tracker; it holds ONLY delivery, so reservations, ACKs and settlement are real.
  origin=await start(probe);
  const csrf=(await(await context.request.get(base+'/api/antiforgery')).json()).token;
  const cookie=(await context.cookies()).map(c=>`${c.name}=${c.value}`).join('; ');
  telemetry.marker('csrf','raw-http-token',csrf);
  const active=async()=>Number((await db.query('SELECT COALESCE(sum("ReservedMicroEur"),0)::bigint n FROM ai_usage_entries')).rows[0].n);
  const fieldsActive=async()=>Number((await db.query('SELECT COALESCE(sum("ReservedMicroEur"),0)::bigint n FROM ai_usage_entries WHERE "Feature"=\'programm-fields\'')).rows[0].n)>0;
  async function request(controller) {const form=new FormData();form.set('entwurfId',randomUUID());form.set('anfrageId','1');form.set('datei',new Blob([photo.buffer],{type:'image/png'}),'p.png');return fetch(base+'/api/auftritt-lesungen',{method:'POST',headers:{cookie,'X-CSRF-TOKEN':csrf,traceparent:'00-23112311231123112311231123112311-2311231123112311-01'},body:form,signal:controller.signal});}
  const dropped=new AbortController(),pending=request(dropped).catch(e=>e.name);await wait(async()=>await active()>0,'held request');
  // Wait for the second feature, not merely the transcription reservation.
  await wait(fieldsActive,'held fields');dropped.abort();assert.equal(await pending,'AbortError');await wait(async()=>await active()===0,'disconnect settlement');
  assert((await fetch(base+'/alive')).ok);
  const duringShutdown=new AbortController(),closing=request(duringShutdown).catch(e=>e.name);await wait(fieldsActive,'shutdown reservation');await stop(origin);await closing;assert.equal(await active(),0);
  const paid=(await db.query('SELECT "Feature","InputTokens","OutputTokens","ReservedMicroEur","CostMicroEur" FROM ai_usage_entries ORDER BY "CreatedAt" DESC LIMIT 4')).rows;
  assert.equal(paid.filter(r=>r.Feature==='programm-transcription'&&Number(r.InputTokens)===100&&Number(r.OutputTokens)===20).length,2);assert.equal(paid.filter(r=>r.Feature==='programm-fields'&&Number(r.OutputTokens)===3000&&Number(r.ReservedMicroEur)===0).length,2);
  assert.equal((await db.query('SELECT count(*)::int n FROM events')).rows[0].n,1);assert.equal((await db.query('SELECT count(*)::int n FROM auftritt_entwuerfe')).rows[0].n,0);
  origin=await start(probe,1);const failing=new AbortController(),failure=request(failing).catch(e=>e.name);await wait(fieldsActive,'failed settlement reservation');
  await db.query('CREATE FUNCTION test231_reject_settle() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW."ReservedMicroEur"=0 AND OLD."ReservedMicroEur">0 THEN RAISE EXCEPTION \'synthetic settlement unavailable\'; END IF; RETURN NEW; END $$');await db.query('CREATE TRIGGER test231_reject_settle BEFORE UPDATE ON ai_usage_entries FOR EACH ROW EXECUTE FUNCTION test231_reject_settle()');
  await stop(origin,1);await failure;assert(await active()>0,'failed settlement must retain reservation and fail the host');
  for(const row of(await db.query('SELECT "BlobName" name FROM dateien UNION SELECT "BlobName" name FROM upload_sessions')).rows)telemetry.marker('object','stored-blob',row.name);
  await flush();assert.deepEqual(errors,[]);await browser.close();browser=undefined;await telemetry.drain();
  for(const route of ['TraceService','MetricsService','LogsService'])assert(Object.keys(signals).some(r=>r.includes(route)));
  const traces=Buffer.concat(Object.entries(signals).filter(([r])=>r.includes('TraceService')).map(([,b])=>b));assert(traces.includes(Buffer.from('archive-programm-transcriber')));assert(traces.includes(Buffer.from('archive-programm-fields')));
  telemetry.assertClean();console.log('PASS: actual programme photo/UI/opaque draft restart/editor save; actual stateless HTTP disconnect/SIGTERM/settlement failure, producer join, reported+unknown usage, three-signal checked flush/privacy');
 } finally {
  if(browser)await browser.close();for(const [child,expected] of children)await stop(child,expected);if(db)await db.end();smtp.close();await telemetry.drain();await admin.end();
  // Own synthetic database/container intentionally retained for evidence audit.
 }
}
main().catch(error=>{require('node:fs').writeFileSync('/tmp/opencode/231-programme-native-failure.txt',String(error.stack),{mode:0o600});console.error('Programme native gate failed; inspect private 231-programme-native-failure.txt');process.exitCode=1;});
