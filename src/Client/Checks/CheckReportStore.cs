using System.Text.Json;
using Client.Workspace.Domain;
using Microsoft.JSInterop;

namespace Client.Checks;

/// <summary>Independent local report records. A saved report never carries live edit or navigation authority.</summary>
public sealed class CheckReportStore(IJSRuntime js)
{
    internal const int MaxPayloadChars = 1_048_576;
    public const int PageSize = 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Default)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<(CheckReport[] Reports, bool HasMore)> ReadAsync(ProjectId project, CheckReport? before)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/workspace-storage.js");
        var page = await module.InvokeAsync<string[]>("readCheckReports", project.ToString(), before?.Created, before?.Id, PageSize);
        if (page.Length > PageSize + 1) throw new InvalidDataException("Invalid saved report page.");
        var reports = page.Take(PageSize).Select(payload =>
        {
            if (payload.Length > MaxPayloadChars || JsonSerializer.Deserialize<CheckReport>(payload, Json) is not { } report
                || !report.IsValid(project)) throw new InvalidDataException("Invalid saved report.");
            return report;
        }).ToArray();
        return (reports, page.Length > PageSize);
    }

    public async Task<bool> SaveAsync(ProjectId project, string lockId, CheckReport report)
    {
        if (!report.IsValid(project)) throw new InvalidDataException("Report is outside the local storage limits.");
        var payload = JsonSerializer.Serialize(report, Json);
        if (payload.Length > MaxPayloadChars) throw new InvalidDataException("Report is too large to save locally.");
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/workspace-storage.js");
        return await module.InvokeAsync<bool>("writeCheckReport", project.ToString(), lockId, report.Id, report.Created, payload);
    }

    public async Task<bool> RemoveAsync(ProjectId project, string lockId, string id)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/workspace-storage.js");
        return await module.InvokeAsync<bool>("removeCheckReport", project.ToString(), lockId, id);
    }
}
