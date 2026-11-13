// Identical synthetic wire bytes for actual ASP.NET FormFeature and native routes.
const id='11111111-1111-1111-1111-111111111111';
function section(name,data,extra='',kind='form-data'){return Buffer.concat([Buffer.from(`Content-Disposition: ${kind}; name="${name}"\r\n${extra}\r\n`),Buffer.isBuffer(data)?data:Buffer.from(data)]);}
function multipart(parts,boundary='form230'){return Buffer.concat(parts.flatMap(p=>[Buffer.from(`--${boundary}\r\n`),p,Buffer.from('\r\n')]).concat([Buffer.from(`--${boundary}--\r\n`)]));}
const cases=[];
function add(name,parts,boundary='form230'){cases.push({name,contentType:`multipart/form-data; boundary="${boundary}"`,body:multipart(parts,boundary).toString('base64')});}
function utf16be(text){const bytes=Buffer.from(text,'utf16le');return Buffer.from(bytes).swap16();}
for(const [name,bytes,charset] of [
 ['utf8',Buffer.from(id),'utf-8'],['utf16le',Buffer.from(id,'utf16le'),'utf-16'],['utf16be',utf16be(id),'utf-16be'],
 ['utf8-bom',Buffer.concat([Buffer.from([239,187,191]),Buffer.from(id)]),'utf-8'],
 ['le-bom',Buffer.concat([Buffer.from([255,254]),Buffer.from(id,'utf16le')]),'utf-8'],
 ['be-bom',Buffer.concat([Buffer.from([254,255]),utf16be(id)]),'utf-16'],
 ['unknown-charset',Buffer.from(id),'x-not-an-encoding'],['disabled-utf7',Buffer.from(id),'utf-7'],
 ['ascii',Buffer.from(id),'us-ascii'],['latin',Buffer.from(id),'iso-8859-1'],
 ['utf32le',Buffer.concat([...id].map(c=>Buffer.from([c.charCodeAt(0),0,0,0]))),'utf-32'],
 ['utf32be',Buffer.concat([...id].map(c=>Buffer.from([0,0,0,c.charCodeAt(0)]))),'utf-32be'],
 ])add(name,[section('entwurfId',bytes,`Content-Type: text/plain; charset=${charset}\r\n`),section('anfrageId','1')]);
