using Client.Review;
using Client.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Client.AI;

public static class AiRegistration
{
    /// <summary>Call after the application's existing workspace/review registration.
    /// Does not register or replace those services, choose models or send requests.</summary>
    public static IServiceCollection AddAiAssistance(this IServiceCollection services)
    {
        services.TryAddScoped<AiSessionFactory>();
        return services;
    }
}

/// <summary>A dedicated non-logging HTTP client, never the app's shared client.
/// The panel owns only its session; DI owns this factory/transport lifetime.</summary>
public sealed class AiSessionFactory : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly IChangeReviewService _review;
    private readonly HttpClient _http = new();
    public AiSessionFactory(WorkspaceCoordinator workspace, IChangeReviewService review)
    { _workspace = workspace; _review = review; }
    public AiSession Create() => new(_workspace, _review,
        [new OllamaProvider(_http), new GeminiProvider(_http), new ClaudeProvider(_http)]);
    public void Dispose() => _http.Dispose();
}
