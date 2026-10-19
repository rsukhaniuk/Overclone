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
// Pages borrow them across remounts. Compile/PDF default is explicitly unavailable;
// this registration selects no executable runtime and starts no native imports.
builder.Services.AddAuthoringWorkspace();

await builder.Build().RunAsync();
