// Synthetic tooling oracles only. No browser/native/Rust/standards acceptance.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const R=require('../../scripts/rendered-review-ui-checks.cjs');
const P13={count:4357,totalBytes:259559615,closure:'34540765dfd1b5d46666841378221cce0b40204014c4a8c2dc25ddddbe56d1ed'};
const P14={count:4453,totalBytes:261154796,closure:'f0c9ff2492565b273130a4596ea608d646728cacf5dd73294c88e0d03c6e86a6'};
const P15={count:4453,totalBytes:261178860,closure:'e20e5f5e14f2088d4c8e6718566824b9ce0911120be519386f09709f292f5e03'};
const P16={count:4557,totalBytes:262320783,closure:'8e3fcc0c3255613609a423a5440934b261feee32a5cc73510f4633648b4a135e'};
const renderer={loader:'968aae4935523f528e2314c4dbd831b2dd326ac8d3de3444fd033b73463b0225',wasm:'8b715ed561ec644757f305ddad0a99ae728c6705f9868e96ccb53c8c9143c4b3'};
const rendererV1={...renderer,wasm:'d1623f880b530b3a6503436d43bcd05d01f6ff8fc8a40c782ba848cd209cb217'};
const objectLayoutRenderer={loader:'f95ae827b12703999ce1daafebbf6830f5f4d2c14bb7805a8f47abe54772b079',wasm:'2e2126ead8e00ef7c79d21e3836d66957b19e827523f7c054ca887f3c11ae5b7'};
test('mode defaults off and invalid flag values never silently enable/disable checks',()=>{
  for(const value of [undefined,'','0'])assert.equal(R.enabled(value),false);
  assert.equal(R.enabled('1'),true);
  for(const value of [1,true,'true','yes','2'])assert.throws(()=>R.enabled(value));
});
test('only exact closed P13/P14 byte-identity tuples classify; caller profile is never authority',()=>{
  assert.equal(R.closedIdentity(P13).profile,13);assert.equal(R.closedIdentity({...P14,profile:13}).profile,14);
  for(const changed of [{...P14,count:4454},{...P14,totalBytes:P14.totalBytes+1},{...P14,closure:'0'.repeat(64)}])assert.equal(R.closedIdentity(changed),null);
  assert.equal(R.identityProfile(R.closedIdentity(P14).identity),14);
  assert.throws(()=>R.identityProfile('0'.repeat(64)));
  assert.equal(R.classifyRuntime([{name:'invented.wasm',length:1,sha256:'a'.repeat(64)}]),null);
});
test('opt-in preflight retains fixture, embedded runtime, export, exact renderer and evidence requirements',()=>{
  const valid={caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime:R.closedIdentity(P14),renderer};
  assert.equal(R.preflight(valid).profile,14);assert.equal(R.preflight({...valid,caseName:'portable'}).profile,14);
  for(const change of [{caseName:'long'},{bundled:false},{downloads:false},{resultDirectory:''},{runtime:R.closedIdentity(P13)},{renderer:{...renderer,wasm:'0'.repeat(64)}},{runtime:{...R.closedIdentity(P14),totalBytes:1}}])assert.throws(()=>R.preflight({...valid,...change}));
});
test('closed P16 preserves exact renderer and refuses mixed compiler closure fields',()=>{
  const runtime=R.closedIdentity(P16);assert.equal(runtime.profile,16);
  assert.equal(R.identityProfile(runtime.identity),16);
  assert.equal(R.preflight({caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime,renderer}).profile,16);
  for(const change of [{count:P15.count},{totalBytes:P15.totalBytes},{closure:P15.closure}])
    assert.equal(R.closedIdentity({...P16,...change}),null);
  assert.throws(()=>R.preflight({caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime:{...runtime,profile:15},renderer}));
});
function evidence(pageIndex=0,width=210,height=297){
  const box=[0,0,width*72/25.4,height*72/25.4];
  const uncertainty=Math.max(...[box[2],box[3]].map(value=>Math.abs(value)*2**-23*25.4/72+Number.EPSILON*1000));
  return {pageIndex,rawBox:box,characterStart:null,characterEnd:null,sourceLocation:null,roleProvenance:null,unit:'mm',method:'native-page-box',
    measured:width.toFixed(9)+' x '+height.toFixed(9),expected:'A4 210 x 297; A3 297 x 420 only for necessary illustrations',
    uncertainty:{absoluteError:uncertainty,method:'pdfium-float32-roundoff-bound',limitations:['nominal-paper-tolerance-unspecified']}};
}
test('physical paper oracle checks raw units and roundoff without inventing a manufacturing tolerance',()=>{
  assert.equal(R.paperObservation(evidence()).expectedOutcome,'pass');
  assert.equal(R.paperObservation(evidence(0,215.9,279.4)).expectedOutcome,'indeterminate');
  assert.equal(R.paperObservation(evidence(0,297,420)).expectedOutcome,'indeterminate');
  for(const mutate of [e=>e.measured='210 x 297',e=>e.rawBox=[0,0,0,0],e=>e.unit='points',e=>e.uncertainty.absoluteError=1,e=>e.roleProvenance='authorLabel']){
    const value=evidence();mutate(value);assert.throws(()=>R.paperObservation(value));
  }
});
const ids=['KNU-PAPER-001','KNU-MARGINS-001','KNU-TYPOGRAPHY-001','KNU-PARAGRAPH-RENDERED-001','KNU-HEADINGS-RENDERED-001','KNU-PAGINATION-001','KNU-STRUCTURE-001','KNU-ABSTRACT-001','KNU-LENGTH-001','KNU-REFERENCES-001','KNU-APPENDIX-001','KNU-CAPTION-RENDERED-001','KNU-BIBLIOGRAPHY-RENDERED-001'];
function rows(){return ids.map(id=>({id,support:['KNU-MARGINS-001','KNU-PARAGRAPH-RENDERED-001','KNU-HEADINGS-RENDERED-001'].includes(id)?'unsupported':'partial',
  outcome:id==='KNU-PAPER-001'?'pass':['KNU-LENGTH-001','KNU-BIBLIOGRAPHY-RENDERED-001'].includes(id)?'notApplicable':['KNU-MARGINS-001','KNU-PARAGRAPH-RENDERED-001','KNU-HEADINGS-RENDERED-001'].includes(id)?'unavailable':'indeterminate',
  reason:id==='KNU-TYPOGRAPHY-001'?'measurement-definition-unresolved':'glyph-ink-unavailable',citation:'https://csc.knu.ua/uk/filer/canonical/1740300611/2680/',evidence:id==='KNU-PAPER-001'?[evidence()]:[]}));}
