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
