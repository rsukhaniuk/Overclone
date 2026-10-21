// TEST-host startup only. Supported runtime download admission; no integrity,
// browser security or native inspection policy changes. App/UI logic stays C#.
Blazor.start({ configureRuntime: runtime => runtime.withConfig({ maxParallelDownloads: 8 }) })
 .catch(error => { console.error('N15 TEST runtime startup failed', error); });