test('category oracle refuses all-green, fabricated physical pass, duplicate page and missing coverage',()=>{
  assert.equal(R.assertRules(rows(),1).length,1);
  for(const mutate of [r=>r.splice(1,1),r=>r[1].outcome='pass',r=>r[2].outcome='pass',r=>r[0].evidence=[evidence(0,215.9,279.4)],r=>r[0].citation='https://unapproved.invalid/']){
    const value=rows();mutate(value);assert.throws(()=>R.assertRules(value,1));
  }
  const duplicate=rows();duplicate[0].evidence.push(evidence());assert.throws(()=>R.assertRules(duplicate,2));
});
const source=fs.readFileSync(path.join(__dirname,'../../scripts/test-local-authoring-ui.cjs'),'utf8');
const begin=source.indexOf('function assertPackageCompilerTrace('),end=source.indexOf('async function observePackageCompilerPhases(',begin);
assert.ok(begin>=0&&end>begin);
const traceCheck=vm.runInNewContext(source.slice(begin,end)+'\nassertPackageCompilerTrace;',{assert,renderedReview:R});
function traceFixture(profile){
  const runtime=R.closedIdentity(profile===13?P13:profile===14?P14:P15),request='a'.repeat(32),digest='b'.repeat(64),capture='11111111-2222-3333-4444-555555555555';
  const trace=['latex','bibtex','latex','latex'].map((tool,index)=>({posted:true,entered:true,terminated:true,outcome:'native-return',nativeReturn:0,
    tag:{tool,profile,version:23,engineId:'pdftex',runtimeIdentity:runtime.identity,request,digest,epoch:7,capture,job:'c'.repeat(32),
      snapshot:{captureId:capture},ordinal:index+1,nonce:(index+1).toString(16).padStart(32,'0')}}));
  return {runtime,trace,details:`Request: ${request}; epoch: 7. Input: ${digest}; PDF: ${'d'.repeat(64)}`};
}
test('existing package observer accepts exact13/14 while preserving native ordering/backend/freshness guards',()=>{
  for(const profile of [13,14,15]){
    const f=traceFixture(profile);assert.equal(traceCheck(f.details,f.trace,'local-authoring-v4',f.runtime.identity).profile,profile);
    for(const mutate of [t=>t[0].tag.profile=16,t=>t[0].tag.profile=profile===13?14:13,t=>t[0].tag.runtimeIdentity='0'.repeat(64),
      t=>t[0].entered=false,t=>t[0].nativeReturn=1,t=>t[0].tag.version=6,t=>t[0].tag.digest='0'.repeat(64),t=>t[1].tag.tool='biber',
      t=>t[1].tag.nonce=t[0].tag.nonce,t=>t[0].tag.ordinal=2,t=>t[3].tag.tool='bibtex']){
      const bad=JSON.parse(JSON.stringify(f.trace));mutate(bad);assert.throws(()=>traceCheck(f.details,bad,'local-authoring-v4',f.runtime.identity));
    }
  }
  const f=traceFixture(14);assert.throws(()=>traceCheck(f.details,f.trace,'local-authoring-v4','0'.repeat(64)));
});

