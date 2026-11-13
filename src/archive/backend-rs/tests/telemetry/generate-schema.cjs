// Maintenance-only TEST fixture generation; never invoked by a server/build/test.
// Source and license are pinned to the official OpenTelemetry proto commit.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { createHash } = require("node:crypto");
const protobuf = require("protobufjs");
assert.equal(process.argv[2], "--refresh", "explicit --refresh required");
const commit = "a8951735f7801e8adfaec5c0ace9262771cfec6e"; // v1.9.0
const files = ["common/v1/common.proto", "resource/v1/resource.proto", "trace/v1/trace.proto", "metrics/v1/metrics.proto", "logs/v1/logs.proto", "collector/trace/v1/trace_service.proto", "collector/metrics/v1/metrics_service.proto", "collector/logs/v1/logs_service.proto"].map(p => `opentelemetry/proto/${p}`);
const temporary = fs.mkdtempSync("/tmp/opencode/223-otlp-schema-");
async function main() {
  const hashes = {};
  for (const file of [...files, "LICENSE"]) {
    const response = await fetch(`https://raw.githubusercontent.com/open-telemetry/opentelemetry-proto/${commit}/${file}`);
    assert(response.ok, `official source unavailable: ${file}`);
    const bytes = Buffer.from(await response.arrayBuffer());
    hashes[file] = createHash("sha256").update(bytes).digest("hex");
    const target = path.join(temporary, file);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, bytes);
  }
  const root = new protobuf.Root();
  root.resolvePath = (_, target) => path.join(temporary, target);
  root.loadSync(files).resolveAll();
  fs.writeFileSync(path.join(__dirname, "official-schema.json"), JSON.stringify(root.toJSON()) + "\n");
  fs.copyFileSync(path.join(temporary, "LICENSE"), path.join(__dirname, "LICENSE"));
  fs.writeFileSync(path.join(__dirname, "provenance.json"), JSON.stringify({ repository: "https://github.com/open-telemetry/opentelemetry-proto", tag: "v1.9.0", commit, protobufjs: require("protobufjs/package.json").version, files: hashes }, null, 2) + "\n");
  console.log("Generated pinned official test-only OTLP reflection descriptor; no production file changed.");
}
main().catch(error => { console.error(error); process.exitCode = 1; });
