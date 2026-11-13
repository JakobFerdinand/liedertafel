// Public synthetic HTTP propagation controls, reusable by consumer native gates.
const assert=require('node:assert/strict');
const type=require('./telemetry-redaction.cjs').root.lookupType('opentelemetry.proto.collector.trace.v1.ExportTraceServiceRequest');
const trace='bade01cafe02face03dead04beef0506',parent='bade08cafe090a0b',state='archive=synthetic230,peer=shared';
function decode(bytes){const spans=[];for(let at=0;at<bytes.length;){assert.equal(bytes[at],0);const n=bytes.readUInt32BE(at+1);const message=type.decode(bytes.subarray(at+5,at+5+n));at+=5+n;for(const resource of message.resourceSpans||[])for(const scope of resource.scopeSpans||[])spans.push(...scope.spans||[]);}return spans;}
function hex(bytes){return Buffer.from(bytes||[]).toString('hex');}
function path(span){return (span.attributes||[]).find(a=>['uri','url.path','http.target'].includes(a.key))?.value?.stringValue;}
async function exercise(base){
 for(const [name,headers] of [
  ['valid',{traceparent:`00-${trace}-${parent}-01`,tracestate:state}],
  ['unsampled',{traceparent:`00-${trace.slice(0,-2)}08-${parent}-00`,tracestate:state}],
  ['missing',{}],['invalid',{traceparent:'not-a-trace',tracestate:state}],
  ['zero',{traceparent:`00-${'0'.repeat(32)}-${parent}-01`}],
  ['bad-state',{traceparent:`00-${trace.slice(0,-2)}09-${parent}-01`,tracestate:'invalid state'}],
 ]) {const response=await fetch(base+'/api/w3c-'+name,{headers:{...headers,baggage:'private=synthetic-baggage-forbidden-230'}});await response.arrayBuffer();}
 for(const route of ['/alive','/health']){const response=await fetch(base+route,{headers:{traceparent:`00-${trace.slice(0,-2)}0a-${parent}-01`,tracestate:state}});await response.arrayBuffer();}
}
function verify(spans,alwaysOn){
 const find=name=>spans.filter(s=>path(s)==='/api/w3c-'+name);
 const valid=find('valid');assert.equal(valid.length,1,'one actual request span per sampled HTTP call');
 assert.equal(hex(valid[0].traceId),trace,'incoming HTTP trace id');assert.equal(hex(valid[0].parentSpanId),parent,'incoming remote parent span id');
 assert.equal(valid[0].traceState,state,'incoming tracestate');assert.equal(valid[0].flags&1,1);assert.notEqual(hex(valid[0].spanId),parent);
 const fresh=['missing','invalid','zero'].map(name=>{const found=find(name);assert.equal(found.length,1,name);assert.equal(hex(found[0].parentSpanId),'',name+' fresh root');assert.notEqual(hex(found[0].traceId),trace);assert.equal(found[0].traceState||'','');return hex(found[0].traceId);});assert.equal(new Set(fresh).size,3,'independent fresh roots');
 const invalid=find('bad-state');assert.equal(invalid.length,1);assert.equal(hex(invalid[0].traceId),trace.slice(0,-2)+'09');assert.equal(hex(invalid[0].parentSpanId),parent);assert.equal(invalid[0].traceState||'','');
 const off=find('unsampled');assert.equal(off.length,alwaysOn?1:0,'parent sampling versus Development AlwaysOn');if(alwaysOn){assert.equal(hex(off[0].traceId),trace.slice(0,-2)+'08');assert.equal(hex(off[0].parentSpanId),parent);assert.equal(off[0].traceState,state);assert.equal(off[0].flags&1,1);}
 assert(!spans.some(s=>['/alive','/health'].includes(path(s))),'exact health probes excluded');
}
module.exports={decode,exercise,verify,trace,parent,state,hex};