test('exact P15 keeps its distinct closure and reviewed renderer admission',()=>{const runtime=R.closedIdentity(P15);assert.equal(runtime.profile,15);assert.equal(R.identityProfile(runtime.identity),15);assert.equal(R.preflight({caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime,renderer}).profile,15);assert.equal(R.closedIdentity({...P15,totalBytes:P14.totalBytes}),null);});

function vector(name) {return JSON.parse(fs.readFileSync(path.join(__dirname,'../Workspace.Tests/Fixtures/RenderedReview/',name),'utf8'));}
function producerRows(v) {return v.response.payload.results.map(row=>({id:row.ruleId,outcome:row.outcome,support:row.supportState,reason:row.reason,evidence:row.evidence,citation:'https://csc.knu.ua/uk/filer/canonical/1740300611/2680/'}));}
test('literal role oracle validates actual Rust schema1 heading/caption vectors without claiming visual proof',()=>{
  for(const name of ['producer-role-heading.json','producer-role-caption.json']){
    const v=vector(name),labels=v.request.payload.annotations;
    const row=v.response.payload.results.find(r=>r.evidence.some(e=>e.observation));
    assert.equal(R.roleObservation(row.evidence[0],{labels,renderer}).text,row.evidence[0].measured);
    R.assertRules(producerRows(v),1,{labels,renderer});
    for(const mutate of [e=>e.observation.visualBold=true,e=>e.observation.unicodeCase='visual-upper',e=>e.roleProvenance.annotationId=999,
      e=>e.characterEnd++,e=>e.measured+='\0',e=>e.uncertainty.limitations.splice(1,1),e=>e.observation.relativeOrder='below-illustration']){
      const bad=structuredClone(row.evidence[0]);mutate(bad);assert.throws(()=>R.roleObservation(bad,{labels,renderer}));
    }
    const badRows=producerRows(v);badRows.find(r=>r.id===row.ruleId).outcome='pass';assert.throws(()=>R.assertRules(badRows,1,{labels,renderer}));
  }
});
test('genuine metric and placement shapes require exact corrected v2; old v1 stays unavailable',()=>{
  const v=vector('producer-metrics-v2.json'),labels=v.request.payload.annotations;
  const paperBox=v.response.payload.results.find(r=>r.ruleId==='KNU-PAPER-001').evidence[0].rawBox;
  const metrics=v.response.payload.results.find(r=>r.ruleId==='KNU-MARGINS-001').evidence;
  assert.equal(metrics.length,4);for(const e of metrics)R.metricShape(e,{labels,paperBox});
  for(const mutate of [e=>e.measured++,e=>e.uncertainty.absoluteError=-1,e=>e.classification.characterCount=100000,e=>e.expected='25mm']){
    const bad=structuredClone(metrics[0]);mutate(bad);assert.throws(()=>R.metricShape(bad,{labels,paperBox}));
  }
  R.assertRules(producerRows(v),1,{labels,renderer});
  assert.throws(()=>R.assertRules(producerRows(v),1,{labels,renderer:rendererV1}));
  const h=vector('producer-role-native-heading.json'),row=h.response.payload.results.find(r=>r.ruleId==='KNU-HEADINGS-RENDERED-001');
  R.roleObservation(row.evidence[0],{labels:h.request.payload.annotations,renderer});
  assert.throws(()=>R.roleObservation(row.evidence[0],{labels:h.request.payload.annotations,renderer:rendererV1}));
  assert.equal(R.rendererVersion({...rendererV1,inkSchema:2,schemaVersion:2}),1);
  assert.equal(R.rendererVersion({...renderer,inkSchema:1,schemaVersion:1}),2);
  const prototype={...renderer,wasm:'5369631dae420aad15cb7aa5deaf229936b6e9452087900eeb480ceb78859e74'};
  assert.throws(()=>R.rendererVersion(prototype));
  assert.throws(()=>R.preflight({caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime:R.closedIdentity(P15),renderer:prototype}));
});

