using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Client;
using Client.Analysis;
using Client.Review;
using Client.Workspace;
using Client.Workspace.Transfer;
using Client.Compilation.Presentation;
using Client.Compilation.Provisioning;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same app-owned M2 persistence/analysis/review services, shared with TEST builders.
// Pages borrow them across remounts. Compiler/PDF assets ship with the app and
// prepare automatically; actual native execution remains in isolated workers.
builder.Services.AddAuthoringWorkspace();
builder.Services.AddScoped<Client.Checks.CheckReportStore>();
builder.Services.AddScoped<Client.Editing.Assistance.IAuthoringAssistProvider, Client.Editing.Assistance.RustAuthoringAssistProvider>();
builder.Services.AddScoped(services => new BundledRuntimeAssets(new HttpClient
{
    BaseAddress = new Uri(services.GetRequiredService<NavigationManager>().BaseUri),
    Timeout = Timeout.InfiniteTimeSpan // The bounded, owned downloads enforce their own deadlines.
}, services.GetRequiredService<IJSRuntime>()));

await builder.Build().RunAsync();
