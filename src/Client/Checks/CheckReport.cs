using System.Text.Json;
using Client.Workspace.Domain;

namespace Client.Checks;

public sealed record CheckReport(
    string Id, string ProjectId, long Created, string Kind, string Title,
    string Outcome, string? Revision, string? EvidenceIdentity, string Detail)
{
    public static CheckReport Create(ProjectId project, string kind, string title, string outcome,
        RevisionId? revision, string? evidenceIdentity, string detail, string? id = null)
        => new(id ?? Guid.NewGuid().ToString("N"), project.ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            kind, title, outcome, revision?.ToString(), evidenceIdentity, detail);

    public static string FormatEvidence<T>(T evidence) => JsonSerializer.Serialize(evidence, EvidenceOptions);

    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal bool IsValid(ProjectId project) => ProjectId == project.ToString() && Id is { Length: > 0 and <= 128 }
        && Created is >= 0 and <= 253402300799999L && Kind is "source" or "compiler" or "pdf" or "ai"
        && Title is { Length: > 0 and <= 512 } && Outcome is { Length: > 0 and <= 512 }
        && (Revision is null || Guid.TryParse(Revision, out _)) && (EvidenceIdentity is null || EvidenceIdentity.Length <= 256)
        && Detail is { Length: <= CheckReportStore.MaxPayloadChars };
}