test('each exact renderer tuple retains its schema and preflight identity; prototypes and mixtures refuse',()=>{
  for(const [pair,version,loaderBytes,wasmBytes] of [[renderer,2,14103,3716447],[rendererV1,1,14103,3699309],[objectLayoutRenderer,2,14175,3725577]]){
    assert.equal(R.rendererVersion(pair),version);
    const result=R.preflight({caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime:R.closedIdentity(P15),renderer:{...pair,inkSchema:3-version}});
    assert.equal(result.renderer.wasm,pair.wasm);assert.equal(result.renderer.schemaVersion,version);assert.equal(result.renderer.loaderBytes,loaderBytes);assert.equal(result.renderer.wasmBytes,wasmBytes);
  }
  assert.equal(R.rendererVersion(),2,'canonical next candidate is corrected v2');
  for(const bad of [
    {...renderer,wasm:'3d312207560db73be538860501c363888593e75b1b98ca443a4a4a4b9bc9b2dd'},
    {...renderer,wasm:'5369631dae420aad15cb7aa5deaf229936b6e9452087900eeb480ceb78859e74'},
    {...renderer,wasm:'463b434e64eeb265af97d974d81d05e11cc683df6095727d6ec5c4e6bf3717bc'},
    {...renderer,loader:'040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b'},
    {...renderer,loaderBytes:14104},{...renderer,wasmBytes:3699309},{...rendererV1,wasmBytes:3716447},
    {...objectLayoutRenderer,loader:renderer.loader},{...objectLayoutRenderer,wasm:renderer.wasm},
    {...renderer,wasm:'0'.repeat(64),inkSchema:2},null,
  ]){
    assert.throws(()=>R.rendererVersion(bad));
    assert.throws(()=>R.preflight({caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime:R.closedIdentity(P15),renderer:bad}));
  }
});
test('genuine two-line typography values match actual native glyphs and baselines without numeric standard verdict',()=>{
  const v=vector('producer-typography-v2.json'),labels=v.request.payload.annotations;
  const fixtureDir=path.join(__dirname,'../../rust/src/rendered_review/fixtures/typography');
  const raw=fs.readFileSync(path.join(fixtureDir,'native-two-line-v2.json')),native=JSON.parse(raw);
  const lineage=JSON.parse(fs.readFileSync(path.join(fixtureDir,'native-two-line-v2.json.lineage.json')));
  const {createHash}=require('node:crypto');assert.equal(createHash('sha256').update(raw).digest('hex'),lineage.facts.sha256);
  assert.equal(lineage.runtime.wasmSha256,renderer.wasm);
  // Retain the exact captured bytes/hash. The pretty Rust response vector is a
  // consumer serialization, not a byte-identical copy of the native payload.
  assert.equal(v.request.payload.factsSha256,lineage.facts.sha256);assert.equal(v.request.payload.pdfSha256,native.documentSha256);
  const letters=native.pages[0].facts.characters.filter(c=>/\p{Alphabetic}/u.test(String.fromCodePoint(c.unicode)));
  assert.equal(letters.length,4);const heights=letters.map(c=>(c.inkBox[3]-c.inkBox[1])*25.4/72);
  const baselines=[...new Set(letters.map(c=>c.origin[1]))].sort((a,b)=>b-a),distances=baselines.slice(1).map((b,i)=>(baselines[i]-b)*25.4/72);
  const expected={'letter-ink-height-min':Math.min(...heights),'letter-ink-height-max':Math.max(...heights),'baseline-distance-min':Math.min(...distances),'baseline-distance-max':Math.max(...distances)};
  const rows=producerRows(v),typography=rows.find(r=>r.id==='KNU-TYPOGRAPHY-001');
  assert.equal(typography.outcome,'indeterminate');assert.equal(typography.support,'partial');assert.equal(typography.evidence.length,4);
  R.assertRules(rows,1,{labels,renderer});
  for(const e of typography.evidence){R.metricShape(e,{labels,ruleId:typography.id});assert.ok(Math.abs(e.measured-expected[e.metric])<1e-12);assert.equal(typeof e.expected,'string');}
  assert.throws(()=>R.assertRules(rows,1,{labels,renderer:rendererV1}));
});
test('typography oracle refuses unknown definitions, wrong units, numeric thresholds and forged provenance',()=>{
  const v=vector('producer-typography-v2.json'),labels=v.request.payload.annotations;
  const e=v.response.payload.results.find(r=>r.ruleId==='KNU-TYPOGRAPHY-001').evidence[0];
  for(const mutate of [e=>e.metric='font-height',e=>e.expected=1.8,e=>e.expected='at least1.8mm',e=>e.method='font-metadata',e=>e.unit='pt',
    e=>e.measured=-1,e=>e.measured=10000,e=>e.measured='3.3',e=>e.uncertainty.absoluteError=0,e=>e.uncertainty.absoluteError=-1,
    e=>e.uncertainty.method='guideline-tolerance',e=>e.uncertainty.limitations.pop(),e=>e.roleProvenance.annotationId=999,
    e=>e.roleProvenance.provenance='validatedNativeClassification',e=>e.roleProvenance.certainty='proven',e=>e.characterStart=0,
    e=>e.sourceLocation='guessed.tex:1',e=>e.classification.characterCount=100000,e=>e.visualBold=true]){
    const bad=structuredClone(e);mutate(bad);assert.throws(()=>R.metricShape(bad,{labels,ruleId:'KNU-TYPOGRAPHY-001'}));
  }
  const baseline=v.response.payload.results.find(r=>r.ruleId==='KNU-TYPOGRAPHY-001').evidence.find(e=>e.metric==='baseline-distance-min');
  const wrong=structuredClone(baseline);wrong.measured++;assert.throws(()=>R.metricShape(wrong,{labels,ruleId:'KNU-TYPOGRAPHY-001'}));
  for(const mutate of [r=>r.outcome='pass',r=>r.outcome='fail',r=>r.reason='normatively-compliant',r=>r.evidence.pop(),
    r=>r.evidence.push(structuredClone(r.evidence[0])),r=>r.evidence[0].measured=r.evidence[1].measured+1,
    r=>r.evidence.forEach(e=>{e.classification.lineCount=1;e.classification.continuationBaselineRaw=null;})]){
    const rows=producerRows(v),row=rows.find(r=>r.id==='KNU-TYPOGRAPHY-001');mutate(row);assert.throws(()=>R.assertRules(rows,1,{labels,renderer}));
  }
  const forged=structuredClone(labels);forged[0].provenance='validatedNativeClassification';
  const changed=structuredClone(e);changed.roleProvenance.provenance='validatedNativeClassification';
  assert.throws(()=>R.metricShape(changed,{labels:forged,ruleId:'KNU-TYPOGRAPHY-001'}),'tooling never reclassifies author gestures as machine proof');
});

