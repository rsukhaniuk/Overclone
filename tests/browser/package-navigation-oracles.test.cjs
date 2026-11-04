// Pure synthetic text-oracle tests only. No native/compiler/renderer/browser
// execution and no product acceptance is inferred from these constructed strings.
'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const { assertPackageText } = require('../../scripts/source-pdf-navigation-ui-checks.cjs');

const text = 'Portable compiler fixture. Section 2 is resolved. ' +
  'Hyperlink fixture. Package compatibility entry. Listing marker ' +
  'Included file, reference to Section 1. ' +
  'See section 2. Book citation [1]. References ' +
  '[1] Zoe Zimmer. Portable Compiler Reference. Offline Fixture Press, 2025.';

test('both bibliography fixtures require resolved literal text on every compiler', () => {
  for (const fixture of ['packages', 'biblatex']) {
    for (const compiler of ['local-authoring-v4', 'latex-dvi', 'xelatex', 'lualatex']) {
      const input = ['xelatex', 'lualatex'].includes(compiler) ? 'Привіт, Київ! ' + text : text;
      const evidence = assertPackageText(input.replaceAll(' ', '\r\n '), fixture, compiler);
      assert.equal(evidence.bibliographyBackend, 'BibTeX');
    }
  }
});

test('correct titles and author cannot conceal wrong counters, keys or bibliography', () => {
  const corruptions = [
    ['Section 2 is resolved.', 'Section 20 is resolved.'],
    ['reference to Section 1.', 'reference to Section ??.'],
    ['See section 2.', 'See section 1.'],
    ['Book citation [1].', 'Book citation [book].'],
    ['[1] Zoe Zimmer.', '[2] Zoe Zimmer.'],
    ['Offline Fixture Press, 2025.', 'Offline Fixture Press, 2024.'],
  ];
  for (const fixture of ['packages', 'biblatex'])
    for (const [before, after] of corruptions)
      assert.throws(() => assertPackageText(text.replace(before, after), fixture, 'local-authoring-v4'));
});

test('Unicode fixture evidence cannot silently fall back to Latin-only output', () => {
  for (const compiler of ['xelatex', 'lualatex'])
    assert.throws(() => assertPackageText(text, 'packages', compiler));
});
