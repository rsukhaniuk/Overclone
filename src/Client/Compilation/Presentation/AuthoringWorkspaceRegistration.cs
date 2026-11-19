using Client.Analysis;
using Client.AI;
using Client.Compilation.Download;
using Client.Compilation.History;
using Client.Compilation.History.Index;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Compilation.SyncTeX;
using Microsoft.JSInterop;
using Client.Review;
using Client.Pdf;
using Client.Workspace;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Client.Workspace.Drive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Client.Compilation.Presentation;

public static class AuthoringWorkspaceRegistration
{
    public static IReadOnlyList<CompileOutputDescriptor> HistoricalOutputAdapters() =>
        new[] { NativeSyncTeXContract.OutputAdapter, NativeLatexDviContract.OutputAdapter }
            .Concat(Enum.GetValues<NativeRuntimeProfile>()
                .Where(p => NativeRuntimeProfiles.IsSuite(p) && NativeRuntimeProfiles.SuitePinsReady(p))
                .SelectMany(p => Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerComponents.For(p, e).OutputAdapter)))
            .Where(adapter => adapter != NativeRuntimePackage.Adapter).Distinct().ToArray();
    /// <summary>Shared app services with fixed local authoring trust and no bundled runtime bytes.
    /// A trusted TEST/application builder may explicitly replace the configuration.</summary>
    public static IServiceCollection AddAuthoringWorkspace(this IServiceCollection services)
    {
        services.AddSingleton<IProjectRepository, WorkspaceStorage>();
        services.AddSingleton<IWorkspaceLocks, JsWorkspaceLocks>();
        services.AddSingleton<IProjectTransfer, ZipTransferService>();
        services.AddSingleton<HistoryService>();
        services.AddScoped<FolderTransferService>();
        services.AddScoped<WorkspaceCoordinator>();
        services.AddScoped<ISourceAnalyzer, RustSourceAnalyzer>();
        services.AddScoped<AnalysisCoordinator>();
        services.AddScoped<SourceNavigator>();
        services.AddScoped<IChangeReviewService, ChangeReviewService>();
        services.AddAiAssistance();
        services.AddDriveTransfers();
        services.AddScoped<LocalInstalledRuntimeProviders>();
        services.AddScoped<ISyncTeXQuery>(provider => new JsSyncTeXQuery(provider.GetRequiredService<IJSRuntime>(),
            provider.GetRequiredService<LocalInstalledRuntimeProviders>()));
        services.TryAddScoped<ICompileWorkspaceConfiguration, LocalCompileWorkspaceConfiguration>();
        services.AddScoped<Client.Compilation.Provisioning.NativeRuntimeSetupFactory>();
        services.AddScoped<Client.Compilation.Provisioning.PdfRuntimeSetupFactory>();
        services.AddScoped<CompileWorkspaceSession>();
        services.AddTransient<Client.Analysis.RenderedReview.IRenderedReviewEvaluator, Client.Analysis.RenderedReview.RustRenderedReviewEvaluator>();
        services.AddScoped<IPdfDownloadBrowser, PdfDownloadBrowser>();
        services.AddScoped<CompileHistoryUiSession>(provider =>
        {
            var workspace = provider.GetRequiredService<WorkspaceCoordinator>();
            var compile = provider.GetRequiredService<CompileWorkspaceSession>();
            var js = provider.GetRequiredService<IJSRuntime>();
            // The same output descriptors used by CompileWorkspaceSession's B3
            // request options. Metadata contracts only: no runtime is selected,
            // provisioned or approved here, including the unavailable default.
            var store = new CompileArtifactHistoryStore(new ArtifactStorageBackend(js),
                provider.GetRequiredService<IProjectRepository>(), NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter,
                HistoricalOutputAdapters());
            var index = new CompileHistoryIndex(new JsHistoryIndexBackend(js));
            var config = provider.GetRequiredService<ICompileWorkspaceConfiguration>() as LocalCompileWorkspaceConfiguration;
            var capacity = config?.UsesBundledResources == true ? BundledRuntimeAssets.SelectedPdfCapacity : null;
            int maxPdfBytes = capacity?.MaxInputBytes ?? PdfDocument.DefaultInputBytes;
            return new(workspace, compile, store, index,
                new(new(NativeThesisCompilerContract.ProjectFiles,
                    NativeRuntimeProfiles.ProjectFileBytes(NativeInputBudgetContract.Profile),
                    NativeRuntimeProfiles.ProjectTotalBytes(NativeInputBudgetContract.Profile), 131072), maxPdfBytes, 262144,
                    new(new(2, 256, maxPdfBytes, 16777216, 65536, 256, 256), 16384, 64, 128, 15000))
                    { CompiledInputCapacity = capacity },
                new(64, 65536, 15000),
                config?.PdfInspector);
        });
        return services;
    }
}