test('report identity follows the actually admitted pair for v1 and v2 instead of a global default',()=>{
  const authority={request:'a'.repeat(32),epoch:3,capture:'11111111-2222-3333-4444-555555555555',digest:'b'.repeat(64)},hash='c'.repeat(64);
  for(const admitted of [renderer,rendererV1]){
    const identity=`Compile request: ${authority.request}; epoch: ${authority.epoch}; capture: ${authority.capture}. Input digest: ${authority.digest}. PDF SHA256: ${hash}; profile: knu-csc-2025-rendered-review 1.0.0. Inspector loader SHA256: ${admitted.loader}; WASM SHA256: ${admitted.wasm}.`;
    assert.equal(R.reportIdentity(identity,hash,authority,admitted).wasm,admitted.wasm);
    assert.throws(()=>R.reportIdentity(identity,hash,authority,admitted===renderer?rendererV1:renderer));
    assert.throws(()=>R.reportIdentity(identity,hash,{...authority,request:'0'.repeat(32)},admitted));
    assert.throws(()=>R.reportIdentity(identity,'0'.repeat(64),authority,admitted));
  }
  assert.throws(()=>R.preflight({caseName:'plain',bundled:true,downloads:true,resultDirectory:'/evidence',runtime:R.closedIdentity(P15)}));
});
