// #222: actual native CLI/origin, real PG, unchanged exported UI and Chromium.
// Only delivery is synthetic SMTP. No production/ACS/AI calls or frontend mocks.
const assert=require('node:assert/strict');
const {spawn}=require('node:child_process');
const {once}=require('node:events');
const {randomUUID}=require('node:crypto');
const net=require('node:net'),http2=require('node:http2'),path=require('node:path');
const {Client}=require('pg');
const {chromium,expect}=require('@playwright/test');
const binary=process.env.ARCHIVE_BINARY||path.resolve(__dirname,'../../target/debug/archive-server');
const server=process.env.ARCHIVE_TEST_POSTGRES;
assert(server && process.env.ARCHIVE_STATIC_ROOT,'explicit local PG + unchanged static export required');
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(server).hostname));
const database=`members_native_${randomUUID().replaceAll('-','')}`;
const databaseUrl=new URL(server);databaseUrl.pathname=database;
const messages=[],signals={},children=[];let output='';
async function listen(listener){listener.listen(0,'127.0.0.1');await once(listener,'listening');return listener.address().port;}
const smtp=net.createServer(socket=>{socket.setEncoding('utf8');socket.write('220 synthetic SMTP\r\n');let buffer='',data=false,mail='';socket.on('data',chunk=>{buffer+=chunk;let end;while((end=buffer.indexOf('\r\n'))>=0){const line=buffer.slice(0,end);buffer=buffer.slice(end+2);if(data){if(line==='.'){messages.push(mail);data=false;socket.write('250 accepted\r\n');}else mail+=line+'\n';continue;}if(/^(EHLO|HELO)/.test(line))socket.write('250 synthetic\r\n');else if(line==='DATA'){data=true;mail='';socket.write('354 send\r\n');}else if(line==='QUIT')socket.end('221 bye\r\n');else socket.write('250 ok\r\n');}});});
const collector=http2.createServer();collector.on('stream',(stream,headers)=>{const chunks=[];stream.on('data',c=>chunks.push(c));stream.on('end',()=>{const route=headers[':path'];signals[route]=Buffer.concat([signals[route]||Buffer.alloc(0),...chunks]);stream.respond({':status':200,'content-type':'application/grpc'},{waitForTrailers:true});stream.on('wantTrailers',()=>stream.sendTrailers({'grpc-status':'0'}));stream.end(Buffer.from([0,0,0,0,0]));});});
async function stop(child){if(child.exitCode===null){child.kill('SIGTERM');const [code]=await once(child,'exit');assert.equal(code,0);}}
async function main(){
 const adminDb=new Client({connectionString:server});await adminDb.connect();await adminDb.query(`CREATE DATABASE ${database}`);
 let db,browser;
 try{
  const mailPort=await listen(smtp),otlpPort=await listen(collector);
  const reserve=net.createServer(),port=await listen(reserve);await new Promise(r=>reserve.close(r));
  const base=`http://localhost:${port}`;
  const env={...process.env,ARCHIVE_ENVIRONMENT:'Development',PORT:String(port),RUST_LOG:'trace',OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${otlpPort}`,OTEL_BSP_SCHEDULE_DELAY:'100',OTEL_BLRP_SCHEDULE_DELAY:'100',OTEL_METRIC_EXPORT_INTERVAL:'100',Mail__Provider:'Smtp',Mail__Host:'127.0.0.1',Mail__Port:String(mailPort),'ConnectionStrings__archive-db':databaseUrl.href,'ConnectionStrings__archive-migrations':databaseUrl.href};
  async function command(args,expected=0){const child=spawn(binary,args,{env,stdio:['ignore','pipe','pipe'],signal:AbortSignal.timeout(30000)});let log='';child.stdout.on('data',c=>log+=c);child.stderr.on('data',c=>log+=c);const [code]=await once(child,'exit');output+=log;assert.equal(code,expected,log);}
  await command(['--migrate']);await command(['--bootstrap-admin','--email','operator@liedertafel.test','--operator','Synthetic operator']);await command(['--seed-dev-auth']);await command(['--bootstrap-admin','--email','must-not-exist@liedertafel.test'],1);
  db=new Client({connectionString:databaseUrl.href});await db.connect();
  async function start(){const child=spawn(binary,[],{env,stdio:['ignore','pipe','pipe']});children.push(child);child.stdout.on('data',c=>output+=c);child.stderr.on('data',c=>output+=c);for(let i=0;i<200;i++){if(child.exitCode!==null)throw Error(output);try{if((await fetch(base+'/alive')).ok)return child;}catch{}await new Promise(r=>setTimeout(r,25));}throw Error('startup timeout');}
  let origin=await start();browser=await chromium.launch({executablePath:process.env.ARCHIVE_CHROMIUM});
  const admin=await browser.newContext(),member=await browser.newContext();const page=await admin.newPage(),singer=await member.newPage();const errors=[];for(const p of [page,singer])p.on('pageerror',e=>errors.push(e.message));
  function decode(mail){if(!/Content-Transfer-Encoding: quoted-printable/i.test(mail))return mail;const at=mail.indexOf('\n\n');return mail.slice(0,at+2)+Buffer.from(mail.slice(at+2).replace(/=\n/g,'').replace(/=([0-9A-F]{2})/gi,(_,hex)=>String.fromCharCode(parseInt(hex,16))),'latin1').toString('utf8');}
  function code(email,change=false){const marker=change?'Ihr Bestätigungscode lautet:':'Ihr Anmeldecode lautet:';const message=messages.map(decode).findLast(m=>m.includes(email)&&m.includes(marker));const found=message?.match(new RegExp(marker+'\\s*(\\d{6})'))?.[1];assert(found,'missing synthetic code');return found;}
  async function login(p,email){await p.goto(base+'/anmelden/');await p.getByLabel('E-Mail-Adresse',{exact:true}).fill(email);await p.getByRole('button',{name:'Code anfordern'}).click();await expect(p.getByLabel('Code aus der E-Mail')).toBeVisible();await p.getByLabel('Code aus der E-Mail').fill(code(email));await p.getByRole('button',{name:'Anmelden',exact:true}).click();await expect(p).toHaveURL(/\/archiv\//);}
  async function me(p){return p.evaluate(async()=>(await fetch('/api/auth/me')).json());}
  await login(page,'verwaltung@liedertafel.test');await page.goto(base+'/verwaltung/');await expect(page.getByRole('heading',{name:'Mitgliederverwaltung'})).toBeVisible();
  const email='browser-member@liedertafel.test',moved='browser-moved@liedertafel.test';
  await page.locator('details.mitglied-einladen > summary').click();await page.getByLabel('E-Mail-Adresse',{exact:true}).fill(email);await page.getByLabel('Name (optional)').fill('Browser Stimme');await page.getByLabel('Rolle',{exact:true}).selectOption('Member');await page.getByRole('button',{name:'Einladung senden'}).click();await expect(page.getByText('Einladung erstellt')).toBeVisible();
  let row=page.getByRole('row',{name:new RegExp(email)});await expect(row).toBeVisible();await row.getByRole('button',{name:'Erneut senden'}).click();await expect(page.getByText('erneut zum Versand übergeben')).toBeVisible();
  await login(singer,email);const id=(await me(singer)).accountId;await singer.goto(base+'/verwaltung/');await expect(singer.getByText('Keine Berechtigung für die Mitgliederverwaltung.')).toBeVisible();
  await singer.goto(base+'/archiv/');const cdp=await member.newCDPSession(singer);await cdp.send('WebAuthn.enable',{enableUI:false});await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
  await singer.getByLabel('Name für den neuen Passkey').fill('Member browser key');await singer.getByRole('button',{name:'Passkey hinzufügen'}).click();await expect(singer.getByText('Passkey registriert.',{exact:true})).toBeVisible();
  await row.getByLabel(/Rolle für/).selectOption('Editor');await row.getByRole('button',{name:'Rolle speichern'}).click();await expect(page.getByText('Rolle aktualisiert')).toBeVisible();assert.deepEqual((await me(singer)).roles,['Editor']);
  await row.getByRole('button',{name:'Deaktivieren'}).click();await expect(row.getByText('Deaktiviert',{exact:true})).toBeVisible();assert.equal((await me(singer)).authenticated,false);
  await row.getByRole('button',{name:'Reaktivieren'}).click();await expect(row.getByText('Aktiv',{exact:true})).toBeVisible();assert.equal((await me(singer)).authenticated,false);
  await login(singer,email);assert.equal((await me(singer)).accountId,id);
  await row.locator('details.adress-wechsel').evaluate(e=>e.open=true);await row.getByLabel(`Neue E-Mail-Adresse für ${email}`).fill(moved);await row.getByRole('button',{name:'Code senden'}).evaluate(e=>e.click());await expect(page.getByText(/Bestätigungscode an die neue Adresse gesendet/)).toBeVisible();await row.getByLabel(`Bestätigungscode für ${moved}`).fill(code(moved,true));await row.getByRole('button',{name:'Adresse bestätigen'}).evaluate(e=>e.click());await expect(page.getByText('Adresse geändert.')).toBeVisible();assert.equal((await me(singer)).authenticated,false);
  await stop(origin);origin=await start();await singer.goto(base+'/anmelden/');await singer.getByRole('button',{name:'Mit Passkey anmelden'}).click();await expect(singer).toHaveURL(/\/archiv\//);assert.equal((await me(singer)).accountId,id);assert.equal((await me(singer)).email,moved);assert.equal((await me(singer)).authMethod,'passkey');
  await command(['--repair-admin','--accountId',id,'--email','browser-repaired@liedertafel.test','--operator','Synthetic operator']);assert.equal((await me(singer)).authenticated,false);assert.equal((await db.query('SELECT count(*)::int AS n FROM "AspNetUserPasskeys" WHERE "UserId"=$1',[id])).rows[0].n,0);
  const audit=(await db.query('SELECT * FROM member_admin_actions WHERE "TargetUserId"=$1 ORDER BY "OccurredAt"',[id])).rows;assert.deepEqual(audit.map(a=>a.Action),[5,2,0,1,3,4]);assert.equal(audit.at(-1).ActorAccountId,'00000000-0000-0000-0000-000000000000');
  assert.deepEqual(errors,[]);await browser.close();browser=undefined;await stop(origin);
  const traces=Object.entries(signals).filter(([k])=>k.includes('TraceService')).map(([,v])=>v.toString()).join('');const metrics=Object.entries(signals).filter(([k])=>k.includes('MetricsService')).map(([,v])=>v.toString()).join('');const logs=Object.entries(signals).filter(([k])=>k.includes('LogsService')).map(([,v])=>v.toString()).join('');
  for(const span of ['bootstrap-admin','seed-dev-auth','repair-admin','archive.members.invite','archive.members.resend','archive.members.role_change','archive.members.deactivate','archive.members.reactivate','archive.members.email_change.request','archive.members.email_change.confirm','archive.mail.invite.send','archive.mail.email_change.send'])assert(traces.includes(span),`missing ${span}`);
  for(const metric of ['archive.members.invited','archive.members.resent','archive.members.role_changed','archive.members.deactivated','archive.members.reactivated','archive.members.email_change_requested','archive.members.email_changed'])assert(metrics.includes(metric),`missing ${metric}`);
  assert(logs.includes('Archive job bootstrap-admin failed'),'handled finite failure must flush OTLP logs');
  const all=traces+metrics+logs+output;for(const secret of ['operator@liedertafel.test','must-not-exist@liedertafel.test','verwaltung@liedertafel.test',email,moved,'browser-repaired@liedertafel.test','Member browser key',...messages.map(decode).flatMap(m=>[...m.matchAll(/(?:Anmeldecode|Bestätigungscode) lautet:\s*(\d{6})/g)].map(m=>m[1]))])assert(!all.includes(secret),`telemetry leaked member marker`);
  console.log('PASS: native bootstrap/seed/repair, unchanged UI invite/resend/role/deactivate/reactivate/verified email repair, retained real passkey after email move and restart, revoked repair credentials/session, real SMTP and finite/operator/member/mail OTLP with redaction');
 }finally{if(browser)await browser.close();for(const child of children)await stop(child);if(db)await db.end();smtp.close();collector.close();await adminDb.query(`DROP DATABASE ${database} WITH (FORCE)`);await adminDb.end();}
}
main().catch(e=>{console.error(e);process.exitCode=1;});
