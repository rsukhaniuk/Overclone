using System.Text.Json;
using static Client.Analysis.RenderedExtent.RenderedExtentJson;

namespace Client.Analysis.RenderedExtent;

// Finite request/response and measured-count correlation only. The limits and
// comparison policy belong to the bundled Rust export, never a C# threshold table.
internal static class RenderedSectionJson
{
    internal static object Pack(RenderedSectionRanges ranges)
    {
        object? Range(RenderedPageRange? r) => r is null ? null : new { start = r.Start, end = r.End };
        return new { introduction = Range(ranges.Introduction), mainBody = Range(ranges.MainBody),
            conclusions = Range(ranges.Conclusions), appendixStartPage = ranges.AppendixStartPage, noAppendices = ranges.NoAppendices };
    }
    internal static RenderedSectionResults Parse(JsonElement root, RenderedExtentRequest expected, out int? nonAppendix)
    {
        var ranges = expected.Sections!;
        var s = root.GetProperty("sections"); Keys(s, ["labelsSource", "ranges", "introduction", "mainBody", "conclusions"]);
        Require(Text(s, "labelsSource") == "userProvidedPageRanges");
        var echo = s.GetProperty("ranges"); Keys(echo, ["introduction", "mainBody", "conclusions", "appendixStartPage", "noAppendices"]);
        void Range(string name, RenderedPageRange? range)
        {
            var value = echo.GetProperty(name);
            if (range is null) { Require(value.ValueKind == JsonValueKind.Null); return; }
            Keys(value, ["start", "end"]); Require(Integer(value, "start") == range.Start && Integer(value, "end") == range.End);
        }
        Range("introduction", ranges.Introduction); Range("mainBody", ranges.MainBody); Range("conclusions", ranges.Conclusions);
        Require(OptionalInteger(echo, "appendixStartPage") == ranges.AppendixStartPage);
        Require(echo.GetProperty("noAppendices").ValueKind is JsonValueKind.True or JsonValueKind.False
            && echo.GetProperty("noAppendices").GetBoolean() == ranges.NoAppendices);
        nonAppendix = OptionalInteger(root, "nonAppendixPages");
        Require(nonAppendix == (ranges.AppendixStartPage is { } start ? start - 1 : ranges.NoAppendices ? expected.PageCount : null)
            && OptionalInteger(root, "mainBodyPages") == ranges.MainBody?.Count);
        RenderedSectionResult Result(string name, string id, RenderedPageRange? range, bool hasMinimum, bool hasMaximum)
        {
            var value = s.GetProperty(name); Keys(value, ["ruleId", "pages", "minimum", "maximum", "status"]);
            var pages = OptionalInteger(value, "pages"); var min = OptionalInteger(value, "minimum"); var max = OptionalInteger(value, "maximum");
            var status = Text(value, "status"); Require(Text(value, "ruleId") == id && pages == range?.Count);
            Require(hasMinimum ? min is >= 1 and <= 512 : min is null);
            Require(hasMaximum ? max is >= 1 and <= 512 : max is null);
            Require(range is null || !hasMinimum && !hasMaximum ? status == "indeterminate"
                : status == "withinLimit" || hasMinimum && status == "belowMinimum" || hasMaximum && status == "aboveMaximum");
            return new(id, pages, min, max, status);
        }
        return new(Result("introduction", "KNU-CSC-2025-PDF-INTRODUCTION-001", ranges.Introduction, false, true),
            Result("mainBody", "KNU-CSC-2025-PDF-BODY-001", ranges.MainBody, expected.WorkType != "unspecified", false),
            Result("conclusions", "KNU-CSC-2025-PDF-CONCLUSIONS-001", ranges.Conclusions, false, true));
    }
}
