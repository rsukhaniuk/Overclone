// Opt-in real registered UI observations. No response interception, runtime
// substitution, storage writes, worker messages or alternate compilation server.
const assert = require('node:assert/strict');
const {createHash} = require('node:crypto');
const H = require('./review-ui-helpers.cjs');
const sha = value => createHash('sha256').update(value).digest('hex');
const CLOSED = [
  {profile:13,count:4357,totalBytes:259559615,closure:'34540765dfd1b5d46666841378221cce0b40204014c4a8c2dc25ddddbe56d1ed',name:'local-compiler-biber-v1'},
  {profile:14,count:4453,totalBytes:261154796,closure:'f0c9ff2492565b273130a4596ea608d646728cacf5dd73294c88e0d03c6e86a6',name:'local-compiler-thesis-v1'},
  {profile:15,count:4453,totalBytes:261178860,closure:'e20e5f5e14f2088d4c8e6718566824b9ce0911120be519386f09709f292f5e03',name:'local-compiler-origins-v1'},
  {profile:16,count:4557,totalBytes:262320783,closure:'8e3fcc0c3255613609a423a5440934b261feee32a5cc73510f4633648b4a135e',name:'local-compiler-input-budget-v1'},
  {profile:17,count:5106,totalBytes:266486260,closure:'b646803e08bc431d358c3ffe66718218b544880d28497020af1570595d921705',name:'private-r1-pdfa-official-ec-tc'},
].map(value => Object.freeze({...value,identity:sha(Buffer.from('native-runtime-profile-v1\n'+value.name+'\n'+value.closure))}));
const RENDERER = Object.freeze({loader:'968aae4935523f528e2314c4dbd831b2dd326ac8d3de3444fd033b73463b0225',wasm:'8b715ed561ec644757f305ddad0a99ae728c6705f9868e96ccb53c8c9143c4b3',loaderBytes:14103,wasmBytes:3716447,schemaVersion:2});
const V1_RENDERER = Object.freeze({loader:RENDERER.loader,wasm:'d1623f880b530b3a6503436d43bcd05d01f6ff8fc8a40c782ba848cd209cb217',loaderBytes:14103,wasmBytes:3699309,schemaVersion:1});
const OBJECT_LAYOUT_RENDERER = Object.freeze({loader:'f95ae827b12703999ce1daafebbf6830f5f4d2c14bb7805a8f47abe54772b079',wasm:'2e2126ead8e00ef7c79d21e3836d66957b19e827523f7c054ca887f3c11ae5b7',loaderBytes:14175,wasmBytes:3725577,schemaVersion:2});
const LARGE_INPUT_RENDERER = Object.freeze({loader:'ea2cebb93f554788751e58554b6e16a094ee6d0883774cb697cbb4a5ce8ec38c',wasm:'b1630e6501e7715b4e0870a059f4e9b8b5acc4c96aa2af0e4ecacb04071622d7',loaderBytes:14243,wasmBytes:3725611,schemaVersion:2});
const TYPOGRAPHY = ['letter-ink-height-min','letter-ink-height-max','baseline-distance-min','baseline-distance-max'];
const HEIGHT_EXPECTED = 'Font height is undefined in section 3.1; letter ink height is an observation only';
const BASELINE_EXPECTED = '1.5 line spacing has no baseline-distance definition in section 3.1';
const METRIC_GAPS = ['metric-horizontal-frame-required','metric-classification-quota','metric-no-measured-glyphs','metric-incomplete-glyph-coverage','metric-ambiguous-line-order','metric-ambiguous-columns','metric-painted-whitespace-ambiguous','metric-continuation-not-established','metric-evidence-quota','metric-role-scope-overlap'];
const IDS = ['KNU-PAPER-001','KNU-MARGINS-001','KNU-TYPOGRAPHY-001','KNU-PARAGRAPH-RENDERED-001','KNU-HEADINGS-RENDERED-001','KNU-PAGINATION-001','KNU-STRUCTURE-001','KNU-ABSTRACT-001','KNU-LENGTH-001','KNU-REFERENCES-001','KNU-APPENDIX-001','KNU-CAPTION-RENDERED-001','KNU-BIBLIOGRAPHY-RENDERED-001'];
const WAIT = 130000;
function enabled(value) { assert.ok(value === undefined || ['', '0', '1'].includes(value), 'RENDERED_REVIEW_CHECKS must be absent,0 or1'); return value === '1'; }
function closedIdentity(value) {
  const found = CLOSED.find(row => row.count === value.count && row.totalBytes === value.totalBytes && row.closure === value.closure);
  return found ? {...found} : null;
}
function identityProfile(identity) { const found=CLOSED.find(row=>row.identity===identity); assert.ok(found,'only exact P13/P14/P15/P16/P17 runtime identities are admitted');return found.profile; }
function classifyRuntime(pins) {
  assert.ok(Array.isArray(pins) && pins.length <= 8192);
  const ordered=pins.slice().sort((a,b)=>a.name<b.name?-1:a.name>b.name?1:0);
  return closedIdentity({count:pins.length,totalBytes:pins.reduce((n,p)=>n+p.length,0),
    closure:sha(Buffer.from(ordered.map(p=>`${p.name}\t${p.length}\t${p.sha256}`).join('\n')))});
}
function preflight({caseName,bundled,downloads,resultDirectory,runtime,renderer}) {
  assert.ok(['plain','portable'].includes(caseName),'rendered review currently targets unchanged plain/portable fixtures');
  assert.equal(bundled,true,'rendered review requires the registered bundled runtime');
  assert.equal(downloads,true,'rendered review requires the existing final stale-source/download flow');
  assert.ok(resultDirectory,'retain the existing acceptance receipt directory');
  const closed=runtime&&closedIdentity(runtime);
  assert.ok(closed && [14,15,16,17].includes(closed.profile) && runtime.profile === closed.profile && runtime.identity === closed.identity,'exact closed P14/P15/P16/P17 runtime required');
  const admitted=admittedRenderer(renderer);
  return {enabled:true,profile:closed.profile,runtimeIdentity:runtime.identity,renderer:{...admitted},caseName,
    scope:'real registered PDF/Rust rendered observations; partial standards coverage; no overall compliance claim'};
}
function paperObservation(evidence) {
  assert.ok(evidence && Number.isInteger(evidence.pageIndex) && evidence.pageIndex>=0);
  assert.equal(evidence.method,'native-page-box');assert.equal(evidence.unit,'mm');
  assert.equal(evidence.characterStart,null);assert.equal(evidence.characterEnd,null);assert.equal(evidence.sourceLocation,null);assert.equal(evidence.roleProvenance,null);
  assert.ok(Array.isArray(evidence.rawBox) && evidence.rawBox.length===4 && evidence.rawBox.every(Number.isFinite));
  assert.equal(evidence.uncertainty?.method,'pdfium-float32-roundoff-bound');
  assert.deepEqual(evidence.uncertainty.limitations,['nominal-paper-tolerance-unspecified']);
  assert.equal(evidence.expected,'A4 210 x 297; A3 297 x 420 only for necessary illustrations');
  const [left,bottom,right,top]=evidence.rawBox;assert.ok(right>left&&top>bottom);
  // These unchanged TeX fixtures use default UserUnit=1. This independently
  // checks the native physical observation, not a new paper-size tolerance.
  const width=(right-left)*25.4/72,height=(top-bottom)*25.4/72;
  const error=(a,b)=>(Math.abs(a)+Math.abs(b))*2**-23*25.4/72+Number.EPSILON*1000;
  const ew=error(left,right),eh=error(bottom,top);
  assert.ok(Math.abs(evidence.uncertainty.absoluteError-Math.max(ew,eh))<1e-10,'reported roundoff bound matches default-UserUnit fixture');
  const actual=/^(\d+\.\d{9}) x (\d+\.\d{9})$/.exec(evidence.measured);assert.ok(actual,'bounded numeric native dimensions');
  assert.ok(Math.abs(Number(actual[1])-width)<=6e-10&&Math.abs(Number(actual[2])-height)<=6e-10);
  const a4=Math.abs(width-210)<=ew&&Math.abs(height-297)<=eh;
  return {pageIndex:evidence.pageIndex,widthMm:width,heightMm:height,roundoffWidth:ew,roundoffHeight:eh,
    expectedOutcome:a4?'pass':'indeterminate',manufacturingToleranceSupplied:false};
}
function keys(value,fields) { assert.ok(value&&typeof value==='object'&&!Array.isArray(value));assert.deepEqual(Object.keys(value).sort(),fields.slice().sort()); }
function finite(value,bound=1e12) { assert.ok(Number.isFinite(value)&&Math.abs(value)<=bound); }
function box(value) { assert.ok(Array.isArray(value)&&value.length===4);value.forEach(n=>finite(n,1e6));assert.ok(value[0]<=value[2]&&value[1]<=value[3]); }
function admittedRenderer(renderer) {
  // Exact reviewed tuples are the authority. Optional caller schema/version
  // hints cannot upgrade corrected v1 or admit known bad/private prototypes.
  const found=[RENDERER,V1_RENDERER,OBJECT_LAYOUT_RENDERER,LARGE_INPUT_RENDERER].find(row=>renderer&&row.loader===renderer.loader&&row.wasm===renderer.wasm);
  assert.ok(found,'exact reviewed renderer pair required');
  for(const field of ['loaderBytes','wasmBytes'])if(renderer[field]!==undefined)assert.equal(renderer[field],found[field]);
  return found;
}
function rendererVersion(renderer=RENDERER) { return admittedRenderer(renderer).schemaVersion; }
function roleBinding(e,labels) {
  keys(e.roleProvenance,['annotationId','role','provenance','certainty']);
  const matches=labels.filter(label=>label.annotationId===e.roleProvenance.annotationId);assert.equal(matches.length,1);
  const a=matches[0];assert.equal(a.pageIndex,e.pageIndex);assert.equal(a.characterStart,e.characterStart);assert.equal(a.characterEnd,e.characterEnd);
  for(const field of ['role','provenance','certainty'])assert.equal(e.roleProvenance[field],a[field]);
  assert.equal(a.provenance,'authorLabel');assert.ok(['tentative','confirmedObservation'].includes(a.certainty));assert.equal(e.sourceLocation,null);return a;
}
function metricShape(e,{labels,paperBox,ruleId}={}) {
  keys(e,['pageIndex','characterStart','characterEnd','rawBox','measured','expected','unit','method','metric','classification','uncertainty','roleProvenance','sourceLocation']);
  const a=roleBinding(e,labels);box(e.rawBox);finite(e.measured);assert.equal(e.unit,'mm');
  const typography=ruleId==='KNU-TYPOGRAPHY-001'||ruleId===undefined&&TYPOGRAPHY.includes(e.metric);
  if(typography){
    assert.ok(TYPOGRAPHY.includes(e.metric)&&['body','paragraph'].includes(a.role));assert.ok(e.measured>0);
    const height=e.metric.startsWith('letter-ink-height-');
    assert.equal(e.method,height?'native-glyph-ink-height':'classified-baseline-distance');
    assert.equal(e.expected,height?HEIGHT_EXPECTED:BASELINE_EXPECTED);
    assert.ok(e.rawBox[0]<e.rawBox[2]&&e.rawBox[1]<e.rawBox[3]);
  }else{
    finite(e.expected);assert.ok(ruleId===undefined||['KNU-MARGINS-001','KNU-PARAGRAPH-RENDERED-001'].includes(ruleId));
    assert.equal(e.method,a.role==='paragraph'?'classified-paragraph-start':'native-glyph-ink');
    assert.ok(a.role==='paragraph'?e.metric==='paragraph-indent':a.role==='body'&&['margin-left','margin-top','margin-right','margin-bottom'].includes(e.metric));
    if(ruleId)assert.equal(ruleId,a.role==='paragraph'?'KNU-PARAGRAPH-RENDERED-001':'KNU-MARGINS-001');
    assert.equal(e.expected,{'paragraph-indent':12.7,'margin-left':25,'margin-top':20,'margin-right':10,'margin-bottom':20}[e.metric]);
  }
  keys(e.classification,['lineCount','characterCount','firstBaselineRaw','continuationBaselineRaw']);
  const c=e.classification;assert.ok(Number.isInteger(c.lineCount)&&c.lineCount>=1&&c.lineCount<=256);assert.ok(Number.isInteger(c.characterCount)&&c.characterCount>=c.lineCount&&c.characterCount<=8192);
  finite(c.firstBaselineRaw,1e6);if(c.lineCount===1)assert.equal(c.continuationBaselineRaw,null);else{finite(c.continuationBaselineRaw,1e6);assert.ok(c.continuationBaselineRaw<c.firstBaselineRaw);}
  keys(e.uncertainty,['absoluteError','method','limitations']);finite(e.uncertainty.absoluteError);assert.ok(typography?e.uncertainty.absoluteError>0:e.uncertainty.absoluteError>=0);
  assert.equal(e.uncertainty.method,'native-outline-and-float32-bound');
  assert.deepEqual(e.uncertainty.limitations,typography?['conditional-role-scope-only','extracted-unicode-not-visual-glyph-proof','ink-height-not-font-height','baseline-distance-not-leading-definition']:['conditional-role-scope-only','other-page-paint-not-established','ink-extents-not-layout-box']);
  if(typography){
    if(e.metric.startsWith('baseline-distance-')){
      assert.ok(c.lineCount>=2);
      if(c.lineCount===2)assert.ok(Math.abs(e.measured-(c.firstBaselineRaw-c.continuationBaselineRaw)*25.4/72)<=e.uncertainty.absoluteError,'two-line default-UserUnit baseline distance matches native origins');
    }else assert.ok(e.measured<=(e.rawBox[3]-e.rawBox[1])*25.4/72+e.uncertainty.absoluteError,'letter ink fits the measured raw scope');
  }
  if(paperBox&&a.role==='body'&&!typography){
    const b=e.rawBox,m=paperBox;const raw={'margin-left':b[0]-m[0],'margin-top':m[3]-b[3],'margin-right':m[2]-b[2],'margin-bottom':b[1]-m[1]}[e.metric];
    assert.ok(Math.abs(e.measured-raw*25.4/72)<=e.uncertainty.absoluteError,'default-UserUnit fixture margin matches actual ink/media boxes within measurement error');
  }
  return {metric:e.metric,pageIndex:e.pageIndex,measured:e.measured,absoluteError:e.uncertainty.absoluteError,annotationId:a.annotationId};
}
function typographyPairs(evidence) {
  const groups=new Map();
  for(const e of evidence){const key=e.pageIndex+':'+e.roleProvenance.annotationId;let group=groups.get(key);if(!group)groups.set(key,group=new Map());assert.ok(!group.has(e.metric),'duplicate typography metric');group.set(e.metric,e);}
  for(const group of groups.values()){
    assert.ok([2,4].includes(group.size));
    for(const prefix of ['letter-ink-height','baseline-distance']){
      const low=group.get(prefix+'-min'),high=group.get(prefix+'-max');assert.equal(!!low,!!high,'both extrema are required');
      if(low){assert.ok(low.measured<=high.measured);assert.deepEqual(low.classification,high.classification);assert.deepEqual(low.rawBox,high.rawBox);}
    }
  }
}
function roleObservation(e,{labels,renderer=RENDERER}={}) {
  keys(e,['pageIndex','characterStart','characterEnd','rawBox','measured','expected','unit','method','uncertainty','roleProvenance','sourceLocation','observation']);
  const a=roleBinding(e,labels);assert.equal(e.method,'annotation-scoped-native-text');assert.equal(e.unit,'text');
  for(const field of ['measured','expected'])assert.ok(typeof e[field]==='string'&&Buffer.byteLength(e[field],'utf8')>0&&Buffer.byteLength(e[field],'utf8')<=256&&!e[field].includes('\0')&&!e[field].includes('\ufffd'));
  keys(e.uncertainty,['absoluteError','method','limitations']);assert.equal(e.uncertainty.absoluteError,null);assert.equal(e.uncertainty.method,'native-character-unicode');
  assert.deepEqual(e.uncertainty.limitations,['conditional-role-scope-only','extracted-text-not-visual-glyph-proof','role-and-document-order-not-established','page-centre-not-text-layout-centre']);
  const o=e.observation;keys(o,['kind','textOrder','unicodeCase','asciiDigitValue','oneBasedPdfPage','captionPrefix','finalFullStop','placement','placementReason','relativeOrder']);
  const kind=a.role==='pageNumber'?'page-number':['headingMajor','headingMinor'].includes(a.role)?'heading':['figureCaption','tableCaption'].includes(a.role)?'caption':null;
  assert.equal(o.kind,kind);assert.ok(kind);assert.equal(o.oneBasedPdfPage,e.pageIndex+1);assert.ok(['native-index','validated-horizontal'].includes(o.textOrder));
  assert.ok(['upper','lower','mixed','uncased'].includes(o.unicodeCase));assert.ok(o.asciiDigitValue===null||Number.isSafeInteger(o.asciiDigitValue)&&o.asciiDigitValue>=0);
  assert.ok(['figure','table','none'].includes(o.captionPrefix));assert.equal(typeof o.finalFullStop,'boolean');assert.equal(o.relativeOrder,kind==='caption'?'illustration-geometry-unavailable':null);
  if(o.placement!==null){
    assert.equal(rendererVersion(renderer),2,'measured role placement requires a root-adopted exact ink-v2 renderer pair');
    keys(o.placement,['media','inkBox','topClearanceMm','rightClearanceMm','pageCentreOffsetMm','absoluteErrorMm','lineCount']);
    const p=o.placement;box(p.media);box(p.inkBox);assert.deepEqual(e.rawBox,p.inkBox);assert.equal(o.textOrder,'validated-horizontal');assert.equal(o.placementReason,'native-outline-location');
    for(const field of ['topClearanceMm','rightClearanceMm','pageCentreOffsetMm','absoluteErrorMm'])finite(p[field]);assert.ok(p.absoluteErrorMm>=0);assert.ok(Number.isInteger(p.lineCount)&&p.lineCount>=1&&p.lineCount<=256);
  } else {
    assert.equal(e.rawBox,null);assert.ok(['glyph-ink-uncertainty-unavailable','physical-scale-unavailable','metric-horizontal-frame-required','metric-classification-quota','metric-no-measured-glyphs','metric-incomplete-glyph-coverage','metric-ambiguous-line-order','metric-ambiguous-columns','metric-painted-whitespace-ambiguous'].includes(o.placementReason));
  }
  return {kind:o.kind,text:e.measured,pageIndex:e.pageIndex,textOrder:o.textOrder,unicodeCase:o.unicodeCase,annotationId:a.annotationId};
}
function assertRules(rows,pages,{labels=[],renderer=RENDERER}={}) {
  assert.deepEqual(rows.map(row=>row.id).sort(),IDS.slice().sort(),'finite13-category report');rendererVersion(renderer);
  const paper=rows.find(row=>row.id==='KNU-PAPER-001');assert.equal(paper.support,'partial');
  assert.equal(paper.evidence.length,pages,'one actual paper observation per inspected page');
  const measured=paper.evidence.map(paperObservation);
  assert.deepEqual(measured.map(row=>row.pageIndex).sort((a,b)=>a-b),Array.from({length:pages},(_,i)=>i));
  assert.equal(paper.outcome,measured.some(row=>row.expectedOutcome==='indeterminate')?'indeterminate':'pass');
  for(const row of rows.filter(row=>row.id!=='KNU-PAPER-001')) {
    for(const e of row.evidence)assert.ok(Number.isInteger(e.pageIndex)&&e.pageIndex>=0&&e.pageIndex<pages);
    const reason=typeof row.reason==='string'?row.reason.replace(/ /g,'-'):null;
    assert.ok(['unavailable','indeterminate','notApplicable'].includes(row.outcome),'no unproved role/typography/global compliance pass or fail');
    if(['KNU-MARGINS-001','KNU-PARAGRAPH-RENDERED-001'].includes(row.id)) {
      if(row.evidence.length){assert.equal(rendererVersion(renderer),2,'ink metrics require an adopted exact v2 renderer');assert.equal(row.outcome,'indeterminate');assert.equal(row.support,'partial');row.evidence.forEach(e=>metricShape(e,{labels,paperBox:paper.evidence.find(p=>p.pageIndex===e.pageIndex)?.rawBox,ruleId:row.id}));}
      else if(rendererVersion(renderer)===1){assert.equal(row.outcome,'unavailable');assert.equal(row.support,'unsupported');}
      else if(row.support==='unsupported'){assert.equal(row.outcome,'unavailable');assert.ok(['glyph-ink-unavailable','glyph-ink-uncertainty-unavailable','physical-scale-unavailable'].includes(reason));}
      else{assert.equal(row.outcome,'indeterminate');assert.equal(row.support,'partial');assert.ok([...METRIC_GAPS,'confirmed-body-boundaries-required','confirmed-paragraph-starts-required'].includes(reason));}
    } else if(row.id==='KNU-TYPOGRAPHY-001'){
      assert.equal(row.outcome,'indeterminate');assert.equal(row.support,'partial');
      if(row.evidence.length){assert.equal(rendererVersion(renderer),2);assert.equal(reason,'conditional-typography-observations');assert.ok(row.evidence.length<=32);row.evidence.forEach(e=>metricShape(e,{labels,ruleId:row.id}));typographyPairs(row.evidence);}
      else if(rendererVersion(renderer)===1)assert.equal(reason,'measurement-definition-unresolved');
      else assert.ok([...METRIC_GAPS,'measurement-definition-unresolved','physical-scale-unavailable'].includes(reason));
    } else if(['KNU-HEADINGS-RENDERED-001','KNU-PAGINATION-001','KNU-CAPTION-RENDERED-001'].includes(row.id)) {
      if(row.evidence.length){assert.equal(row.outcome,'indeterminate');assert.equal(row.support,'partial');row.evidence.forEach(e=>roleObservation(e,{labels,renderer}));}
      else if(row.id==='KNU-HEADINGS-RENDERED-001'){assert.equal(row.outcome,'unavailable');assert.equal(row.support,'unsupported');}
      else{assert.equal(row.outcome,'indeterminate');assert.equal(row.support,'partial');}
    } else {
      assert.equal(row.evidence.length,0,'unassessed categories cannot acquire invented evidence');assert.equal(row.support,'partial');
      assert.equal(row.outcome,['KNU-LENGTH-001','KNU-BIBLIOGRAPHY-RENDERED-001'].includes(row.id)?'notApplicable':'indeterminate');
    }
  }
  assert.ok(rows.some(row=>['unavailable','indeterminate','notApplicable'].includes(row.outcome)),'no fabricated all-green compliance');
  for(const row of rows)assert.equal(row.citation,'https://csc.knu.ua/uk/filer/canonical/1740300611/2680/');
  return measured;
}
function paperBoxFor(rows,page) {return rows.find(row=>row.id==='KNU-PAPER-001').evidence.find(e=>e.pageIndex===page)?.rawBox;}
async function labelSnapshot(page) {
  return H.$(page,'rendered-role-row').evaluateAll(elements=>elements.map(e=>({annotationId:Number(e.dataset.annotationId),role:e.dataset.role,pageIndex:Number(e.dataset.pageIndex),
    characterStart:e.dataset.characterStart===undefined?null:Number(e.dataset.characterStart),characterEnd:e.dataset.characterEnd===undefined?null:Number(e.dataset.characterEnd),
    provenance:e.dataset.provenance,certainty:e.dataset.certainty})));
}
async function compileAuthority(page,expected) {
  const details=await H.$(page,'compile-details').textContent();
  const request=details.match(/Request: ([0-9a-f]{32});/)?.[1],digest=details.match(/Input: ([0-9a-f]{64})/)?.[1],epoch=Number(details.match(/epoch: (\d+)/)?.[1]);
  assert.ok(request&&digest&&Number.isSafeInteger(epoch)&&epoch>0);
  const observations=await page.evaluate(async()=>{
    // Existing cached production module; READ observations only. No init,
    // provision, execute, dispose, Worker construction or manufactured receipt.
    const bridge=await import('./js/compile-port-bridge.js');return bridge.observations();
  });
  const trace=observations.trace.filter(row=>row.tag.request===request);assert.ok(trace.length>=2);
  const capture=trace[0].tag.capture;assert.match(capture,/^[0-9a-f-]{36}$/);
  for(const row of trace){assert.equal(row.tag.profile,expected.profile);assert.equal(row.tag.runtimeIdentity,expected.runtimeIdentity);
    assert.equal(row.tag.request,request);assert.equal(row.tag.digest,digest);assert.equal(row.tag.epoch,epoch);assert.equal(row.tag.capture,capture);
    assert.ok(row.posted&&row.entered&&row.terminated);assert.equal(row.outcome,'native-return');assert.equal(row.nativeReturn,0);}
  return {request,digest,epoch,capture,snapshot:trace[0].tag.snapshot,runtimeIdentity:expected.runtimeIdentity};
}
function reportIdentity(identity,hash,authority,renderer) {
  const admitted=admittedRenderer(renderer);
  assert.ok(identity.includes(`Compile request: ${authority.request}; epoch: ${authority.epoch}; capture: ${authority.capture}`));
  assert.ok(identity.includes(`Input digest: ${authority.digest}`));assert.ok(identity.includes(hash));
  assert.ok(identity.includes(`Inspector loader SHA256: ${admitted.loader}; WASM SHA256: ${admitted.wasm}.`));
  assert.match(identity,/knu-csc-2025-rendered-review 1\.0\.0/);
  return {...admitted};
}
async function readReport(page,hash,authority,pages,renderer) {
  renderer=admittedRenderer(renderer);
  const report=H.$(page,'rendered-review-report');assert.equal(await report.getAttribute('data-current'),'true');
  assert.equal(await report.getAttribute('data-pdf-sha256'),hash);assert.equal(await report.getAttribute('data-complete'),'true');
  const version=Number(await report.getAttribute('data-annotation-version'));assert.ok(Number.isSafeInteger(version)&&version>0);
  const coverage=await H.$(page,'rendered-review-coverage').textContent();assert.match(coverage,new RegExp(`Inspected ${pages} of ${pages} requested pages`));assert.match(coverage,/Overall compliance is not assessed/);
  const identity=await H.$(page,'rendered-review-identity').textContent();
  reportIdentity(identity,hash,authority,renderer);
  const rows=await report.locator('[data-testid=rendered-review-rule]').evaluateAll(elements=>elements.map(element=>({
    id:element.dataset.ruleId,outcome:element.dataset.outcome,support:element.dataset.support,
    citation:element.querySelector('a')?.getAttribute('href'),reason:element.querySelector('[data-testid=rendered-review-reason]')?.textContent?.trim(),
    evidence:Array.from(element.querySelectorAll('[data-testid=rendered-review-measurement] details pre')).map(pre=>JSON.parse(pre.textContent)),
  })));
  assert.ok(pages<=8,'initial plain/portable acceptance must fit the first evidence page; no silently skipped pagination');
  const labels=await labelSnapshot(page);const measurements=assertRules(rows,pages,{labels,renderer});
  assert.equal(Number(await H.$(page,'rendered-role-list').getAttribute('data-annotation-version')),version);
  assert.match(await report.textContent(),/No source fixes, automatic edits or PDF replacement/);
  return {pdfSha256:hash,annotationVersion:version,compile:authority,coverage:{pages,complete:true,standardsComplete:false},measurements,
    roles:rows.flatMap(row=>row.evidence.filter(e=>e.observation).map(e=>roleObservation(e,{labels,renderer}))),
    metrics:rows.flatMap(row=>row.evidence.filter(e=>e.metric).map(e=>metricShape(e,{labels,ruleId:row.id,paperBox:paperBoxFor(rows,e.pageIndex)}))),
    renderer:{...renderer},categories:rows.map(row=>({ruleId:row.id,outcome:row.outcome,supportState:row.support,reason:row.reason}))};
}
function workerCounts(workers) {
  const records=typeof workers==='function'?workers():workers;
  return {pdf:records.filter(row=>/\/__oop\/native-worker\/pdf\b/.test(row.url)).length,
    rust:records.filter(row=>/\/js\/rendered-review-worker\.js$/.test(new URL(row.url).pathname)).length};
}
async function collected(page,project,hash,authority,pages,workers,renderer) {
  const beforeHead=await H.head(page,project),beforeSource=await H.$(page,'editor').inputValue(),beforeWorkers=workerCounts(workers);
  assert.equal(await H.$(page,'rendered-review-check').isEnabled(),true);
  await H.$(page,'rendered-review-check').click();
  await page.waitForFunction(()=>document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy==='false'&&document.querySelector('[data-testid=rendered-review-report]')?.dataset.current==='true',null,{timeout:WAIT});
  const report=await readReport(page,hash,authority,pages,renderer),afterWorkers=workerCounts(workers);
  assert.ok(afterWorkers.pdf-beforeWorkers.pdf>=pages,'real native PDF workers during explicit observation');
  assert.ok(afterWorkers.rust-beforeWorkers.rust>=pages,'real dedicated Rust WASM workers during explicit observation');
  assert.deepEqual(await H.head(page,project),beforeHead,'review never saves or mutates source');assert.equal(await H.$(page,'editor').inputValue(),beforeSource);
  assert.equal(await H.$(page,'compile-panel').getAttribute('data-output-hash'),hash);assert.equal(await H.$(page,'compile-panel').getAttribute('data-is-current'),'true');
  return {...report,workers:{nativePdf:afterWorkers.pdf-beforeWorkers.pdf,rust:afterWorkers.rust-beforeWorkers.rust},sourceAndHeadUnchanged:true};
}
async function staleReport(page) {
  await page.waitForFunction(()=>document.querySelector('[data-testid=rendered-review-report]')?.dataset.current==='false',null,{timeout:10000});
  await H.$(page,'rendered-review-stale').waitFor();
}
async function searchLabel(page,{project,hash,authority,pages,workers,previewReady,renderer}) {
  async function selection(action){
    const generation=await H.$(page,'pdf-pane').getAttribute('data-generation');await action();
    await page.waitForFunction(old=>document.querySelector('[data-testid=pdf-pane]')?.dataset.generation!==old,generation,{timeout:10000});
    if(previewReady)await previewReady();else await page.waitForFunction(()=>document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus==='Available',null,{timeout:WAIT});
  }
  if(!/^Page 1 of /.test(await H.$(page,'page-label').textContent()))await selection(async()=>{const input=page.locator('input[aria-label="Go to page"]');await input.fill('1');await input.press('Tab');});
  const text=await H.$(page,'page-text').textContent(),term=text.match(/[\p{L}]{3,24}/u)?.[0];assert.ok(term,'real extracted page text must contain a searchable literal');
  const input=H.$(page,'pdf-pane').locator('input[aria-label="Search PDF"]');
  await H.reveal(page, input);
  async function find(query){await input.fill(query);await selection(()=>page.getByRole('button',{name:'Search document',exact:true}).click());}
  await find(term);assert.ok(Number(await H.$(page,'search-status').getAttribute('data-match-count'))>0);
  await selection(()=>page.getByRole('button',{name:'Next result',exact:true}).click());
  await H.reveal(page,H.$(page,'rendered-role-use-search'));await H.$(page,'rendered-role-use-search').click();
  const draft=H.$(page,'rendered-role-search-draft');assert.equal(await draft.getAttribute('data-current'),'true');
  assert.equal(await draft.getAttribute('data-pdf-sha256'),hash);assert.equal(await draft.getAttribute('data-compile-request'),authority.request);
  assert.equal(await draft.getAttribute('data-generation'),await H.$(page,'pdf-pane').getAttribute('data-generation'));
  const before=await labelSnapshot(page);await find(term+'_absent');
  assert.equal(await draft.getAttribute('data-current'),'false');await H.$(page,'rendered-role-search-stale').waitFor();
  assert.equal(await H.$(page,'rendered-role-add').isDisabled(),true);assert.deepEqual(await labelSnapshot(page),before,'stale search draft never confirms a label');
  await find(term);await selection(()=>page.getByRole('button',{name:'Next result',exact:true}).click());
  await H.$(page,'rendered-role').selectOption('headingMajor');await H.$(page,'rendered-role-certainty').selectOption('tentative');
  await H.$(page,'rendered-role-use-search').click();
  const selected={pageIndex:Number(await draft.getAttribute('data-page-index')),start:Number(await draft.getAttribute('data-character-start')),
    end:Number(await draft.getAttribute('data-character-end')),text:await draft.getAttribute('data-glyph-text')};
  assert.equal(selected.pageIndex,0);assert.ok(Number.isInteger(selected.start)&&selected.start>=0&&selected.end>selected.start&&selected.end<=100000);
  assert.ok(Buffer.byteLength(selected.text,'utf8')<=256&&!selected.text.endsWith('…'));
  await H.$(page,'rendered-role-add').click();
  const labels=await labelSnapshot(page),label=labels.find(a=>a.role==='headingMajor');assert.ok(label);
  assert.equal(label.characterStart,selected.start);assert.equal(label.characterEnd,selected.end);assert.equal(label.pageIndex,selected.pageIndex);
  assert.equal(label.provenance,'authorLabel');assert.equal(label.certainty,'tentative');
  const observed=await collected(page,project,hash,authority,pages,workers,renderer);
  const role=observed.roles.find(r=>r.annotationId===label.annotationId);assert.ok(role,'actual Rust literal heading evidence for confirmed selected native range');
  assert.equal(role.text,selected.text);assert.equal(role.kind,'heading');if(rendererVersion(renderer)===1)assert.equal(role.textOrder,'native-index','corrected v1 cannot claim ink alignment');else assert.ok(['native-index','validated-horizontal'].includes(role.textOrder));
  await page.getByRole('button',{name:'Remove Major heading label on page 1',exact:true}).click();
  await H.$(page,'rendered-role-scope').selectOption('page');await H.$(page,'rendered-role-discard').click();
  return {selected,observation:observed,staleSearchRefused:true,explicitAuthorLabel:true,visualCaseProof:false,selectedLabelRemovedAfterReadOnlyCheck:true};
}
async function verify(page,{project,hash,pages,workers,expected,offline,recompile,previewReady}) {
  assert.ok(Number.isInteger(pages)&&pages>=1&&pages<=8);
  const renderer=admittedRenderer(expected.renderer);
  const before=await H.head(page,project),source=await H.$(page,'editor').inputValue();
  await H.reveal(page,H.$(page,'rendered-review-check'));await H.$(page,'rendered-review-check').waitFor();
  assert.equal(await H.$(page,'rendered-role-row').count(),0,'ephemeral labels are empty after a new app/browser session');
  const idleWorkers=workerCounts(workers);assert.equal(idleWorkers.rust,0,'no rendered-review Rust request before gesture');
  await H.$(page,'rendered-review-work-type').selectOption('bachelorQualification');
  await H.reveal(page,H.$(page,'rendered-role-add'));
  await H.$(page,'rendered-role').selectOption('body');await H.$(page,'rendered-role-first').fill('1');await H.$(page,'rendered-role-last').fill('');
  await H.$(page,'rendered-role-certainty').selectOption('confirmedObservation');await H.$(page,'rendered-role-add').click();
  assert.equal(await H.$(page,'rendered-role-row').count(),1);
  const bodyLabel=H.$(page,'rendered-role-row');
  assert.match(await bodyLabel.textContent(),/Page 1: Main body, whole page\.\s*Your label: author-confirmed observation\./);
  assert.equal(await bodyLabel.getAttribute('data-provenance'),'authorLabel');
  assert.equal(await bodyLabel.getAttribute('data-certainty'),'confirmedObservation');
  let authority=await compileAuthority(page,expected);
  const receipt={offline,initial:await collected(page,project,hash,authority,pages,workers,renderer)};
  await H.$(page,'rendered-review-work-type').selectOption('masterQualification');await staleReport(page);
  await H.$(page,'rendered-review-work-type').selectOption('bachelorQualification');await staleReport(page);
  receipt.workTypeAba=await collected(page,project,hash,authority,pages,workers,renderer);
  await H.$(page,'rendered-role').selectOption('illustration');await H.$(page,'rendered-role-certainty').selectOption('tentative');await H.$(page,'rendered-role-add').click();await staleReport(page);
  assert.equal(await H.$(page,'rendered-role-row').count(),2);
  await page.getByRole('button',{name:'Remove Illustration label on page 1',exact:true}).click();await staleReport(page);
  receipt.annotationAba=await collected(page,project,hash,authority,pages,workers,renderer);
  assert.ok(receipt.annotationAba.annotationVersion>receipt.initial.annotationVersion);
  receipt.selectedPdfSearchLabel=await searchLabel(page,{project,hash,authority,pages,workers,previewReady,renderer});
  if(recompile){
    const next=await recompile();assert.equal(await H.$(page,'compile-panel').getAttribute('data-is-current'),'true');
    await staleReport(page);await H.$(page,'rendered-role-stale').waitFor();assert.equal(await H.$(page,'rendered-review-check').isDisabled(),true);
    await H.$(page,'rendered-role-confirm').click();
    const nextAuthority=await compileAuthority(page,expected);assert.notEqual(nextAuthority.request,authority.request);assert.notEqual(nextAuthority.capture,authority.capture);
    receipt.sameSourceRecompile=await collected(page,project,next.hash,nextAuthority,pages,workers,renderer);
    hash=next.hash;authority=nextAuthority;
  }
  // Real race: no held or fabricated response. Force only bypasses Playwright's
  // actionability wait; the browser still ignores clicks on disabled controls.
  await H.$(page,'rendered-review-check').click();await H.$(page,'rendered-review-cancel').click({force:true});
  await page.waitForFunction(()=>document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy==='false',null,{timeout:WAIT});
  const notice=await H.$(page,'rendered-review-status').textContent();
  if(/cancelled/i.test(notice)){await staleReport(page);receipt.cancellation={outcome:'cancelled',actualGesture:true,physicalSettlementObserved:true};}
  else{assert.match(notice,/Measured selected pages/);await readReport(page,hash,authority,pages,renderer);receipt.cancellation={outcome:'completed-before-cancel-delivery',actualGesture:true,cancellationAcceptance:'not-established-by-this-race'};}
  receipt.final=await collected(page,project,hash,authority,pages,workers,renderer);
  await H.$(page,'rendered-review-rule').filter({has:page.locator('[data-testid=rendered-review-measurement]')})
    .getByRole('button',{name:'Show page 1 in PDF preview',exact:true}).click();
  if(previewReady)await previewReady();
  assert.equal(await H.$(page,'pdf-pane').getAttribute('data-document'),hash);
  assert.match(await H.$(page,'page-label').textContent(),/^Page 1 of /);
  receipt.measuredPagePreviewNavigation={pageIndex:0,pdfSha256:hash,actualUiAction:true};
  assert.deepEqual(await H.head(page,project),before);assert.equal(await H.$(page,'editor').inputValue(),source);
  return {...receipt,sourceAndHeadUnchanged:true};
}
async function sourceStale(page,{hash,workers}) {
  const before=workerCounts(workers);await staleReport(page);await H.$(page,'rendered-role-stale').waitFor();
  assert.equal(await H.$(page,'rendered-review-check').isDisabled(),true);assert.equal(await H.$(page,'rendered-role-confirm').isDisabled(),true);
  assert.equal(await H.$(page,'rendered-review-report').getAttribute('data-pdf-sha256'),hash);
  assert.equal(await H.$(page,'compile-panel').getAttribute('data-is-current'),'false');
  assert.equal(workerCounts(workers).rust,before.rust,'source edit never triggers background rendered evaluation');
  return {pdfSha256:hash,current:false,sourceChangeInvalidatedReview:true,reconfirmationDisabledUntilFreshCompile:true,noBackgroundRenderedEvaluation:true};
}
module.exports={enabled,closedIdentity,identityProfile,classifyRuntime,preflight,paperObservation,rendererVersion,reportIdentity,metricShape,roleObservation,assertRules,verify,sourceStale};
