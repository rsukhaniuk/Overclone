// editor-select.js — the ONLY browser-selection JS for click-to-source (M2a-2b-2b).
// DOM APIs ONLY: it performs final DOM checks, focus and setSelectionRange, and
// returns a bool. ALL encoding, source-mapping, rule/freshness policy stays in
// C# (SourceNavigator + SpanMapping). No innerHTML, no document edits, no global
// lookup/debug hooks. The atomic final DOM comparison (data-file-id, value,
// selectionStart/End, substring) happens in the SAME call as focus/
// setSelectionRange, so a DOM-only textarea.value mutation that the page did not
// rerender away cannot be claimed as a successful selection, and identical text
// in another file cannot cause a wrong-file selection (the data-file-id
// attribute is compared too).
//
// Exports:
//   readValue(element) -> string|null   read the live textarea value (defense)
//   trySelect(element, expectedFileId, expectedNormalizedText, start, end) -> bool
//     atomic: verify element is the right file's textarea AND its live value
//     equals the C#-computed normalized captured SourceText, then focus +
//     setSelectionRange, then re-verify selectionStart/End and the substring.
//     Returns true ONLY when the final DOM selection equals the expected range.

export function readValue(element) {
  if (!element || element.tagName !== "TEXTAREA" || !element.isConnected)
    return null;
  return element.value;
}

export function trySelect(
  element,
  expectedFileId,
  expectedNormalizedText,
  start,
  end,
) {
  if (!element || element.tagName !== "TEXTAREA") return false;
  // Reject a detached (removed from the document) or disabled textarea BEFORE
  // any focus/range mutation: a late navigation callback must not activate a
  // recycled/dead DOM node.
  if (!element.isConnected) return false;
  if (element.disabled) return false;
  // Wrong-file guard: identical text in another file cannot select here.
  if (element.getAttribute("data-file-id") !== expectedFileId) return false;
  // DOM-only mismatch guard: the live value must equal the C#-computed normalized
  // captured SourceText. If a test mutated textarea.value without firing input
  // (so WorkingFile.Content is unchanged), this fails BEFORE we touch the
  // selection — we do not rerender the mismatch away.
  if (element.value !== expectedNormalizedText) return false;
  // Require integer offsets: the prior `typeof !== "number"` check admitted
  // fractional and NaN values (NaN passes typeof === "number" and all NaN
  // comparisons are false), which could then mutate the selection before
  // returning false. Number.isInteger rejects NaN, Infinity and fractions.
  if (!Number.isInteger(start) || !Number.isInteger(end)) return false;
  if (start < 0 || end < 0 || start > end || end > element.value.length)
    return false;
  try {
    element.focus();
    element.setSelectionRange(start, end);
  } catch {
    return false;
  }
  // Atomic final DOM verification in the SAME call as the selection.
  if (element.selectionStart !== start || element.selectionEnd !== end)
    return false;
  if (
    element.value.substring(start, end) !==
    expectedNormalizedText.substring(start, end)
  )
    return false;
  return true;
}
