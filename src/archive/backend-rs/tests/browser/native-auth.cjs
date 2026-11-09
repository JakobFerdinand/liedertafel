// Native-process restart/replica + real PG + SMTP delivery boundary + OTLP wire gate.
// Uses only a freshly-created local throwaway DB; no live credentials/mail/AI.
const assert=require("node:assert/strict");
const net=require("node:net");
const http2=require("node:http2");
const {spawn,execFileSync}=require("node:child_process");
const {once}=require("node:events");
const {randomUUID,createHash,createPrivateKey,sign}=require("node:crypto");
const fixture=require("../fixtures/dotnet-es256.json");
const {Client}=require("pg");
const path=require("node:path");

const binary=path.resolve(__dirname,"../../target/debug/archive-server");
const server=process.env.ARCHIVE_TEST_POSTGRES;
assert(server,"dedicated local ARCHIVE_TEST_POSTGRES required");
const databaseUrl=new URL(server);
assert(["localhost","127.0.0.1","[::1]"].includes(databaseUrl.hostname),"refuses nonlocal database");
const database=`auth_native_${randomUUID().replaceAll("-","")}`;
databaseUrl.pathname=database;
const messages=[];
const signals={};
let output="";
const children=[];
async function listen(listener){listener.listen(0,"127.0.0.1");await once(listener,"listening");return listener.address().port;}
const smtp=net.createServer(socket=>{
  socket.setEncoding("utf8");socket.write("220 synthetic SMTP\r\n");let buffer="",data=false,mail="";
  socket.on("data",chunk=>{buffer+=chunk;let newline;
    while((newline=buffer.indexOf("\r\n"))>=0){const line=buffer.slice(0,newline);buffer=buffer.slice(newline+2);
      if(data){if(line==="."){messages.push(mail);data=false;socket.write("250 accepted\r\n");}else{mail+=`${line}\n`;}continue;}
      if(/^(EHLO|HELO)/.test(line))socket.write("250 synthetic\r\n");
      else if(line==="DATA"){data=true;mail="";socket.write("354 send\r\n");}
      else if(line==="QUIT"){socket.end("221 bye\r\n");}
      else socket.write("250 ok\r\n");
    }
  });
});
const collector=http2.createServer();
collector.on("stream",(stream,headers)=>{
  const chunks=[];stream.on("data",c=>chunks.push(c));
  stream.on("end",()=>{
    const route=headers[":path"];
    signals[route]=Buffer.concat([signals[route]||Buffer.alloc(0),...chunks]);
    stream.respond({":status":200,"content-type":"application/grpc"},{waitForTrailers:true});
    stream.on("wantTrailers",()=>stream.sendTrailers({"grpc-status":"0"}));
    stream.end(Buffer.from([0,0,0,0,0]));
  });
});
async function port(){const listener=net.createServer();const port=await listen(listener);await new Promise(r=>listener.close(r));return port;}
async function start(port,mailPort,otlpPort){
  const child=spawn(binary,[],{env:{...process.env,ARCHIVE_ENVIRONMENT:"Development",PORT:String(port),RUST_LOG:"trace",OTEL_EXPORTER_OTLP_ENDPOINT:`http://127.0.0.1:${otlpPort}`,OTEL_EXPORTER_OTLP_TIMEOUT:"5000",OTEL_BSP_SCHEDULE_DELAY:"100",OTEL_BLRP_SCHEDULE_DELAY:"100",OTEL_METRIC_EXPORT_INTERVAL:"100",Mail__Provider:"Smtp",Mail__Host:"127.0.0.1",Mail__Port:String(mailPort),Authentication__PasskeyRelyingPartyId:"localhost",Authentication__PasskeyOrigins__0:"http://localhost","ConnectionStrings__archive-db":databaseUrl.href},stdio:["ignore","pipe","pipe"]});
  children.push(child);child.stdout.on("data",c=>output+=c);child.stderr.on("data",c=>output+=c);
  for(let i=0;i<100;i++){
    if(child.exitCode!==null)throw new Error("native server exited during startup");
    try{if((await fetch(`http://127.0.0.1:${port}/alive`)).ok)return child;}catch{}
    await new Promise(r=>setTimeout(r,25));
  }
  throw new Error("native server readiness timeout");
}
async function stop(child){if(child.exitCode===null){child.kill("SIGTERM");const [code]=await once(child,"exit");assert.equal(code,0);}}
async function main(){
  const admin=new Client({connectionString:server});await admin.connect();await admin.query(`CREATE DATABASE ${database}`);
  const mailPort=await listen(smtp);const otlpPort=await listen(collector);
  let db;
  try{
    execFileSync(binary,["--migrate"],{env:{...process.env,ARCHIVE_ENVIRONMENT:"Production","ConnectionStrings__archive-migrations":databaseUrl.href},stdio:"pipe"});
    db=new Client({connectionString:databaseUrl.href});await db.connect();
    await db.query(`INSERT INTO "AspNetUsers"("Id","DisplayName","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","SecurityStamp") VALUES($1,$2,$3,$4,$3,$4,true,false,false,true,0,'synthetic')`,[fixture.user.Id,fixture.user.DisplayName,fixture.user.Email,fixture.user.NormalizedEmail]);
    await db.query(`INSERT INTO "AspNetUserPasskeys"("CredentialId","UserId","Data") VALUES($1,$2,$3)`,[Buffer.from(fixture.credential.credentialId,"base64"),fixture.user.Id,fixture.data]);
    const firstPort=await port(),secondPort=await port();
    let first=await start(firstPort,mailPort,otlpPort);
    const second=await start(secondPort,mailPort,otlpPort);
    const cookies=new Map();let token="";
    async function call(port,route,body){
      const response=await fetch(`http://127.0.0.1:${port}${route}`,{method:body===undefined?"GET":"POST",headers:{cookie:[...cookies].map(([k,v])=>`${k}=${v}`).join("; "),"content-type":"application/json","X-CSRF-TOKEN":token},body:body===undefined?undefined:JSON.stringify(body)});
      for(const cookie of response.headers.getSetCookie()){const pair=cookie.split(";")[0];const index=pair.indexOf("=");cookies.set(pair.slice(0,index),pair.slice(index+1));}
      return {status:response.status,body:await response.json()};
    }
    token=(await call(firstPort,"/api/antiforgery")).body.token;
    assert.equal((await call(firstPort,"/api/dev/auth/seed",{})).status,200);
    assert.equal((await call(firstPort,"/api/auth/code/request",{email:"mitglied@liedertafel.test"})).status,202);
    assert.equal(messages.length,1);const code=/Ihr Anmeldecode lautet: (\d{6})/.exec(messages[0])?.[1];assert(code);
    assert.equal((await call(secondPort,"/api/auth/code/verify",{email:"mitglied@liedertafel.test",code})).status,200);
    const bearer=cookies.get("archive.auth");assert.equal(bearer.length,43);
    const before=(await call(firstPort,"/api/auth/me?private=redacted-query-marker")).body;assert.equal(before.authenticated,true);
    assert.equal((await call(secondPort,"/api/auth/me")).body.verifiedAt,before.verifiedAt);
    await stop(first);first=await start(firstPort,mailPort,otlpPort);
    assert.deepEqual((await call(firstPort,"/api/auth/me")).body,before);
    // Old .NET credential, new Rust challenge, complete real ES256 signature.
    // Repeat after another actual native restart (not an in-memory mock).
    for(const counter of [2,3]){
      token=(await call(firstPort,"/api/antiforgery")).body.token;
      const options=JSON.parse((await call(firstPort,"/api/auth/passkeys/login/options",{})).body.requestOptions);
      const clientData=Buffer.from(JSON.stringify({type:"webauthn.get",challenge:options.challenge,origin:"http://localhost",crossOrigin:false}));
      const authData=Buffer.concat([createHash("sha256").update("localhost").digest(),Buffer.from([5,0,0,0,counter])]);
      const signed=Buffer.concat([authData,createHash("sha256").update(clientData).digest()]);
      const key=createPrivateKey({key:Buffer.from(fixture.credential.privateKey,"base64"),type:"pkcs8",format:"der"});
      const id=Buffer.from(fixture.credential.credentialId,"base64").toString("base64url");
      const credential={id,rawId:id,type:"public-key",response:{authenticatorData:authData.toString("base64url"),clientDataJSON:clientData.toString("base64url"),signature:sign("sha256",signed,key).toString("base64url"),userHandle:Buffer.from(fixture.user.Id).toString("base64url")},clientExtensionResults:{}};
      assert.equal((await call(secondPort,"/api/auth/passkeys/login/verify",{credential:JSON.stringify(credential)})).status,200);
      const passkeyMe=(await call(firstPort,"/api/auth/me")).body;assert.equal(passkeyMe.authMethod,"passkey");assert.equal(passkeyMe.accountId,fixture.user.Id);
      await stop(first);first=await start(firstPort,mailPort,otlpPort);
      assert.deepEqual((await call(firstPort,"/api/auth/me")).body,passkeyMe);
    }
    const saved=(await db.query(`SELECT "Data" FROM "AspNetUserPasskeys" WHERE "UserId"=$1`,[fixture.user.Id])).rows[0].Data;
    assert.equal(saved.SignCount,3);
    for(const field of ["PublicKey","Name","CreatedAt","AttestationObject","ClientDataJson"])assert.deepEqual(saved[field],fixture.data[field]);
    await db.end();db=undefined;
    // Stale anonymous CSRF pair must fail before logout; then a shared fresh
    // token works on either replica and invalidates the original token globally.
    assert.equal((await call(firstPort,"/api/auth/logout",{})).status,400);
    token=(await call(secondPort,"/api/antiforgery")).body.token;
    assert.equal((await call(firstPort,"/api/auth/logout",{})).status,200);
    cookies.set("archive.auth",bearer);
    assert.equal((await call(secondPort,"/api/auth/me")).body.authenticated,false);
    cookies.delete("archive.auth");token=(await call(firstPort,"/api/antiforgery")).body.token;
    assert.equal((await call(firstPort,"/api/auth/code/verify",{email:"unknown@liedertafel.test",code:"654321"})).status,400);
    // Discard cookies, alternate replicas, vary the untrusted leftmost hop.
    // Both allocation routes share one effective-address quota before inserts.
    const allocationAddress="198.51.100.221";
    for(let i=0;i<=30;i++){
      const port=i%2?firstPort:secondPort;
      const route=i%2?"/api/antiforgery":"/api/auth/passkeys/login/options";
      const response=await fetch(`http://127.0.0.1:${port}${route}`,{method:i%2?"GET":"POST",headers:{"x-forwarded-for":`198.18.0.${i}, ${allocationAddress}`}});
      assert.equal(response.status,i<30?200:429);
    }
    await stop(first);await stop(second);
    const traces=Object.entries(signals).filter(([k])=>k.includes("TraceService")).map(([,v])=>v.toString()).join("");
    const metrics=Object.entries(signals).filter(([k])=>k.includes("MetricsService")).map(([,v])=>v.toString()).join("");
    const logs=Object.entries(signals).filter(([k])=>k.includes("LogsService")).map(([,v])=>v.toString()).join("");
    for(const span of ["archive.auth.request","archive.auth.challenge","archive.auth.verify","archive.mail.send"])assert(traces.includes(span),`missing ${span}`);
    for(const metric of ["archive.auth.requests","archive.auth.verified","archive.auth.failures","archive.auth.allocations_limited"])assert(metrics.includes(metric),`missing ${metric}`);
    assert(logs.includes("Sign-in code verified"),"safe auth log exported");
    const all=traces+metrics+logs+output;
    for(const secret of [code,bearer,"mitglied@liedertafel.test","unknown@liedertafel.test","redacted-query-marker",fixture.credential.credentialId,fixture.data.PublicKey,allocationAddress,createHash("sha256").update(allocationAddress).digest("hex")])assert(!all.includes(secret),"auth telemetry leaked secret marker");
    console.log("PASS: native email login, .NET ES256 old-row fresh-challenge login repeated after native restart, atomic counter/JSON persistence, cross-replica CSRF/session, bounded anonymous allocation, server-side logout, real SMTP handoff, gRPC OTLP traces/metrics/logs and redaction under RUST_LOG=trace");
  }finally{
    if(db)await db.end();
    for(const child of children)await stop(child);
    smtp.close();collector.close();
    await admin.query(`DROP DATABASE ${database} WITH (FORCE)`);await admin.end();
  }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