add('attachment-ignored',[section('entwurfId','not-an-id','','attachment'),section('entwurfId',id),section('anfrageId','1')]);
add('attachment-after-valid',[section('entwurfId',id),section('anfrageId','1'),section('entwurfId','not-an-id','','attachment')]);
add('utf16-identities',[section('entwurfId',Buffer.from(id,'utf16le'),'Content-Type: text/plain; charset=utf-16\r\n'),section('ANFRAGEID',Buffer.from('1','utf16le'),'Content-Type: text/plain; charset=utf-16\r\n')]);
for(const be of [false,true]){const payload=Buffer.concat([...id].map(c=>Buffer.from(be?[0,0,0,c.charCodeAt(0)]:[c.charCodeAt(0),0,0,0])));add('utf32-bom-'+(be?'be':'le'),[section('entwurfId',Buffer.concat([Buffer.from(be?[0,0,254,255]:[255,254,0,0]),payload]),'Content-Type: text/plain; charset=us-ascii\r\n'),section('anfrageId','1')]);}
add('unknown-attachment',[section('unknown','anything','','attachment'),section('entwurfId',id),section('anfrageId','1')]);
add('repeated-case-empty',[section('ENTWURFID',''),section('entwurfId',id),section('ANFRAGEID',''),section('anfrageId','1')]);
add('repeated-case-invalid',[section('entwurfId',id),section('ENTWURFID',id),section('anfrageId','1')]);
for(const n of [127,128,129])add('boundary-'+n,[section('entwurfId',id),section('anfrageId','1')],'b'.repeat(n));
for(const n of [16,17])add('headers-'+n,[section('entwurfId',id,Array.from({length:n-1},(_,i)=>`X-Header-${i}: x\r\n`).join('')),section('anfrageId','1')]);
add('duplicate-case-headers',[section('entwurfId',id,Array.from({length:17},(_,i)=>`${i%2?'X-SAME':'x-same'}: x\r\n`).join('')),section('anfrageId','1')]);
add('duplicate-disposition',[section('entwurfId',id,'content-disposition: form-data; name="entwurfId"\r\n'),section('anfrageId','1')]);
// .NET sums header-line UTF16 lengths, without line CRLF. Normal ASCII spelling.
for(const n of [16380,16382,16384,16385,16400]){
 const prefix='Content-Disposition: form-data; name="entwurfId"';const extra='X-Pad: ';const pad='x'.repeat(n-prefix.length-extra.length);
 add('header-length-'+n,[section('entwurfId',id,extra+pad+'\r\n'),section('anfrageId','1')]);
}
for(const n of [16382,16400]){
 const prefix='Content-Disposition: form-data; name="entwurfId"';const extra='X-Pad:';const pad=' '.repeat(n-prefix.length-extra.length-1)+'x';
 add('header-whitespace-'+n,[section('entwurfId',id,extra+pad+'\r\n'),section('anfrageId','1')]);
}
add('empty-filename-first',[section('entwurfId',id),section('anfrageId','1'),Buffer.from('Content-Disposition: form-data; name="datei"; filename=""\r\n\r\n'),Buffer.from('Content-Disposition: form-data; name="datei"; filename="a.pdf"\r\n\r\n%PDF-x')]);
for(const [name,padding] of [['boundary-transport-padding',' \t'],['boundary-long-padding',' '.repeat(101)]]){
 const original=cases[0];cases.push({...original,name,body:Buffer.from(original.body,'base64').toString().replaceAll('--form230\r\n','--form230'+padding+'\r\n')});
 cases.at(-1).body=Buffer.from(cases.at(-1).body).toString('base64');
}
for(const kind of ['scalar','file','ignored'])for(const total of [1024,1025]){
 const extra=kind==='file'?Buffer.from('Content-Disposition: form-data; name="ignored"; filename="x.bin"\r\n\r\nx'):section('ignored','x','',kind==='ignored'?'attachment':'form-data');
 add('section-count-'+kind+'-'+total,[section('entwurfId',id),section('anfrageId','1'),...Array.from({length:total-2},()=>extra)]);
}
function urlCase(name,bytes,charset='utf-8'){cases.push({name,contentType:'application/x-www-form-urlencoded; charset='+charset,body:(Buffer.isBuffer(bytes)?bytes:Buffer.from(bytes)).toString('base64')});}
const urlIdentity=`entwurfId=${id}&anfrageId=1`;
for(const length of [2048,2049]){
 urlCase('url-raw-key-'+length,urlIdentity+'&'+'k'.repeat(length)+'=x');
 urlCase('url-escaped-key-'+length,urlIdentity+'&'+'%61'.repeat(Math.floor(length/3))+'a'.repeat(length%3)+'=x');
 urlCase('url-utf8-key-'+length,urlIdentity+'&'+'ä'.repeat(Math.floor(length/2))+'a'.repeat(length%2)+'=x');
}
for(const length of [4194304,4194305]){
 urlCase('url-raw-value-'+length,urlIdentity+'&unknown='+'a'.repeat(length));
 urlCase('url-escaped-value-'+length,urlIdentity+'&unknown='+'%61'.repeat(Math.floor(length/3))+'a'.repeat(length%3));
}
for(const count of [1022,1023,1024])urlCase('url-empty-tail-'+count,urlIdentity+'&'.repeat(count));
for(const text of ['', '&', '&&', '=', '=&', '&&'+urlIdentity, urlIdentity+'&', urlIdentity+'&&', urlIdentity+'&='])urlCase('url-empty-shape-'+cases.length,text);
module.exports={cases,id};
if(require.main===module)process.stdout.write(JSON.stringify(cases));
