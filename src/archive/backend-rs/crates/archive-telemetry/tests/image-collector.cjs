// In-memory test collector started on an isolated local container network.
// stdout is unused; nothing (including headers) is written to disk.
const http = require('node:http');
const captures = [];
const token = '235 local-test';
const paths = new Set(['/v1/logs', '/v1/traces', '/v1/metrics']);
let active = 0;
let invalid = false;
let sealed = false;
const server = http.createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/ready') {
    response.end();
    return;
  }
  // Called only after the product process and its stdout/stderr closed.
  if (request.method === 'POST' && request.url === '/seal') {
    sealed = true;
    if (invalid || active !== 0) response.statusCode = 409;
    response.end(JSON.stringify(captures));
    return;
  }
  if (sealed || request.method !== 'POST' || !paths.has(request.url)
      || request.headers['x-otlp-test'] !== token
      || request.headers['content-type'] !== 'application/x-protobuf'
      || request.headers['content-encoding']) {
    invalid = true;
    response.statusCode = 403;
    response.end();
    return;
  }
  active++;
  try {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) {
      size += chunk.length;
      if (size > 1_000_000) throw Error('oversized payload');
      chunks.push(chunk);
    }
    if (!request.complete || !size || captures.length >= 32) throw Error('incomplete capture');
    captures.push(`${request.url} ${Buffer.concat(chunks).toString('hex')}`);
    response.setHeader('Content-Type', 'application/x-protobuf');
    response.end();
  } catch {
    invalid = true;
    response.destroy();
  } finally {
    active--;
  }
});
server.listen(4318, '0.0.0.0');
