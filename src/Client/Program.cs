using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Client;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.History;
using Client.Workspace.Transfer;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// C# owns domain/workflow/persistence policy. Storage and locks are thin
// browser-API glue (workspace-storage.js, folder-transfer.js) invoked via JS
// interop. Transfer/history services are C#-only policy over the repository.
builder.Services.AddSingleton<IProjectRepository, WorkspaceStorage>();
builder.Services.AddSingleton<IWorkspaceLocks, JsWorkspaceLocks>();
builder.Services.AddSingleton<IProjectTransfer, ZipTransferService>();
builder.Services.AddSingleton<HistoryService, HistoryService>();
builder.Services.AddScoped<FolderTransferService>();
builder.Services.AddScoped<WorkspaceCoordinator>();
// M2a-2b-1: the analysis worker transport/adapter (Rust WASM over the
// canonical v1 worker envelope). Not owned/disposed by the coordinator.
builder.Services.AddScoped<ISourceAnalyzer, RustSourceAnalyzer>();
// M2a-2b-2b: the AnalysisCoordinator (debounce/cancel/freshness orchestration)
// and the guarded click-to-source navigator. Both are scoped — in Blazor WASM a
// scoped service lives for the whole application lifetime and is shared across
// page mounts, so navigating away and back reuses the SAME coordinator (no
// disposed-service reuse) and the worker epoch survives a page remount. DI
// owns these services and disposes them at application end; the page only
// subscribes/unsubscribes and cancels page analysis on dispose (it must NOT
// dispose the injected coordinator/analyzer). Analysis is independent of
// save/AI/network: it lazy-initializes the worker without blocking page
// init/editing/saving.
builder.Services.AddScoped<AnalysisCoordinator>();
builder.Services.AddScoped<SourceNavigator>();

await builder.Build().RunAsync();
