// Frozen .NET executable vs native Rust, normal email/CSRF, separate real PG DBs.
// Fixture setup is SQL; neither frozen sources nor their tests are edited.
const assert=require('node:assert/strict');
const {spawn,execFileSync}=require('node:child_process'),{once}=require('node:events');
const {randomUUID}=require('node:crypto'),fs=require('node:fs'),path=require('node:path'),net=require('node:net'),http2=require('node:http2');
const {Client}=require('pg');
const telemetry=require('./telemetry-capture.cjs').createCapture('query-binding');
const server=process.env.ARCHIVE_TEST_POSTGRES;
assert(server&&['127.0.0.1','localhost','[::1]'].includes(new URL(server).hostname),'explicit local disposable PG required');
const frozen=path.resolve(__dirname,'../../../backend'),rust=process.env.ARCHIVE_BINARY||path.resolve(__dirname,'../../target/debug/archive-server');
const dll=path.join(frozen,'bin/Debug/net10.0/Archive.Backend.dll');
const rows=[
  {title:'Mond',voiceConfiguration:'TTBB',occasion:'Abend',kinds:['noten','uebe']},
  {title:'Sonne',voiceConfiguration:'SATB',occasion:'Morgen',kinds:['noten']},
  {title:'Mond,Sonne',voiceConfiguration:'TTBB,SATB',occasion:'Abend,Morgen',kinds:['uebe']},
  ...Array.from({length:21},(_,i)=>({title:`Zahl ${String(i).padStart(2,'0')}`,kinds:[]})),
];
const queries=['','?Q=mond','?%51=sonne','?BESETZUNG=TTBB','?ANLASS=Abend','?VOICECONFIGURATION=TTBB','?OCCASION=Abend','?DATEITYP=noten','?PAGE=2',
  '?q=Mond&Q=Sonne','?Q=Mond&q=Mond','?q=&Q=Mond','?Q=Mond&q=','?q=&Q=',
  '?besetzung=TTBB&BESETZUNG=SATB','?VOICECONFIGURATION=TTBB&voiceconfiguration=SATB','?BESETZUNG=&voiceConfiguration=TTBB','?voiceConfiguration=SATB&BESETZUNG=TTBB',
  '?anlass=Abend&ANLASS=Morgen','?OCCASION=Abend&occasion=Morgen','?ANLASS=&occasion=Abend','?occasion=Morgen&ANLASS=Abend',
  '?dateiTyp=noten&DATEITYP=uebe','?DATEITYP=uebe&DaTeItYp=noten','?dateiTyp=noten&DATEITYP=NOTEN','?DATEITYP=noten%2Cuebe','?DATEITYP=&dateiTyp=noten',
  '?page=1&PAGE=2','?PAGE=2&page=2','?page=&PAGE=2','?PAGE=','?page=&PAGE=','?PAGE=%20%2B2%20','?PAGE=0',
  '?Q=mond&BESETZUNG=TTBB&ANLASS=Abend&DATEITYP=noten&dateiTyp=uebe',
  `?q=${'a'.repeat(100)}&Q=${'b'.repeat(100)}`];
