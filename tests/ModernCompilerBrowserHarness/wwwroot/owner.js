// Test-only browser scheduling seam for the existing single-owner context.
export function schedule() {
    queueMicrotask(() => DotNet.invokeMethodAsync('ModernCompilerBrowserHarness', 'Pump')
        .catch(error => { document.getElementById('result').textContent = String(error); }));
}
