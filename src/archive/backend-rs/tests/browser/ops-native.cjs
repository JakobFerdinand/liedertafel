// #234 product binary only. Start with fresh, dedicated loopback emulators.
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { randomUUID } = require("node:crypto");
const { once } = require("node:events");
const http2 = require("node:http2");
const net = require("node:net");
const { Client } = require("pg");
const blob = require("@azure/storage-blob");
const queue = require("@azure/storage-queue");
const { channels } = require("./telemetry-redaction.cjs");
const capture = require("./telemetry-capture.cjs").createCapture("ops-native");

const binary = process.env.ARCHIVE_BINARY;
const pgPort = process.env.ARCHIVE_234_PG_PORT;
const blobPort = process.env.ARCHIVE_234_BLOB_PORT;
const queuePort = process.env.ARCHIVE_234_QUEUE_PORT;
const smtpPort = process.env.ARCHIVE_234_SMTP_PORT;
const mailPort = process.env.ARCHIVE_234_MAIL_PORT;
for (const port of [pgPort, blobPort, queuePort, smtpPort, mailPort]) {
  assert(/^[0-9]{4,5}$/.test(port), "explicit isolated local ports required");
}
assert(binary && process.env.ARCHIVE_234_PG_PASSWORD);
const dbUrl = new URL(`postgres://postgres@127.0.0.1:${pgPort}/postgres?sslmode=disable`);
dbUrl.password = process.env.ARCHIVE_234_PG_PASSWORD;
const admin = new Client({ connectionString: dbUrl.href });
const dbName = `ops234_${randomUUID().replaceAll("-", "")}`;
const account = "devstoreaccount1";
// Azurite's documented public emulator key, NOT a hosted credential.
const key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
const blobUrl = `http://127.0.0.1:${blobPort}/${account}`;
const queueUrl = `http://127.0.0.1:${queuePort}/${account}`;
const connection = `AccountName=${account};AccountKey=${key};BlobEndpoint=${blobUrl};QueueEndpoint=${queueUrl};`;
function pipeline(sdk) {
  const result = sdk.newPipeline(new sdk.StorageSharedKeyCredential(account, key));
  result.factories.push({create(next) {
    return { sendRequest(request) {
      request.headers.set("x-ms-version", "2025-11-05");
      return next.sendRequest(request);
    }};
  }});
  return result;
}
const blobService = new blob.BlobServiceClient(blobUrl, pipeline(blob));
const blobs = blobService.getContainerClient("archive-assets");
const queueService = new queue.QueueServiceClient(queueUrl, pipeline(queue));
const queueNames = ["archive-extraction-234", "archive-reading-234", "archive-videos-234"];
const [extraction, reading, videos] = queueNames.map(name => queueService.getQueueClient(name));
const signals = [];
let failSignal;
const collector = capture.collector(http2.createServer());
collector.on("stream", (stream, headers) => {
  const parts = [];
  stream.on("data", data => parts.push(data));
  stream.on("end", () => {
    const route = headers[":path"];
    const body = Buffer.concat(parts);
    capture.signal(route, body);
    signals.push({ route, body });
    stream.respond({":status":200,"content-type":"application/grpc"}, {waitForTrailers:true});
    stream.on("wantTrailers", () => stream.sendTrailers({
      "grpc-status": channels[route]?.channel === failSignal ? "3" : "0",
    }));
    stream.end(Buffer.from([0, 0, 0, 0, 0]));
  });
});
function decode(entries, channel) {
  return entries.filter(({route}) => channels[route]?.channel === channel).flatMap(({route,body}) => {
    const results = [];
    for (let i = 0; i < body.length;) {
      const n = body.readUInt32BE(i + 1);
      assert.equal(body[i], 0);
      results.push(channels[route].type.decode(body.subarray(i + 5, i + n + 5)));
      i += n + 5;
    }
    return results;
  });
}
const spans = entries => decode(entries, "trace").flatMap(m => m.resourceSpans)
  .flatMap(m => m.scopeSpans).flatMap(m => m.spans);
