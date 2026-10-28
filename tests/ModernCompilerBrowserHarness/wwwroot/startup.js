// Test-host adaptation of the existing app's bounded runtime boot admission.
// Default framework resource loading/integrity remains in Blazor; no SW/cache
// substitution, security switches, altered manifests or fallback assemblies.
Blazor.start({
    loadBootResource: (type, _name, defaultUri) =>
        type === 'dotnetjs' ? `${defaultUri}?v=modern-profile-v1` : undefined,
    configureRuntime: builder => builder.withConfig({ maxParallelDownloads: 8 }),
}).catch(error => {
    console.error('Explicit modern compiler test startup failed', error);
    document.getElementById('app').textContent = String(error);
});
