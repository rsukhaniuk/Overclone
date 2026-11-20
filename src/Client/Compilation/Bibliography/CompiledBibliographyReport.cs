using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client.Compilation.Bibliography;

internal sealed record CompiledBibliographyReport(int ProtocolVersion, string RequestId, string Status, string? Reason,
    bool? MembershipMatches, bool? OrderMatches, bool? NumbersMatch, CompiledBibliographyRow[] Rows)
{
    private const int MaxResponseBytes = 262144, MaxRows = 256;
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 8,
    };

    internal static CompiledBibliographyReport? Read(string requestId, string? json)
    {
        if (json is null || json.Length > MaxResponseBytes || Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
            return null;
        CompiledBibliographyReport? report;
        try { report = JsonSerializer.Deserialize<CompiledBibliographyReport>(json, Strict); }
        catch (JsonException) { return null; }
        if (report is not { ProtocolVersion: 1 } || report.RequestId != requestId
            || report.Rows is null || report.Rows.Length > MaxRows) return null;
        if (report.Status == "unavailable")
            return report.Rows.Length == 0 && report.MembershipMatches is null && report.OrderMatches is null
                && report.NumbersMatch is null && report.Reason is { Length: > 0 and <= 64 }
                && report.Reason.All(c => char.IsAsciiLetterLower(c) || c == '-') ? report : null;
        if (report.Status is not ("match" or "difference") || report.Reason is not null
            || report.MembershipMatches is null || report.OrderMatches is null || report.NumbersMatch is null
            || (report.Status == "match") != (report.MembershipMatches == true && report.OrderMatches == true && report.NumbersMatch == true))
            return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var firstUses = new HashSet<int>();
        var positions = new HashSet<int>();
        foreach (var row in report.Rows)
            if (row is null || row.Key is not { Length: > 0 and <= 96 } || Encoding.UTF8.GetByteCount(row.Key) > 96
                || !keys.Add(row.Key)
                || row.Key.EnumerateRunes().Any(rune => !Rune.IsLetterOrDigit(rune) && rune.Value is not (':' or '.' or '_' or '-' or '/' or '+'))
                || row.FirstUse is null && row.ListPosition is null
                || row.FirstUse is { } first && (first < 1 || first > MaxRows || !firstUses.Add(first))
                || row.ListPosition is { } position && (position < 1 || position > MaxRows || !positions.Add(position))
                || row.AssignedNumber is < 1 or > 999999
                || (row.AssignedNumber is null) != (row.ListPosition is null))
                return null;
        return firstUses.All(n => n <= firstUses.Count) && positions.All(n => n <= positions.Count) ? report : null;
    }
}

internal sealed record CompiledBibliographyRow(string Key, int? FirstUse, int? ListPosition, int? AssignedNumber);
internal sealed record CompiledBibliographyBridgeReply(int Version, string RequestId, long WorkerEpoch, string Status, string? ResponseJson);
