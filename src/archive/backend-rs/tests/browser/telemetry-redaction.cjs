// Test-only, schema-aware native collector inspection. Never used by the origin.
const protobuf = require("protobufjs");
const { TextDecoder } = require("node:util");
const root = protobuf.Root.fromJSON(require("../telemetry/official-schema.json")).resolveAll();
const channels = Object.fromEntries(["trace", "metrics", "logs"].map(kind => {
  const singular = kind === "metrics" ? "metrics" : kind;
  const service = kind[0].toUpperCase() + kind.slice(1);
  return [`/opentelemetry.proto.collector.${singular}.v1.${service}Service/Export`, {
    channel: kind, type: root.lookupType(`opentelemetry.proto.collector.${singular}.v1.Export${service}ServiceRequest`),
  }];
}));
const utf8 = new TextDecoder("utf-8", { fatal: true });
// Only official fixed64 clock fields are protocol metadata, never AnyValue or
// metric values/attributes. IDs remain bytes and are inspected as bytes.
const clocks = new Set([
  "trace.v1.Span.startTimeUnixNano", "trace.v1.Span.endTimeUnixNano", "trace.v1.Span.Event.timeUnixNano",
  "logs.v1.LogRecord.timeUnixNano", "logs.v1.LogRecord.observedTimeUnixNano",
  ...["NumberDataPoint", "HistogramDataPoint", "ExponentialHistogramDataPoint", "SummaryDataPoint"].flatMap(t => [`metrics.v1.${t}.startTimeUnixNano`, `metrics.v1.${t}.timeUnixNano`]),
  "metrics.v1.Exemplar.timeUnixNano",
].map(p => `.opentelemetry.proto.${p}`));
function scanCapture(capture, markers) {
  const result = analyzeCapture(capture, markers);
  if (result.errors.length) throw Error("invalid-or-unsupported-capture");
  return result.findings;
}
function analyzeCapture(capture, markers) {
  const findings = [], metadata = [], errors = [];
  if (!capture.signals.length && !capture.console.length) errors.push({ channel: "capture", field: "capture", reason: "empty-capture" });
  const needles = markers.map((marker, index) => {
    const text = new Set([marker.value]), binary = [Buffer.from(marker.value)];
    if (/^(credential|public-key|cookie|signature|key)$/.test(marker.type) && marker.value.length >= 8) {
      for (const encoding of ["base64", "hex"]) {
        const decoded = Buffer.from(marker.value, encoding);
        if (decoded.length >= 6 && decoded.toString(encoding).replace(/=+$/, "") === marker.value.replace(/-/g, "+").replace(/_/g, "/").replace(/=+$/, "")) {
          binary.push(decoded);
          for(const spelling of ['base64','base64url','hex'])text.add(decoded.toString(spelling));
        }
      }
    }
    binary.push(...[...text].map(value=>Buffer.from(value)));
    return { marker, index, text: [...text], binary };
  });
  function match(value, location, destination = findings, bytes = false, exact = false) {
    for (const {marker, index, text, binary} of needles) {
      if (!marker.value) continue;
      // Credentials can be exported as protobuf bytes instead of their API
      // Base64/hex representation. Do not reinterpret short numeric OTPs.
      const found = exact ? String(value).includes(marker.value) : bytes ? binary.some(needle=>value.includes(needle)) : text.some(needle=>String(value).includes(needle));
      if (found) destination.push({ marker: index, type: marker.type, label: marker.label, ...location });
    }
  }
  function walk(type, buffer, location) {
    const reader = protobuf.Reader.create(buffer);
    const occurrences = new Map();
    while (reader.pos < reader.len) {
      const tag = reader.uint32(), field = type.fieldsById[tag >>> 3], wire = tag & 7;
      if (!field) throw Error("unknown-schema-field");
      const index = occurrences.get(field.id) || 0;
      occurrences.set(field.id, index + 1);
      const fieldPath = `${location.field}.${field.name}${field.repeated ? `[${index}]` : ""}`;
      const here = { ...location, field: fieldPath };
      if (field.resolvedType instanceof protobuf.Type) {
        if (wire !== 2) throw Error("invalid-message-wire");
        walk(field.resolvedType, reader.bytes(), here);
      } else if (field.type === "string" || field.type === "bytes") {
        if (wire !== 2) throw Error("invalid-payload-wire");
        const bytes = reader.bytes();
        match(field.type === "string" ? utf8.decode(bytes) : bytes, here, findings, field.type === "bytes");
      } else {
        const expected = /^(double|fixed64|sfixed64)$/.test(field.type) ? 1 : /^(float|fixed32|sfixed32)$/.test(field.type) ? 5 : 0;
        const method = field.resolvedType instanceof protobuf.Enum ? "int32" : field.type;
        const numeric = (r, position = here) => {
          const start = r.pos;
          const value = r[method]().toString();
          const clock = field.type === "fixed64" && clocks.has(`${type.fullName}.${field.name}`);
          match(value, position, clock ? metadata : findings);
          // A numeric attribute can disclose 012345 as 12345. Only typed OTP
          // markers are normalised, only at numeric fields, never text/IDs.
          if(!clock)for(const {marker,index} of needles)if(marker.type==='otp'&&/^0\d{5}$/.test(marker.value)&&BigInt(marker.value).toString()===value){
            findings.push({marker:index,type:marker.type,label:marker.label,...position,encoding:'numeric-canonical'});
          }
          if(clock)match(Buffer.from(r.buf.subarray(start,r.pos)),{...position,encoding:'fixed64-wire'},metadata,true);
        };
        if (wire === 2 && field.repeated) {
          const packed = protobuf.Reader.create(reader.bytes());
          let element = 0;
          while (packed.pos < packed.len) numeric(packed, { ...here, field: `${location.field}.${field.name}[${element++}]` });
        } else {
          if (wire !== expected) throw Error("invalid-number-wire");
          numeric(reader);
        }
      }
    }
  }
  for (const [request, signal] of capture.signals.entries()) {
    const route = channels[signal.route];
    const base = { channel: route?.channel || "unsupported-otlp", request, field: "request" };
    try {
      if (!route) throw Error("unsupported-otlp-route");
      const body = Buffer.from(signal.body); let offset = 0, frame = 0;
      if (!body.length) throw Error("empty-grpc-request");
      while (offset < body.length) {
        if (body.length - offset < 5) throw Error("truncated-grpc-header");
        if (body[offset] !== 0) throw Error("unsupported-grpc-compression");
        const length = body.readUInt32BE(offset + 1); offset += 5;
        if (length > body.length - offset) throw Error("truncated-grpc-body");
        walk(route.type, body.subarray(offset, offset + length), { ...base, frame: frame++, field: route.type.fullName });
        offset += length;
      }
    } catch (error) {
      // Never include parser errors: some libraries embed payload bytes in them.
      errors.push({ ...base, reason: "invalid-or-unsupported-capture" });
    }
  }
  // Reassemble stream chunks, not processes or stdout/stderr. A payload split
  // across chunks must fail; a suffix/prefix across channels must not.
  const streams = new Map();
  for (const item of capture.console) {
    const key = JSON.stringify([item.source, item.channel]);
    if (!streams.has(key)) streams.set(key, []);
    streams.get(key).push(Buffer.from(item.body));
  }
  let stream = 0;
  for (const [key, chunks] of streams) {
    const [, channel] = JSON.parse(key);
    let lines;
    try { lines = utf8.decode(Buffer.concat(chunks)).split("\n"); }
    catch { errors.push({ channel: channel === "stdout" ? "stdout" : "stderr", stream, field: "console.payload", reason: "invalid-console-utf8" }); stream++; continue; }
    for (const [line, text] of lines.entries()) {
      const here = { channel: channel === "stdout" ? "stdout" : "stderr", stream, line, field: "console.payload" };
      // The native formatter's anchored RFC3339 clock + level prefix only.
      // Date-looking strings in messages/attributes or unrecognised lines stay
      // payload. No whole-line or logger/field whitelist.
      const clock = "\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,9})?Z";
      const level = "(?:TRACE|DEBUG|INFO|WARN|ERROR)";
      const prefix = new RegExp(`^(?:(${clock})\\s+${level}\\s+|\\x1b\\[2m(${clock})\\x1b\\[0m \\x1b\\[(?:31|32|33|34|35)m *${level}\\x1b\\[0m )`).exec(text);
      const timestamp = prefix && (prefix[1] || prefix[2]);
      if (timestamp && Number.isFinite(Date.parse(timestamp))) {
        match(timestamp, { ...here, field: "console.formatter.timestamp" }, metadata);
        const start = text.indexOf(timestamp);
        match(text.slice(0, start) + text.slice(start + timestamp.length), here);
      } else match(text, here);
    }
    stream++;
  }
  // Legacy-style raw concatenation in capture arrival order, diagnostic only;
  // never the acceptance decision or a reconstruction of the lost failed run.
  const legacy = capture.signals.map(s => Buffer.from(s.body).toString()).join("") + capture.console.map(s => Buffer.from(s.body).toString()).join("");
  const legacyMatches = markers.filter(m => m.value && legacy.includes(m.value)).map(({ type, label }) => ({ type, label }));
  const legacyLocations = [];
  capture.signals.forEach((signal, request) => match(Buffer.from(signal.body).toString(), { channel: channels[signal.route]?.channel || "unsupported-otlp", request, field: "grpc.raw-wire" }, legacyLocations, false, true));
  capture.console.forEach((item, chunk) => match(Buffer.from(item.body).toString(), { channel: item.channel === "stdout" ? "stdout" : "stderr", chunk, field: "console.raw-chunk" }, legacyLocations, false, true));
  return { findings, metadata, errors, legacyMatches, legacyLocations };
}
module.exports = { scanCapture, analyzeCapture, root, channels };
