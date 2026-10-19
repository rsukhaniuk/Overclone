using Client.Workspace.Transfer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Client.Workspace.Drive;

public static class DriveRegistration
{
    /// <summary>Lead integration opt-in. Existing workspace, repository and
    /// IProjectTransfer registrations must already exist. Does not load Google
    /// scripts, connect, transfer, save or modify the current workspace.</summary>
    public static IServiceCollection AddDriveTransfers(this IServiceCollection services)
    {
        services.TryAddScoped<IDriveAuth, BrowserDriveAuth>();
        services.TryAddScoped<IArchiveExport>(sp => new ZipTransferService(sp.GetRequiredService<IProjectRepository>()));
        // A private HttpClient avoids general-purpose delegating handlers that
        // may log headers. Browser fetch options still enforce no ambient cookies.
        services.TryAddScoped<DriveRestClient>(sp => new DriveRestClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, sp.GetRequiredService<IDriveAuth>()));
        services.AddTransient<DriveTransferSession>();
        return services;
    }
}