const messages=[],children=[];
const smtp=net.createServer(socket=>{socket.setEncoding('utf8');socket.write('220 local SMTP\r\n');let buffer='',data=false,mail='';socket.on('data',chunk=>{buffer+=chunk;let at;while((at=buffer.indexOf('\r\n'))>=0){const line=buffer.slice(0,at);buffer=buffer.slice(at+2);if(data){if(line==='.'){messages.push(mail);data=false;socket.write('250 accepted\r\n');}else mail+=line+'\n';}else if(/^(EHLO|HELO)/.test(line))socket.write('250 local\r\n');else if(line==='DATA'){data=true;mail='';socket.write('354 send\r\n');}else if(line==='QUIT')socket.end('221 bye\r\n');else socket.write('250 ok\r\n');}});});
const collector=telemetry.collector(http2.createServer());collector.on('stream',(stream,headers)=>{const chunks=[];stream.on('data',c=>chunks.push(c));stream.on('end',()=>{telemetry.signal(headers[':path'],Buffer.concat(chunks));stream.respond({':status':200,'content-type':'application/grpc'},{waitForTrailers:true});stream.on('wantTrailers',()=>stream.sendTrailers({'grpc-status':'0'}));stream.end(Buffer.alloc(5));});});
async function listen(s){s.listen(0,'127.0.0.1');await once(s,'listening');return s.address().port;}
async function port(){const s=net.createServer(),p=await listen(s);await new Promise(r=>s.close(r));return p;}
async function stop(child){if(child.exitCode===null)child.kill('SIGTERM');assert.equal((await telemetry.closed(child))[0],0,'native origin stopped cleanly');}
function projection(status,body){if(status!==200)return {status};return {status,query:body.query,page:body.page,total:body.total,filters:body.filters,titles:body.lieder.map(l=>l.title)};}
async function run(kind,mailPort,otlpPort){
  const admin=new Client({connectionString:server});await admin.connect();
  const database=`query_${kind}_${randomUUID().replaceAll('-','')}`,url=new URL(server);url.pathname=database;
  await admin.query(`CREATE DATABASE ${database}`);let db,origin;
  try{
    const p=await port(),base=`http://127.0.0.1:${p}`;
    const connection=kind==='dotnet'?`Host=${url.hostname};Port=${url.port};Database=${database};Username=${decodeURIComponent(url.username)};Password='${decodeURIComponent(url.password).replaceAll("'","''")}'`:url.href;
    const env={...process.env,ASPNETCORE_ENVIRONMENT:'Development',DOTNET_ENVIRONMENT:'Development',ARCHIVE_ENVIRONMENT:'Development',ASPNETCORE_URLS:base,PORT:String(p),OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${otlpPort}`,OTEL_EXPORTER_OTLP_TIMEOUT:'5000',OTEL_BSP_SCHEDULE_DELAY:'100',OTEL_BLRP_SCHEDULE_DELAY:'100',OTEL_METRIC_EXPORT_INTERVAL:'100',RUST_LOG:'trace',Logging__LogLevel__Default:'Warning',Mail__Provider:'Smtp',Mail__Host:'127.0.0.1',Mail__Port:String(mailPort),'ConnectionStrings__archive-db':connection,'ConnectionStrings__archive-migrations':connection,Development__KeysPath:path.join(telemetry.directory,'dotnet-keys'),Archive__Reading__Enabled:'false',ASPNETCORE_WEBROOT:process.env.ARCHIVE_STATIC_ROOT};
    function start(args){const hostEnv=kind==='dotnet'&&!args.length?{...env,ASPNETCORE_ENVIRONMENT:process.env.ARCHIVE_QUERY_DOTNET_ENVIRONMENT||'Testing',DOTNET_ENVIRONMENT:process.env.ARCHIVE_QUERY_DOTNET_ENVIRONMENT||'Testing',Authentication__AllowEphemeralKeysForTests:'true',Authentication__PasskeyRelyingPartyId:'localhost',Authentication__PasskeyOrigins__0:'https://localhost'}:env;const child=spawn(kind==='dotnet'?'dotnet':rust,kind==='dotnet'?[dll,...args]:args,{cwd:kind==='dotnet'?frozen:undefined,env:hostEnv,stdio:['ignore','pipe','pipe']});telemetry.watch(child,`${kind}-${children.length}`);children.push(child);return child;}
    for(const args of [['--migrate'],['--seed-dev-auth']])assert.equal((await telemetry.closed(start(args)))[0],0,`${kind} fixture command`);
    db=new Client({connectionString:url.href});await db.connect();
    for(const row of rows){
      const id=randomUUID(),actor='00000000-0000-0000-0000-000000000000';
      await db.query('INSERT INTO lieder ("Id","Title","VoiceConfiguration","Occasion","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,now(),now(),$5,$5,1)',[id,row.title,row.voiceConfiguration||null,row.occasion||null,actor]);
      for(const kind of row.kinds){const file=randomUUID();await db.query('INSERT INTO dateien ("Id","LiedId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","OriginalFileName","CreatedByAccountId","CreatedAt","RowVersion") VALUES ($1,$2,$3,false,$4,\'application/pdf\',1,\'fixture.pdf\',$5,now(),1)',[file,id,kind,`dateien/${file}/fixture`,actor]);}
    }
    origin=start([]);let ready=false;
    for(let i=0;i<400;i++){if(origin.exitCode!==null)throw Error('native query origin exited');try{if((await fetch(base+'/alive')).ok){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,25));}assert(ready,'native query origin ready');
    const cookies=new Map();let csrf='';
    async function call(route,body){const response=await fetch(base+route,{method:body===undefined?'GET':'POST',headers:{cookie:[...cookies].map(([k,v])=>`${k}=${v}`).join('; '),'content-type':'application/json','X-CSRF-TOKEN':csrf,'x-forwarded-proto':'https'},body:body===undefined?undefined:JSON.stringify(body)});for(const cookie of response.headers.getSetCookie()){const pair=cookie.split(';')[0],at=pair.indexOf('=');cookies.set(pair.slice(0,at),pair.slice(at+1));telemetry.marker('cookie',`${kind}-cookie`,pair.slice(at+1));}const text=await response.text();let value;try{value=JSON.parse(text);}catch{value=null;}return {status:response.status,body:value};}
    assert.equal((await fetch(base+'/api/lieder?Q=mond&DATEITYP=noten')).status,401);
    const email='mitglied@liedertafel.test';telemetry.marker('email',`${kind}-member`,email);
    csrf=(await call('/api/antiforgery')).body.token;telemetry.marker('cookie',`${kind}-csrf`,csrf);
    assert.equal((await call('/api/auth/code/request',{email})).status,202);
    const mail=messages.at(-1).replace(/=\n/g,'').replace(/=([0-9a-f]{2})/gi,(_,h)=>String.fromCharCode(parseInt(h,16)));
    const code=/Ihr Anmeldecode lautet:\s*(\d{6})/.exec(mail)?.[1];assert(code,'actual SMTP code');telemetry.marker('otp',`${kind}-code`,code);
    assert.equal((await call('/api/auth/code/verify',{email,code})).status,200);
    assert.equal((await call('/api/auth/me')).body.authenticated,true);
    const results=[];
    for(const query of queries){const result=await call('/api/lieder'+query);results.push({request:query,...projection(result.status,result.body)});}
    return results;
  }finally{try{if(origin)await stop(origin);}finally{if(db)await db.end();await admin.query(`DROP DATABASE ${database} WITH (FORCE)`);await admin.end();}}
}
async function main(){
  execFileSync('git',['diff','--exit-code','dc3357e','--','src/archive/backend'],{cwd:path.resolve(frozen,'../../..'),stdio:'pipe'});
  const mailPort=await listen(smtp),otlpPort=await listen(collector);
  try{
    const oracle=await run('dotnet',mailPort,otlpPort);
    if(process.env.ARCHIVE_QUERY_ORACLE){assert(process.argv.includes('--oracle-only'),'fixture generation requires explicit oracle-only run');fs.writeFileSync(process.env.ARCHIVE_QUERY_ORACLE,JSON.stringify({source:'frozen .NET native /api/lieder + real PG + real email/CSRF',environment:process.env.ARCHIVE_QUERY_DOTNET_ENVIRONMENT||'Testing',sourceTree:execFileSync('git',['rev-parse','dc3357e:src/archive/backend'],{encoding:'utf8'}).trim(),rows,cases:oracle},null,2)+'\n',{mode:0o600});}
    if(!process.argv.includes('--oracle-only')){
      const fixture=require('../fixtures/catalogue-query-binding.json');
      assert.deepEqual(rows,fixture.rows);assert.deepEqual(oracle,fixture.cases,'fresh frozen executable must reproduce the committed independent observations');
      const actual=await run('rust',mailPort,otlpPort);assert.deepEqual(actual,oracle,'Rust query projection must equal the actual frozen .NET executable');
    }
    await telemetry.drain();telemetry.assertClean();console.log(`PASS: ${oracle.length} frozen .NET ${process.argv.includes('--oracle-only')?'query observations':'vs Rust authenticated query cases'}`);
  }finally{for(const child of children)if(child.exitCode===null)await stop(child);smtp.close();await telemetry.drain();}
}
main().catch(()=>{console.error('Native query differential failed; inspect private capture and local query evidence.');process.exitCode=1;});
