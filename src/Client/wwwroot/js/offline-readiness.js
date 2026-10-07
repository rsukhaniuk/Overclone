// offline-readiness.js — minimal browser-event glue (M2a-2b-2b): forwards the
// service-worker "warm-result" readiness state to the .NET app via a
// registered DotNetObjectReference. The service worker's warming step posts
// {type:"warm-result"} to the page; index.html records the readiness state
// and calls window.__oopOfflineReadinessSink(state). This module wires that
// sink to a .NET [JSInvokable] callback. C# owns the visible
// provisioning/incomplete notice and the connect/reload/retry guidance;
// JS only reports the readiness state string ("unknown" | "ready" |
// "incomplete"). No DOM editing, no policy.
//
// Subscription identity / teardown:
//   subscribeOfflineReadiness(dotNetRef) -> Promise<string>
//     installs THIS subscription's sink and returns an opaque subscription
//     id. The page calls unsubscribeOfflineReadiness(id) BEFORE disposing the
//     DotNetObjectReference. Unsubscribe clears window.__oopOfflineReadinessSink
//     ONLY if it is still THIS subscription's sink (an older page's teardown
//     cannot clear a newer page's subscription), releases the .NET ref
//     closure, and makes the callback a no-op so no .NET callback fires after
//     the ref is gone. A rejected invokeMethodAsync promise (e.g. a disposed
//     DotNet reference) is HANDLED (.catch) so it is never an unhandled
//     rejection — the prior try/catch only caught synchronous throws.

const subscriptions = new Map();

export async function subscribeOfflineReadiness(dotNetRef) {
  // Opaque per-subscription id (string) the page holds and passes back to
  // unsubscribe. Round-trips through Blazor JSInterop without carrying a JS
  // object reference across the boundary.
  const id = `oop-offline-readiness-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const sub = { id, dotNetRef, disposed: false, emit: null };
  sub.emit = (state) => {
    // No callbacks after THIS subscription is unsubscribed.
    if (sub.disposed) return;
    // invokeMethodAsync returns a Promise; a disposed .NET ref rejects it.
    // Handle that rejection so it is never an unhandled rejection (the prior
    // try/catch only caught synchronous throws).
    let p;
    try {
      p = sub.dotNetRef.invokeMethodAsync(
        "OnOfflineReadiness",
        state || "unknown",
      );
    } catch {
      // Synchronous throw (rare): best-effort, never rethrow into the SW.
      return;
    }
    if (p && typeof p.catch === "function")
      p.catch(() => {
        /* .NET ref disposed — best-effort */
      });
  };
  subscriptions.set(id, sub);
  // Install THIS subscription's sink. A newer page overwrites this with its
  // own sink; unsubscribe only clears the global if it still points to THIS
  // emit, so a newer page's subscription survives an older page's teardown.
  window.__oopOfflineReadinessSink = sub.emit;
  // Report the current state immediately (may already be "ready" by the time
  // the page mounts after warming, or "unknown" while warming is in progress).
  sub.emit(window.__oopOfflineReadiness || "unknown");
  return id;
}

export function unsubscribeOfflineReadiness(id) {
  const sub = subscriptions.get(id);
  if (!sub) return;
  subscriptions.delete(id);
  // Stop further callbacks for THIS subscription (the emit closure checks this).
  sub.disposed = true;
  // Clear the global sink ONLY if it is still THIS subscription's sink, so an
  // older page's teardown cannot clear a newer page's subscription.
  if (window.__oopOfflineReadinessSink === sub.emit) {
    delete window.__oopOfflineReadinessSink;
  }
  // Release the .NET ref closure; no callbacks after unsubscription.
  sub.dotNetRef = null;
}
