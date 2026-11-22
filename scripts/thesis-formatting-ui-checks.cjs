// Real published source profile 1.4 through existing UI/Rust worker; synthetic
// document inputs only. No result interception, compiler or provider dependency.
const assert = require("node:assert/strict"), H = require("./review-ui-helpers.cjs");
const { $, reveal } = H;
const ID = "knu-csc-2025-source@1.4.0";
const source = "\\documentclass[letterpaper]{article}\r\n\\setlength{\\parindent}{1.27cm}\r\n\\geometry{left=25mm,right=10mm,top=20mm,bottom=20mm}\r\n\\linespread{1.2}\r\n\\begin{document}\r\n\\begin{figure}\\caption{Київ 😀.}\\end{figure}\r\n\\end{document}\r\n";
const bytes = Buffer.concat([Buffer.from([239,187,191]),Buffer.from(source)]);
const rules = ["KNU-SRC-PAPER-001","KNU-SRC-LINE-SPACING-001","KNU-SRC-CAPTION-DOT-001"];
async function create(page) {
  const name = "ThesisFormatting-"+Date.now();
  await reveal(page,page.locator("#new-project"));
  await page.locator("#new-project").fill(name);
  await page.getByRole("button",{name:"Create project",exact:true}).click();
  await $(page,"import-input").setInputFiles({name:"main.tex",mimeType:"text/plain",buffer:bytes});
  await page.locator(".editor-toolbar label",{hasText:"main.tex"}).waitFor();
  await H.save(page); return name;
}
async function check(page, expected) {
  await H.analyze(page);
  assert.equal(await H.knuReportVersion(page), "1.4.0");
  for (const id of rules) {
    const rule = page.locator(`[data-testid="knu-rule"][data-rule-id="${id}"]`);
    assert.equal(await rule.getAttribute("data-outcome"),expected);
    assert.match(await rule.locator('[data-testid="knu-citation"]').textContent(),id.includes("CAPTION") ? /3\.7; 3\.8.*21, 22, 23/ : /3\.1.*18/);
  }
  assert.equal(await $(page,"preview-fix").count(),0,"profile1.4 supplies no unreviewed fixes");
  assert.match(await $(page,"knu-scope").textContent(),/Source observations only/);
}
async function exercise(page,name,phase) {
  const original=await H.head(page,name);
  assert.deepEqual(original.files.map(f=>({path:f.path,bytes:f.bytes})),[{path:"main.tex",bytes:phase === "online" ? [...bytes] : [239,187,191,...Buffer.from(source.replace(/\r\n/g,"\n"))]}]);
  await reveal(page,$(page,"analysis-profile"));
  await H.selectAnalysisProfile(page, ID);
  await $(page,"analysis-work-type").selectOption("bachelorQualification");
  await check(page,"Fail");
  const caption=page.locator('[data-testid="knu-finding"][data-rule-id="KNU-SRC-CAPTION-DOT-001"]');
  await caption.locator('[data-testid="knu-finding-link"]').click();
  const text=source.replace(/\r\n/g,"\n"), point=text.indexOf("😀.")+"😀".length;
  await page.waitForFunction(point=>{const e=document.querySelector('[data-testid="editor"]');return e.selectionStart===point&&e.selectionEnd===point+1&&e.value.slice(e.selectionStart,e.selectionEnd)===".";},point);
  assert.deepEqual(await H.head(page,name),original,"Unicode finding navigation preserves saved bytes and revision");
  const positive=text.replace("letterpaper","a4paper").replace("1.2}","1.5}").replace("Київ 😀.","Київ 😀");
  await $(page,"editor").fill(positive);await H.save(page);await check(page,"Pass");
  const saved=await H.head(page,name);assert.notEqual(saved.id,original.id);
  assert.deepEqual(saved.files[0].bytes,[239,187,191,...Buffer.from(positive)],"saved source is the explicit user edit with original BOM");
  await $(page,"editor").fill(text);await H.save(page);await check(page,"Fail");
  const restored=await H.head(page,name);assert.notEqual(restored.id,saved.id);
  assert.deepEqual(restored.files[0].bytes,[239,187,191,...Buffer.from(text)]);
  console.log(`PASS thesis formatting ${phase}: actual Rust paper/spacing/caption evidence, cited source, Unicode dot navigation, explicit edits/save/reanalysis; rendered compliance unassessed`);
}
module.exports={create,exercise};
