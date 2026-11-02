// editor-select.js — browser-selection glue for click-to-source and source tools.
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

// No edit decisions or text mutation: C# captures identity/text and validates
// UTF-16/newline boundaries. Read the live selection only for that exact DOM.
export function readSelection(element, expectedFileId, expectedNormalizedText) {
  if (
    !element ||
    element.tagName !== "TEXTAREA" ||
    !element.isConnected ||
    element.getAttribute("data-file-id") !== expectedFileId ||
    element.value !== expectedNormalizedText
  )
    return null;
  return { start: element.selectionStart, end: element.selectionEnd };
}

export function trySelect(
  element,
  expectedFileId,
  expectedNormalizedText,
  start,
  end,
) {
  return selectRange(
    element,
    expectedFileId,
    expectedNormalizedText,
    start,
    end,
    false,
  );
}

// Read-only find can select a disabled textarea without focusing it. Existing
// source navigation still uses trySelect and retains its disabled guard.
export function trySelectForFind(
  element,
  expectedFileId,
  expectedNormalizedText,
  start,
  end,
) {
  return selectRange(
    element,
    expectedFileId,
    expectedNormalizedText,
    start,
    end,
    true,
  );
}

function selectRange(
  element,
  expectedFileId,
  expectedNormalizedText,
  start,
  end,
  allowDisabled,
) {
  if (!element || element.tagName !== "TEXTAREA") return false;
  // Reject a detached (removed from the document) or disabled textarea BEFORE
  // any focus/range mutation: a late navigation callback must not activate a
  // recycled/dead DOM node.
  if (!element.isConnected) return false;
  if (element.disabled && !allowDisabled) return false;
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
    if (!element.disabled) element.focus();
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

// Highlighting DOM transport ONLY. C# supplies escaped lexical runs. Never
// changes textarea.value, selection, document text, or interprets TeX. The
// native text remains visible until an exact, measured layer is ready.
const highlightControllers = new WeakMap();
export function syncHighlight(root, fileId, revision) {
  if (!root?.isConnected) return false;
  let controller = highlightControllers.get(root);
  if (!controller) {
    const shell = root.parentElement;
    const editor = shell?.querySelector(":scope > textarea#editor");
    const text = root.querySelector(".source-highlight-text");
    const viewport = root.querySelector(".source-highlight-viewport");
    const gutter = root.querySelector(".source-highlight-numbers");
    if (!editor || !text || !viewport || !gutter ||
        typeof ResizeObserver !== "function" || typeof MutationObserver !== "function" ||
        typeof matchMedia !== "function") return false;
    const colors = matchMedia("(forced-colors: active)");
    if (typeof colors.addEventListener !== "function" || typeof colors.removeEventListener !== "function") return false;
    let composing = false, expectedText = "", expectedFile = "", expectedRevision = "";
    const hide = () => { root.dataset.ready = "false"; root.dataset.aligned = "false"; };
    const validate = () => {
      hide(); // fail closed BEFORE anything that might throw
      try {
        if (!root.isConnected || !editor.isConnected || root.dataset.supported !== "true" ||
            root.dataset.fileId !== expectedFile || root.dataset.revision !== expectedRevision ||
            editor.dataset.fileId !== expectedFile || editor.dataset.highlightRevision !== expectedRevision ||
            text.textContent !== expectedText || editor.value !== expectedText) return false;
        const a = getComputedStyle(editor), b = getComputedStyle(text);
        for (const property of ["fontFamily", "fontSize", "fontWeight", "lineHeight", "letterSpacing",
          "tabSize", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "whiteSpace"])
          if (a[property] !== b[property]) return false;
        if (editor.wrap !== "off" || editor.clientWidth <= 0 || editor.clientHeight <= 0) return false;
        viewport.style.width = `${editor.clientWidth}px`;
        viewport.style.height = `${editor.clientHeight}px`;
        text.style.transform = `translate(${-editor.scrollLeft}px, ${-editor.scrollTop}px)`;
        gutter.style.transform = `translateY(${-editor.scrollTop}px)`;
        root.dataset.aligned = "true";
        // IME/native disabled and forced-colors presentation stays plain, with
        // the same geometry and optional gutter; no keyboard interception.
        root.dataset.ready = !composing && !editor.disabled && !colors.matches ? "true" : "false";
        return root.dataset.ready === "true";
      } catch { return false; }
    };
    const beforeInput = () => hide();
    const input = () => hide(); // C# render+sync must explicitly re-authorize
    const compositionStart = () => { composing = true; hide(); };
    const compositionEnd = () => { composing = false; validate(); };
    const events = [["beforeinput", beforeInput], ["input", input],
      ["compositionstart", compositionStart], ["compositionend", compositionEnd],
      ["scroll", validate], ["focus", validate], ["pointerdown", validate]];
    for (const [event, handler] of events) editor.addEventListener(event, handler);
    const resize = new ResizeObserver(validate);
    resize.observe(editor);
    const mutation = new MutationObserver(validate);
    const attributes = ["data-file-id", "data-revision", "data-highlight-revision", "data-supported", "disabled"];
    mutation.observe(root, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: attributes });
    mutation.observe(editor, { attributes: true, attributeFilter: attributes });
    colors.addEventListener("change", validate);
    controller = {
      sync(file, version) {
        expectedFile = file; expectedRevision = version;
        expectedText = text.textContent;
        return validate();
      },
      detach() {
        hide();
        for (const [event, handler] of events) editor.removeEventListener(event, handler);
        resize.disconnect(); mutation.disconnect(); colors.removeEventListener("change", validate);
      },
    };
    highlightControllers.set(root, controller);
  }
  return controller.sync(fileId, revision);
}
export function detachHighlight(root) {
  highlightControllers.get(root)?.detach();
  highlightControllers.delete(root);
}

