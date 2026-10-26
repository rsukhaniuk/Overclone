using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Client;
using Client.Analysis;
using Client.Review;
using Client.Workspace;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Client.Compilation.Presentation;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same app-owned M2 persistence/analysis/review services, shared with TEST builders.
// Pages borrow them across remounts. Compile/PDF require explicit installation
// and verification of supported local resources; startup executes no native code.
builder.Services.AddAuthoringWorkspace();

await builder.Build().RunAsync();
