using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Client;
using Client.Workspace;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// C# owns domain/workflow/persistence policy. Storage and locks are thin
// browser-API glue (workspace-storage.js) invoked via JS interop.
builder.Services.AddSingleton<IProjectRepository, WorkspaceStorage>();
builder.Services.AddSingleton<IWorkspaceLocks, JsWorkspaceLocks>();
builder.Services.AddScoped<WorkspaceCoordinator>();

await builder.Build().RunAsync();