// DOM event/selection
// transport only. Native sequence catches caret ABA even before a queued .NET
// event callback runs. No document lexing or source/edit policy here.
const assistanceObservers = new WeakMap();
export function inspectAssistanceSelection(element, expectedFileId, expectedNormalizedText) {
  const selection = readSelection(element, expectedFileId, expectedNormalizedText);
  if (!selection) return null;
  let observer = assistanceObservers.get(element);
  if (!observer) {
    observer = { sequence: 0, composing: false, invalid: false, events: [] };
    const changed = event => {
      // Journal owns history-key browser prevention. Key down/up alone do not
      // change document/caret; real input/select/composition still advance epoch.
      if (sourceJournals.has(element) && (event.type === 'keydown' || event.type === 'keyup')) return;
      if ((event.type === 'select' || event.type === 'selectionchange') && sourceJournalOwnsSelection(element)) return;
      if (observer.sequence >= Number.MAX_SAFE_INTEGER) { observer.invalid = true; return; }
      observer.sequence++;
      if (event.type === 'compositionstart') observer.composing = true;
      else if (event.type === 'compositionend') observer.composing = false;
      else if (typeof event.isComposing === 'boolean') observer.composing = event.isComposing;
    };
    for (const name of ['beforeinput', 'input', 'keydown', 'keyup', 'pointerdown', 'pointerup', 'select', 'selectionchange', 'compositionstart', 'compositionend']) {
      element.addEventListener(name, changed);
      observer.events.push([name, changed]);
    }
    assistanceObservers.set(element, observer);
  }
  if (observer.invalid) return null;
  return { start: selection.start, end: selection.end, sequence: observer.sequence, composing: observer.composing };
}
export function detachAssistanceEditor(element) {
  const observer = assistanceObservers.get(element);
  if (observer) for (const [name, handler] of observer.events) element.removeEventListener(name, handler);
  assistanceObservers.delete(element);
}

