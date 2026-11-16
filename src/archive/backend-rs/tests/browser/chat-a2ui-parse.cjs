// Isolated locked, unchanged frontend parser over ACTUAL product HTTP output
// supplied on stdin by chat_a2ui_api, never mocked component props.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),Module=require('node:module');
const root=path.resolve(process.env.ARCHIVE_STATIC_ROOT,'..'),source=path.join(root,'lib/a2ui.ts');
const ts=require(path.join(root,'node_modules/typescript'));
const original=fs.readFileSync(source,'utf8');
assert.equal(original,fs.readFileSync(path.resolve('..','frontend/lib/a2ui.ts'),'utf8'),'parser is the unchanged locked client source');
const compiled=ts.transpileModule(original,{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText;
const mod=new Module(source,module);mod.filename=source;mod.paths=Module._nodeModulePaths(path.dirname(source));mod._compile(compiled,source);
const raw=JSON.parse(fs.readFileSync(0,'utf8')),parse=mod.exports.leseA2uiOberflaechen,parsed=parse(raw);
assert.equal(parsed.length,4);assert.deepEqual(parsed.map(p=>p.nachrichten[1].updateComponents.components[0].component),['LiedKarte','AuftrittKarte','Quellen','ProgrammListe']);
for(let i=0;i<4;i++){const bad=structuredClone(raw);bad[i*2+1].updateComponents.components[0].href='https://invalid.test';assert.equal(parse(bad).length,3,'strict additional-property rejection '+i);}
console.log('PASS unchanged strict parser: all four actual server components and four extra-property rejection controls');