const logs = entries => decode(entries, "logs").flatMap(m => m.resourceLogs)
  .flatMap(m => m.scopeLogs).flatMap(m => m.logRecords.map(record => ({
    category:m.scope.name, text:record.body?.stringValue ?? "",
  })));
async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}
async function names(service, method) {
  const result = [];
  for await (const item of service[method]()) result.push(item.name);
  return result.sort();
}
let origin, currentEnv, base;
async function http(path, init) {
  return fetch(base + path, { signal: AbortSignal.timeout(45_000), ...init });
}
async function startOrigin(extra = {}) {
  const reserve = net.createServer();
  const port = await listen(reserve);
  await new Promise(resolve => reserve.close(resolve));
  base = `http://127.0.0.1:${port}`;
  origin = spawn(binary, [], { env: { ...currentEnv, PORT: String(port), ...extra } });
  capture.watch(origin, "origin");
  for (let i = 0; i < 200; i++) {
    assert.equal(origin.exitCode, null, "origin exited before readiness");
    if (await http("/alive").then(r => r.ok, () => false)) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  assert.fail("origin never became ready");
}
async function stopOrigin() {
  if (!origin) return;
  if (origin.exitCode === null) origin.kill("SIGTERM");
  const [code, signal] = await capture.closed(origin);
  origin = undefined;
  assert.deepEqual([code, signal], [0, null]);
}
async function command(args, extra, { code = 0, bodySucceeded = code === 0, message, category, cancelWhen } = {}) {
  const before = signals.length;
  const child = spawn(binary, args, { env: { ...currentEnv, PORT: "not-a-web-command", ...extra } });
  capture.watch(child, args[0].slice(2), code);
  const timer = setTimeout(() => child.kill("SIGKILL"), 45_000);
  let exit;
  try {
    if (cancelWhen) {
      assert.equal(await Promise.race([cancelWhen.then(() => true), capture.closed(child).then(() => false)]), true);
      child.kill("SIGTERM");
    }
    exit = await capture.closed(child);
  } finally { clearTimeout(timer); }
  assert.deepEqual(exit, [code, null], args[0]);
  const entries = signals.slice(before);
  const job = args[0].slice(2);
  const trace = spans(entries).find(span => span.name === job);
  assert(trace, `missing finite span: ${job}`);
  assert.equal(trace.status?.code === 2, !bodySucceeded, job);
  const emitted = logs(entries);
  assert(emitted.some(s => s.category === "Archive.Jobs"
    && s.text === `Archive job ${job} ${bodySucceeded ? "completed" : "failed"}`), job);
  assert(decode(entries, "metrics").flatMap(m => m.resourceMetrics)
    .flatMap(m => m.scopeMetrics).flatMap(m => m.metrics).some(m => m.name === "archive.jobs.runs"), job);
  if (message) assert(emitted.some(s => s.text.includes(message) && (!category || s.category === category)), message);
  return entries;
}

async function main() {
  capture.marker("credential", "pg-password", process.env.ARCHIVE_234_PG_PASSWORD);
  capture.marker("key", "public-emulator-key", key);
  const endpoint = `http://127.0.0.1:${await listen(collector)}`;
  await admin.connect();
  await admin.query(`CREATE DATABASE ${dbName}`);
  dbUrl.pathname = dbName;
  const db = new Client({ connectionString: dbUrl.href });
  await db.connect();
  currentEnv = {
    ARCHIVE_ENVIRONMENT: "Development", RUST_LOG: "info", OTEL_EXPORTER_OTLP_ENDPOINT: endpoint,
    OTEL_EXPORTER_OTLP_TIMEOUT: "3000",
    "ConnectionStrings__archive-db": dbUrl.href, "ConnectionStrings__archive-blobs": connection,
    "ConnectionStrings__archive-queues": connection, Mail__Host: "127.0.0.1", Mail__Port: smtpPort,
    Archive__Extraction__QueueName: queueNames[0], Archive__Reading__QueueName: queueNames[1],
    Archive__Video__QueueName: queueNames[2],
  };
  const paused = {Archive__MaintenanceMode:" TrUe "};
  const missingDependencies = {
    "ConnectionStrings__archive-db":"not-a-connection",
    "ConnectionStrings__archive-blobs":"not-a-connection",
    "ConnectionStrings__archive-queues":"not-a-connection",
    Archive__Chat__Endpoint:"not-an-azure-endpoint",
  };
  const put = async name => {
    capture.marker("object", "stored-blob", name);
    await blobs.getBlockBlobClient(name).upload("fixture", 7);
  };
  const objects = () => names(blobs, "listBlobsFlat");
  const queues = () => names(queueService, "listQueues");
  try {
    assert.equal(await blobs.exists(), false, "use a fresh isolated Azurite instance");
    assert.deepEqual(await queues(), []);
    for (const [cmd, [role, category]] of Object.entries({
      "--extract-queue":["Extraction worker","Archive.Extraction"],
      "--dispatch-extraction":["Extraction dispatch","Archive.Extraction"],
      "--read-queue":["Reading worker","Archive.Reading"], "--dispatch-reading":["Reading dispatch","Archive.Reading"],
      "--transcode-queue":["Video worker","Archive.Videos"], "--dispatch-videos":["Video dispatch","Archive.Videos"],
      "--cleanup-papierkorb":["Papierkorb cleanup","Archive.Papierkorb"],
    })) {
      await command([cmd], {...paused, ...missingDependencies}, {message:`${role} paused: maintenance mode is enabled`,category});
      if (cmd !== "--cleanup-papierkorb")
        await command([cmd], {...paused,...missingDependencies,ARCHIVE_ENVIRONMENT:"Production"},
          {message:`${role} paused: maintenance mode is enabled`,category});
      await command([cmd], {Archive__MaintenanceMode:"", ...missingDependencies}, {code:1});
    }
    for (const cmd of ["--initialize-local-storage","--worker-smoke","--cleanup-uploads","--cleanup-papierkorb","--send-test-mail","--seed-dev-auth"])
      await command([cmd], {...paused,...missingDependencies,ARCHIVE_ENVIRONMENT:"Production"}, {code:1});
    assert.deepEqual(await queues(), []);
    assert.equal((await db.query("SELECT to_regclass('public._sqlx_migrations') table_name")).rows[0].table_name, null);
    await command(["--initialize-local-storage"], {...paused,...missingDependencies}, {code:1});
    await command(["--initialize-local-storage"], paused);
    await command(["--initialize-local-storage"]);
    assert.deepEqual(await queues(), [...queueNames].sort());
    assert.equal(await blobs.exists(), true);
    assert.equal((await blobs.getProperties()).blobPublicAccess, undefined);
    assert.deepEqual((await blobService.getProperties()).cors, [{
      allowedOrigins:"*", allowedMethods:"GET,HEAD,PUT,OPTIONS", allowedHeaders:"*",
      exposedHeaders:"*",maxAgeInSeconds:3600,
    }]);
    const custom = [{
      allowedOrigins:"http://127.0.0.1:12345",allowedMethods:"PUT",allowedHeaders:"*",exposedHeaders:"*",
      maxAgeInSeconds:42,
    }];
    await blobService.setProperties({cors:custom});
    await command(["--initialize-local-storage"]);
    assert.deepEqual((await blobService.getProperties()).cors, custom);
    await extraction.sendMessage("reserved-for-extraction");
    await reading.sendMessage("reserved-for-reading");
    await videos.sendMessage("reserved-for-videos");
    const exerciseSpans = async (entries, traceId, traceState) => {
      const values = spans(entries);
      const producer = values.find(s => s.name === "archive.queue.send" && s.kind === 4);
      const consumer = values.find(s => s.name === "archive.queue.process" && s.kind === 5);
      assert(producer && consumer, "queue Producer and Consumer spans");
      assert.deepEqual(producer.traceId, consumer.traceId);
      assert.deepEqual(producer.spanId, consumer.parentSpanId);
      if (traceId) {
        assert.equal(Buffer.from(consumer.traceId).toString("hex"),traceId);
        assert.equal(consumer.traceState,traceState);
      }
      assert(values.some(s => s.name === "archive.mail.send" && s.kind === 3));
      assert.deepEqual(await objects(), []);
      assert.deepEqual(await queues(), [...queueNames].sort());
      for (const q of [extraction, reading, videos]) assert.equal((await q.peekMessages()).peekedMessageItems.length, 1);
    };
    await exerciseSpans(await command(["--worker-smoke"], paused));
    await command(["--worker-smoke"], {Mail__Port:"1"}, {code:1});
    assert.deepEqual(await objects(), []);
    assert.deepEqual(await queues(), [...queueNames].sort());
    const sockets = new Set();
    const held = net.createServer(socket => {
      sockets.add(socket);
      socket.on("close", () => sockets.delete(socket));
      // Hold the greeting, so the CLI has already crossed Blob + Queue
      // and owns both scratch resources when the signal arrives.
    });
    const heldPort = await listen(held);
    try {
      await command(["--worker-smoke"],{Mail__Port:String(heldPort)},{
        code:1,
        cancelWhen:once(held,"connection").then(async () => {
          const [scratchBlob] = await objects();
          const scratchQueue = (await queues()).find(name => name.startsWith("archive-smoke-"));
          assert.match(scratchBlob,/^diagnostics\/[a-f0-9]{32}\.txt$/);
          assert.match(scratchQueue,/^archive-smoke-[a-f0-9]{32}$/);
          capture.marker("object","diagnostic-scratch-blob",scratchBlob);
          capture.marker("object","diagnostic-scratch-queue",scratchQueue);
          capture.marker("object","diagnostic-payload",scratchQueue.slice("archive-smoke-".length));
        }),
      });
      assert.deepEqual(await objects(), []);
      assert.deepEqual(await queues(), [...queueNames].sort());
    } finally {
      for (const socket of sockets) socket.destroy();
      await new Promise(resolve => held.close(resolve));
    }

    await startOrigin();
    const fresh = await http("/api/dev/database").then(r => r.json());
    assert.deepEqual(fresh, { connected:true, pendingMigrations:[
      "0001_frozen_ef_baseline","0002_auth_sessions","0003_auth_allocation_budgets","0004_culture_comparison",
    ]});
    assert.equal((await db.query("SELECT to_regclass('public._sqlx_migrations') table_name")).rows[0].table_name, null);
    await command(["--migrate"], paused);
    assert.deepEqual(await http("/api/dev/database").then(r => r.json()), {connected:true,pendingMigrations:[]});

    const csrf = await http("/api/antiforgery");
    const cookie = csrf.headers.get("set-cookie").split(";")[0];
    const {token} = await csrf.json();
    capture.marker("credential", "diagnostic-csrf", token);
    capture.marker("cookie", "diagnostic-cookie", cookie.split("=")[1]);
    const valid = {cookie, "x-csrf-token":token};
    const post = (path, headers) => http(path, {method:"POST", headers});
    for (const headers of [{}, {cookie}, {"x-csrf-token":token}, {cookie, "x-csrf-token":"A".repeat(43)}]) {
      for (const path of ["/api/dev/exercise", "/api/dev/extraction-duplicate"]) {
        const response = await post(path, headers);
        assert.equal(response.status, 400);
        assert.equal(response.headers.get("cache-control"), null);
        assert.equal((await response.json()).title, "Ungültiger Sicherheitstoken.");
      }
    }
    const before = signals.length;
    const traceId = "ad10cba9887744559911223366338855", traceState = "fixture=ops234";
    const exercise = await post("/api/dev/exercise", {...valid,
      traceparent:`00-${traceId}-1122334455667788-01`,tracestate:traceState,
    });
    assert.equal(exercise.status, 200);
    assert.deepEqual(await exercise.json(), {message:"Blob und Warteschlange geprüft. Testmail wurde gesendet."});
    const mailbox = await fetch(`http://127.0.0.1:${mailPort}/api/v1/messages`).then(r=>r.json());
    assert.equal(mailbox.messages.length, 2, "one SMTP message from each successful exercise");
    for (const mail of mailbox.messages) assert.equal(mail.Subject,"Archiv: lokaler Funktionstest");
    // Remove only the synthetic sentinel from our isolated queue.
    const sentinel = (await extraction.receiveMessages()).receivedMessageItems[0];
    assert.equal(sentinel.messageText, "reserved-for-extraction");
    await extraction.deleteMessage(sentinel.messageId, sentinel.popReceipt);
    for (const suffix of ["", "?dateiId=00000000-0000-0000-0000-000000000000"]) {
      const response = await post("/api/dev/extraction-duplicate" + suffix, valid);
      assert.equal(response.status, 400);
      assert.equal((await response.json()).title, "Es wurde keine Datei angegeben.");
    }
    const dateiId = randomUUID();
    const sent = await post(`/api/dev/extraction-duplicate?dateiId=${dateiId}`, valid);
    assert.equal(sent.status, 200);
    assert.deepEqual(await sent.json(), {sent:true});
    const duplicate = (await extraction.receiveMessages()).receivedMessageItems[0];
    const envelope = JSON.parse(duplicate.messageText);
    assert.equal(envelope.type, "extraction");
    assert.equal(envelope.dateiId, dateiId);
    assert.match(envelope.traceParent, /^00-[a-f0-9]{32}-[a-f0-9]{16}-01$/);
    await extraction.deleteMessage(duplicate.messageId, duplicate.popReceipt);
    await extraction.sendMessage("reserved-for-extraction");
    const budget = () => http("/api/dev/ai-budget").then(r => r.json());
    const empty = await budget();
    assert.match(empty.yearMonth,/^[0-9-]+$/);
    assert.deepEqual(empty,{yearMonth:empty.yearMonth,spentMicroEur:0,capMicroEur:15000000,entries:0,calls:0,costMicroEur:0,reservedMicroEur:0,monthRows:0});
    await db.query(`INSERT INTO ai_budget_months ("YearMonth","NotifiedMicroEur") VALUES ($1,0)`,[empty.yearMonth]);
    await db.query(`INSERT INTO ai_usage_entries ("Id","OperationId","YearMonth","Feature","Model","Calls","CostMicroEur","ReservedMicroEur","CreatedAt","UpdatedAt","InputTokens","OutputTokens") VALUES ($2,$3,$1,'ops','scripted',2,42,7,now(),now(),15,9)`,[empty.yearMonth,randomUUID(),randomUUID()]);
    assert.deepEqual(await budget(),{...empty,spentMicroEur:49,entries:1,calls:2,costMicroEur:42,reservedMicroEur:7,monthRows:1});
    await stopOrigin();
    await exerciseSpans(signals.slice(before),traceId,traceState);

    await startOrigin(paused);
    for (const [path, method] of [["database","GET"],["ai-budget","GET"],["exercise","POST"],["extraction-duplicate","POST"]]) {
      const blocked = await http(`/api/dev/${path}`, {method});
      assert.equal(blocked.status,503);
      assert.equal(blocked.headers.get("retry-after"),"600");
    }
    await stopOrigin();
    await startOrigin({"ConnectionStrings__archive-queues":""});
    const csrf2 = await http("/api/antiforgery");
    const second = {cookie:csrf2.headers.get("set-cookie").split(";")[0], token:(await csrf2.json()).token};
    capture.marker("credential","diagnostic-csrf",second.token);
    capture.marker("cookie","diagnostic-cookie",second.cookie.split("=")[1]);
    const form = `__requestverificationtoken=${second.token}`;
    const multipart = new FormData();
    multipart.set("__RequestVerificationToken",second.token);
    for (const [status, headers, body] of [
      [502,{"x-csrf-token":second.token},"ignored"],
      [502,{"content-type":"application/x-www-form-urlencoded"},form],
      [502,{"content-type":"application/x-www-form-urlencoded"},`__RequestVerificationToken=&${form}`],
      [502,{},multipart],
      [400,{"x-csrf-token":"","content-type":"application/x-www-form-urlencoded"},form],
      [400,{"content-type":"application/x-www-form-urlencoded"},`${form}&${form}`],
    ]) {
      const response = await http(`/api/dev/extraction-duplicate?dateiId=${dateiId}`, {
        method:"POST",headers:{cookie:second.cookie,...headers},body,
      });
      assert.equal(response.status,status);
      assert.equal((await response.json()).title,status===502 ?
        "Kein Warteschlangen-Endpunkt für die Textauswertung konfiguriert." : "Ungültiger Sicherheitstoken.");
    }
    await stopOrigin();

    const actor = randomUUID(), live = randomUUID(), old = randomUUID(), event = randomUUID();
    async function lied(id, deleted) {
      await db.query(`INSERT INTO lieder ("Id","Title","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion","DeletedAt")
        VALUES ($1,'Fixture',now(),$2,now(),$2,1,$3)`,[id,actor,deleted]);
    }
    const ago = days => new Date(Date.now() - days * 86_400_000);
    await lied(live,null);
    await lied(old,ago(8));
    await db.query(`INSERT INTO events ("Id","Kind","Title","DateApproximate","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion","DeletedAt")
      VALUES ($1,'konzert','Fixture',false,now(),$2,now(),$2,1,$3)`,[event,actor,ago(8)]);
    for (const [name, state, hours] of [
      ["expired",0,2],["fresh",0,0.5],["finalized",1,2],["abandoned",2,2],["cancelled",3,2],
    ]) {
      await put(`pending/${name}`);
      await db.query(`INSERT INTO upload_sessions ("Id","BlobName","ContentType","MaxSizeBytes","UploadTicketExpiresAt","State","CreatedByAccountId","CreatedAt","LiedId")
        VALUES ($1,$2,'application/pdf',100,$3,$4,$5,now(),$6)`,
        [randomUUID(),`pending/${name}`,ago(hours/24),state,actor,live]);
    }
    for (const name of ["revisions/old","pending/orphan","pending/Fresh","dateien/live","unrelated/kept"]) await put(name);
    const all = await objects();
    await command(["--cleanup-dropped-blobs","--dry-run"], {...paused, ARCHIVE_ENVIRONMENT:"Production"},
      {message:"1 revision blobs, 2 orphaned staged uploads, 6 objects of the flat model kept"});
    assert.deepEqual(await objects(),all);
    await command(["--cleanup-dropped-blobs"], {...paused,ARCHIVE_ENVIRONMENT:"Production"},
      {message:"1 revision blobs, 2 orphaned staged uploads, 6 objects of the flat model kept"});
    assert.deepEqual(await objects(),all.filter(name => !["revisions/old","pending/orphan","pending/Fresh"].includes(name)));
    await command(["--cleanup-dropped-blobs"], paused,{message:"0 revision blobs, 0 orphaned staged uploads, 6 objects of the flat model kept"});
    await command(["--cleanup-uploads"], paused,{message:"Cleanup marked 1 upload sessions abandoned"});
    const states = (await db.query(`SELECT "BlobName","State" FROM upload_sessions ORDER BY "BlobName"`)).rows;
    assert.deepEqual(states.map(row=>row.State),[2,3,2,1,0]);
    assert(!(await objects()).includes("pending/expired"));
    await command(["--cleanup-uploads"], paused,{message:"Cleanup marked 0 upload sessions abandoned"});
    await put("pending/unavailable");
    await db.query(`INSERT INTO upload_sessions ("Id","BlobName","ContentType","MaxSizeBytes","UploadTicketExpiresAt","State","CreatedByAccountId","CreatedAt","LiedId")
      VALUES ($1,'pending/unavailable','application/pdf',100,$2,0,$3,now(),$4)`,
      [randomUUID(),ago(2/24),actor,live]);
    await command(["--cleanup-uploads"], {
      ...paused, "ConnectionStrings__archive-blobs":connection.replace(blobUrl, "http://127.0.0.1:1/devstoreaccount1"),
    },{message:"Pending upload blob could not be deleted during cleanup",category:"Archive.Jobs"});
    assert.equal((await db.query(`SELECT "State" FROM upload_sessions WHERE "BlobName"='pending/unavailable'`)).rows[0].State,2);
    assert((await objects()).includes("pending/unavailable"));

    async function datei(name, owner, days) {
      const id = randomUUID();
      const blobName = `dateien/${name}`;
      if (name !== "live") await put(blobName);
      await db.query(`INSERT INTO dateien ("Id","LiedId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","CreatedByAccountId","CreatedAt","RowVersion","DeletedAt")
        VALUES ($1,$2,'noten',false,$3,'application/pdf',7,$4,now(),1,$5)`,
        [id,owner,blobName,actor,days === null ? null : ago(days)]);
      return id;
    }
    await datei("live",live,null);
    await datei("trash",live,8);
    await datei("recent",live,6);
    await datei("child",old,null);
    const beforeCleanup = await objects();
    await command(["--cleanup-papierkorb","--dry-run"], {},{message:"1 Lieder, 1 Auftritte, 1 Dateien past 7 days"});
    assert.deepEqual(await objects(),beforeCleanup);
    await command(["--cleanup-papierkorb"], {},{message:"1 Lieder, 1 Auftritte, 1 Dateien past 7 days"});
    assert.deepEqual(await objects(),beforeCleanup.filter(name=>!["dateien/trash","dateien/child"].includes(name)));
    assert.deepEqual((await db.query(`SELECT "Id" FROM lieder ORDER BY "Id"`)).rows.map(r=>r.Id),[live]);
    assert.equal((await db.query("SELECT count(*) FROM events")).rows[0].count,"0");
    assert.deepEqual((await db.query(`SELECT "BlobName" FROM dateien ORDER BY "BlobName"`)).rows.map(r=>r.BlobName),
      ["dateien/live","dateien/recent"]);
    await command(["--cleanup-papierkorb"], {},{message:"0 Lieder, 0 Auftritte, 0 Dateien past 7 days"});
    await command(["--cleanup-papierkorb"], {Archive__Papierkorb__RetentionDays:"5"},
      {message:"0 Lieder, 0 Auftritte, 1 Dateien past 5 days"});
    assert(!(await objects()).includes("dateien/recent"));
    const blocked = await datei("restrict",live,8);
    await db.query(`INSERT INTO video_jobs ("DateiId","Status","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,0,0,$2,now(),now(),1)`,[blocked,actor]);
    await command(["--cleanup-papierkorb"], {},{code:1});
    assert((await objects()).includes("dateien/restrict"));
    assert.equal((await db.query(`SELECT "Id" FROM dateien WHERE "Id"=$1`,[blocked])).rowCount,1);
    assert.equal((await db.query(`SELECT "DateiId" FROM video_jobs WHERE "DateiId"=$1`,[blocked])).rowCount,1);
    assert((await objects()).includes("dateien/live"));

    for (const channel of ["trace","metrics","logs"]) {
      failSignal = channel;
      await command(["--dispatch-extraction"], paused, {code:1,bodySucceeded:true});
    }
    failSignal = undefined;
    await capture.drain();
    capture.assertClean();
    console.log("PASS: private PG/Azurite/Mailpit Development diagnostics, finite cleanup and pause matrix, scoped scratch and gRPC OTLP success/failure");
  } finally {
    await stopOrigin();
    await capture.drain();
    await db.end();
    await admin.query(`DROP DATABASE ${dbName} WITH (FORCE)`);
    await admin.end();
  }
}
main().catch(error => {
  console.error(error.stack);
  process.exitCode = 1;
});