// Browser event/selection transport; grouping and source mutations stay in C#.
const sourceJournals = new WeakMap();
let sourceJournalBinding = 0;
const journalSource = el => el?.tagName === 'TEXTAREA' && el.isConnected && el.dataset.sourceJournal === 'true';
const journalIdentity = el => ({fileId: el.dataset.fileId ?? '', session: el.dataset.journalSession ?? '', path: el.dataset.journalPath ?? ''});
const journalSnapshot = el => ({...journalIdentity(el), value: el.value, start: el.selectionStart, end: el.selectionEnd});
const sameJournalIdentity = (a, b) => a.fileId === b.fileId && a.session === b.session && a.path === b.path;
// Pure ownership query used by BOTH existing assistance and journal observers.
// An unrelated input/key/pointer/composition or different caret clears the arm.
export function sourceJournalOwnsSelection(element) {
  const s = sourceJournals.get(element), own = s?.owned;
  if (!journalSource(element) || !own || !sameJournalIdentity(own, journalIdentity(element))) return false;
  const range = element.selectionStart === own.start && element.selectionEnd === own.end;
  const renderReset = own.phase === 'render' && element.selectionStart === own.after.length && element.selectionEnd === own.after.length;
  if (element.value === own.after && (range || renderReset)) return true;
  if (element.value !== own.before) s.owned = null;
  return false;
}
export function sourceJournalInputOwnsSelection(element) {
  const s = sourceJournals.get(element), input = s?.inputSelection;
  return journalSource(element) && !!input && sameJournalIdentity(input, journalIdentity(element))
    && element.value === input.value && element.selectionStart === input.start && element.selectionEnd === input.end;
}
export function armSourceJournalRender(element, before, after, start, end) {
  const s = sourceJournals.get(element);
  if (!journalSource(element) || !s || s.composing || element.value !== before) return false;
  s.owned = {...journalIdentity(element), before, after, start, end, phase: 'render'};
  return true;
}
export function selectSourceJournal(element, fileId, expected, start, end) {
  const s = sourceJournals.get(element);
  if (!s || s.composing || !s.owned || element.value !== expected || s.owned.after !== expected) return false;
  s.owned.phase = 'selection'; s.owned.start = start; s.owned.end = end;
  const selected = trySelect(element, fileId, expected, start, end);
  if (!selected) s.owned = null;
  return selected;
}
export function enableSourceJournal(element, reference) {
  if (!journalSource(element)) return;
  const existing = sourceJournals.get(element);
  if (existing) { existing.reference = reference; return; }
  if (sourceJournalBinding >= Number.MAX_SAFE_INTEGER) return;
  const state = {reference, bindingEpoch: ++sourceJournalBinding, epoch: 0, compositionEpoch: 0, compositionEndEpoch: 0,
    composing: false, composition: null, before: null, frame: null, lastConsumed: journalSnapshot(element), owned: null, inputSelection: null, events: []};
  const bump = () => { if (state.epoch >= Number.MAX_SAFE_INTEGER) { state.composing = true; return false; } state.epoch++; return true; };
  const notify = (method, ...args) => { try { state.reference?.invokeMethodAsync(method, ...args).catch(() => {}); } catch { /* disposed host */ } };
  const frame = (before, composing) => {
    const after = journalSnapshot(element);
    return {...journalIdentity(element), bindingEpoch: state.bindingEpoch, compositionEpoch: state.compositionEpoch, nativeEpoch: state.epoch,
      beforeValue: before.value, afterValue: after.value, beforeStart: before.start, beforeEnd: before.end,
      start: after.start, end: after.end, composing, overrun: false};
  };
  const beforeInput = () => { state.owned = null; if (bump()) state.before = journalSnapshot(element); };
  const input = event => {
    state.owned = null; if (!bump()) return;
    const next = frame(state.before ?? journalSnapshot(element), state.composing || event.isComposing === true);
    if (state.frame) { next.beforeValue = state.frame.beforeValue; next.beforeStart = state.frame.beforeStart; next.beforeEnd = state.frame.beforeEnd; next.overrun = true; }
    state.frame = next; state.before = null; state.inputSelection = journalSnapshot(element); // One bounded pair, never a buffer queue.
  };
  const compositionStart = () => {
    state.owned = null; if (!bump() || state.compositionEpoch >= Number.MAX_SAFE_INTEGER) return;
    state.composing = true; state.compositionEpoch++; state.compositionEndEpoch = 0;
    state.composition = {...journalIdentity(element), bindingEpoch: state.bindingEpoch, compositionEpoch: state.compositionEpoch, nativeEpoch: state.epoch};
    state.before = state.lastConsumed = journalSnapshot(element);
    notify('SourceJournalComposition', {...state.composition, active: true});
  };
  const compositionEnd = () => {
    if (!state.composition || !sameJournalIdentity(state.composition, journalIdentity(element)) || !bump()) return;
    state.owned = null; state.composing = false; state.compositionEndEpoch = state.epoch;
    if (!state.frame && element.value !== state.lastConsumed.value) state.frame = frame(state.lastConsumed, false);
    notify('SourceJournalComposition', {...state.composition, nativeEpoch: state.compositionEndEpoch, active: false});
    state.composition = null;
  };
  const selected = () => { if (!sourceJournalOwnsSelection(element)) { state.owned = null; if (!sourceJournalInputOwnsSelection(element)) state.inputSelection = null; bump(); } };
  const pointer = () => { state.owned = state.inputSelection = null; };
  const key = event => {
    state.owned = state.inputSelection = null;
    // Native prevention is needed synchronously. C# still owns the command.
    if (!journalSource(element) || event.altKey || !(event.ctrlKey || event.metaKey) || state.composing || event.isComposing) return;
    if (event.code !== 'KeyZ' && event.code !== 'KeyY') return;
    event.preventDefault();
    notify('SourceJournalKey', {...journalIdentity(element), bindingEpoch: state.bindingEpoch, nativeEpoch: state.epoch,
      direction: event.code === 'KeyY' || event.shiftKey ? 'redo' : 'undo'});
  };
  for (const [name, fn] of [['beforeinput', beforeInput], ['input', input], ['compositionstart', compositionStart],
    ['compositionend', compositionEnd], ['select', selected], ['selectionchange', selected], ['pointerdown', pointer], ['keydown', key]]) {
    element.addEventListener(name, fn); state.events.push([name, fn]);
  }
  sourceJournals.set(element, state);
}
export function takeSourceJournalInput(element, expectedAfter) {
  const s = sourceJournals.get(element);
  if (!journalSource(element) || !s?.frame || s.frame.afterValue !== expectedAfter) return null;
  const value = s.frame; s.frame = null;
  s.lastConsumed = {fileId: value.fileId, session: value.session, path: value.path, value: value.afterValue, start: value.start, end: value.end};
  return value;
}
export function sourceJournalState(element) {
  const s = sourceJournals.get(element);
  return journalSource(element) && s ? {...journalIdentity(element), bindingEpoch: s.bindingEpoch, epoch: s.epoch,
    compositionEpoch: s.compositionEpoch, compositionEndEpoch: s.compositionEndEpoch, composing: s.composing} : null;
}
export function resetSourceJournal(element) {
  const s = sourceJournals.get(element);
  if (!s) return;
  if (sourceJournalBinding >= Number.MAX_SAFE_INTEGER) { s.composing = true; return; }
  s.bindingEpoch = ++sourceJournalBinding; s.epoch = s.compositionEpoch = s.compositionEndEpoch = 0;
  s.before = s.frame = s.composition = s.owned = s.inputSelection = null; s.composing = false; s.lastConsumed = journalSnapshot(element);
}
export function disableSourceJournal(element) {
  const s = sourceJournals.get(element);
  if (s) { for (const [name, fn] of s.events) element.removeEventListener(name, fn); s.reference = null; s.before = s.frame = s.lastConsumed = s.owned = s.inputSelection = s.composition = null; }
  sourceJournals.delete(element);
}
