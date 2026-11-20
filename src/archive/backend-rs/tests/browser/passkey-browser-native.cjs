// Two real browser ceremonies against disposable SQL and loopback Mailpit.
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { randomUUID } = require("node:crypto");
const { once } = require("node:events");
const http = require("node:http");
const net = require("node:net");
const path = require("node:path");
const { Client } = require("pg");

const server = new URL(process.env.ARCHIVE_TEST_POSTGRES);
assert.equal(server.hostname, "127.0.0.1");
const mailPort = process.env.ARCHIVE_TEST_MAILPIT_HTTP_PORT;
const smtpPort = process.env.ARCHIVE_TEST_MAILPIT_SMTP_PORT;
assert(/^\d+$/.test(mailPort) && /^\d+$/.test(smtpPort));
const binary = process.env.ARCHIVE_BINARY;

async function listen(listener) {
  listener.listen(0, "127.0.0.1");
  await once(listener, "listening");
  return listener.address().port;
}

async function main() {
  const admin = new Client({ connectionString: server.href });
  await admin.connect();
  // This is a browser gate; the native auth and image gates inspect telemetry.
  const collector = http.createServer((request, response) => {
    request.resume();
    request.on("end", () => response.writeHead(200, {
      "Content-Type": "application/x-protobuf",
    }).end());
  });
  const otlp = await listen(collector);
  try {
    for (const mode of ["enroll", "legacy"]) {
      const db = `passkey_ci_${randomUUID().replaceAll("-", "")}`;
      await admin.query(`CREATE DATABASE ${db}`);
      const database = new URL(`/${db}`, server);
      const reserve = net.createServer();
      const port = await listen(reserve);
      await new Promise((resolve) => reserve.close(resolve));
      const base = `http://localhost:${port}`;
      const env = {
        ...process.env,
        PORT: String(port),
        ARCHIVE_ENVIRONMENT: "Development",
        OTEL_EXPORTER_OTLP_ENDPOINT: `http://127.0.0.1:${otlp}`,
        OTEL_EXPORTER_OTLP_PROTOCOL: "http/protobuf",
        "ConnectionStrings__archive-migrations": database.href,
        "ConnectionStrings__archive-db": database.href,
        Mail__Provider: "Smtp", Mail__Host: "127.0.0.1", Mail__Port: smtpPort,
        Authentication__PasskeyRelyingPartyId: "localhost",
        Authentication__PasskeyOrigins__0: base,
        ARCHIVE_BASE_URL: base,
        ARCHIVE_MAIL_URL: `http://127.0.0.1:${mailPort}`,
        ARCHIVE_AUTH_DATABASE: database.href,
      };
      let origin;
      try {
        const migrate = spawn(binary, ["--migrate"], { env, stdio: "ignore" });
        assert.deepEqual(await once(migrate, "close"), [0, null], "passkey DB migration");
        origin = spawn(binary, [], { env, stdio: "ignore" });
        const closed = once(origin, "close");
        try {
          for (let attempt = 0; ; attempt++) {
            assert(origin.exitCode === null && attempt < 200, "passkey origin readiness");
            if (await fetch(`${base}/alive`).then((r) => r.ok, () => false)) break;
            await new Promise((resolve) => setTimeout(resolve, 25));
          }
          const journey = spawn(process.execPath, [path.join(__dirname, "ceremonies.cjs"), mode], {
            env, stdio: "inherit",
          });
          assert.deepEqual(await once(journey, "close"), [0, null], `passkey ${mode}`);
        } finally {
          origin.kill("SIGTERM");
          assert.deepEqual(await closed, [0, null], "passkey origin shutdown");
        }
      } finally {
        await admin.query(`DROP DATABASE ${db} WITH (FORCE)`);
      }
    }
  } finally {
    await new Promise((resolve) => collector.close(resolve));
    await admin.end();
  }
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
