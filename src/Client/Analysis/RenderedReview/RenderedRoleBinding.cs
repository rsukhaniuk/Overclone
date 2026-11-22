using System.Text;
using Client.Analysis.RenderedExtent;
using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Compilation.SyncTeX;
using Client.Pdf;
using Client.Workspace;
using Client.Workspace.Transfer;

namespace Client.Analysis.RenderedReview;

/// <summary>A canonical Rust event index, never an author-supplied role or text.</summary>
public sealed record RenderedRoleBindingRequest(AnalysisReport Report, int EventIndex, long AnnotationId, string WorkType);
public sealed record RenderedRoleBindingResult(RenderedRoleBinding.Proof? Value, string Code);

/// <summary>
/// Joins existing Rust observations, accepted captured bytes, actual native
/// queries and PDF character boxes. Contains no TeX parser or normative rule.
/// Named section roles denote ONLY heading characters, never section extents.
/// Character boxes establish map/text correlation, NOT measured glyph ink.
/// Borrowed services retain their ownership; this service edits no document.
/// </summary>
public sealed class RenderedRoleBinding(WorkspaceCoordinator workspace, CompileWorkspaceSession compile,
    SourceNavigator source, SyncTeXNavigationPolicy navigation)
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Proof, object> _issued = new();
    public sealed class Proof
    {
        internal Proof(RenderedRoleBinding owner, RenderedExtentAuthority authority, RenderedRoleBindingRequest request,
            RenderedRoleAnnotation annotation, string rangeKind, string factsRequest, long factsEpoch, PdfRuntimeIdentity runtime,
            PdfBox nativeBounds, int sourceLine, CancellationToken cancellation, IEnumerable<PdfGlyph> characters)
        {
            Owner = owner; Authority = authority; Request = request; Annotation = annotation; RangeKind = rangeKind;
            InspectionRequestId = factsRequest; InspectionEpoch = factsEpoch; Runtime = runtime;
            NativeQueryBounds = nativeBounds; SourceLine = sourceLine;
            Cancellation = cancellation;
            NativeCharacters = Array.AsReadOnly(characters.ToArray());
        }
        internal RenderedRoleBinding Owner { get; }
        internal RenderedExtentAuthority Authority { get; }
        internal RenderedRoleBindingRequest Request { get; }
        internal CancellationToken Cancellation { get; }
        public RenderedRoleAnnotation Annotation { get; }
        public string RangeKind { get; }
        public string InspectionRequestId { get; }
        public long InspectionEpoch { get; }
        public PdfRuntimeIdentity Runtime { get; }
        public PdfBox NativeQueryBounds { get; }
        public int SourceLine { get; }
        public IReadOnlyList<PdfGlyph> NativeCharacters { get; }
        public string GeometryMethod => "native-character-box-map-correlation";
        public bool GlyphInkMeasured => false;
        public AcceptedCompileOutput Output => Authority.Output;
        public AnalysisReport Report => Request.Report;
    }

    public bool IsCurrent(Proof proof, string workType, CancellationToken ct = default)
        => proof is not null && !proof.Cancellation.IsCancellationRequested && _issued.TryGetValue(proof, out _)
            && ReferenceEquals(proof.Owner, this) && workType == proof.Request.WorkType
            && Current(proof.Request, proof.Authority, ct);

    public async Task<bool> VerifyCurrentAsync(Proof proof, string workType, CancellationToken ct = default)
    {
        if (!IsCurrent(proof, workType, ct)) return false;
        var verified = await source.VerifyCurrentAsync(proof.Report, ct);
        bool current = verified.Ok && IsCurrent(proof, workType, ct);
        // A failed epoch verification retires this issuance. Cancellation of a
        // separate review check alone must not discard an otherwise valid label.
        if (!current && !ct.IsCancellationRequested) _issued.Remove(proof);
        return current;
    }

    public async Task<RenderedRoleBindingResult> BindAsync(RenderedRoleBindingRequest request, CancellationToken ct = default)
    {
        static RenderedRoleBindingResult Refuse(string code) => new(null, code);
        var authority = RenderedExtentAuthority.Capture(workspace, compile);
        if (authority is null || !Current(request, authority, ct)) return Refuse("stale-source-or-compile");
        if (!Candidate(request, authority, out var candidate)) return Refuse("unsupported-or-ambiguous-source-role");
        var (e, role, rangeKind, file, span) = candidate;
        try
        {
            var verified = await source.VerifyCurrentAsync(request.Report, ct);
            if (!verified.Ok || !Current(request, authority, ct)) return Refuse("stale-source-report");
            var forward = await navigation.ForwardAsync(file.FileId, span.SourceStart, ct);
            if (!Current(request, authority, ct) || !QueryCurrent(forward, authority)
                || forward!.Matches[0] is not { } match || match.File.FileId != file.FileId
                || match.File.RelativePath != file.RelativePath || match.Line != span.Line)
                return Refuse("native-forward-not-exact");
            var document = authority.CreateDocument(authority.Output.ToPayloadArray()!);
            var facts = await authority.Inspector!.InspectAsync(new(document, match.Page - 1,
                RenderedExtentAuthority.InspectionBudget(document), raster: false), ct);
            if (!Current(request, authority, ct) || !QueryCurrent(forward, authority)) return Refuse("stale-pdf-inspection");
            if (facts is not { Status: PdfInspectionStatus.Completed, Page: { } page, WorkerEpoch: > 0, Runtime: { } runtime }
                || facts.DocumentSha256 != document.Sha256 || !RenderedExtentContract.RequestId(facts.RequestId)
                || !PdfHashes.IsSha(runtime.LoaderSha256) || !PdfHashes.IsSha(runtime.WasmSha256)
                || page.PageIndex != match.Page - 1 || page.PageCount is < 1 or > 512
                || page.PageIndex >= page.PageCount || page.Glyphs.Count is < 1 or > 100000)
                return Refuse("invalid-native-character-facts");
            if (!CharacterRange(page, e.Text, out int start, out int end, out var points))
                return Refuse("literal-text-range-not-unique");
            if (!Transform(forward!, match, page, out var matrix, out var bounds)
                || points.Any(p => !Contains(bounds, p))) return Refuse("native-box-does-not-contain-text");
            if (!matrix.TryInverse(out var inverse)) return Refuse("unsupported-native-transform");
            // Probe both ends of the independently measured character range.
            // The native line supplies correlation only; exact source span is
            // exclusively the canonical Rust span checked against captured bytes.
            foreach (var point in new[] { points[0], points[^1] }.Distinct())
            {
                var p = Apply(inverse, point);
                var back = await navigation.InverseAsync(match.Page, p.X, p.Y, ct);
                if (!Current(request, authority, ct) || !QueryCurrent(forward, authority) || !QueryCurrent(back, authority)
                    || back!.Matches[0].File.FileId != file.FileId || back.Matches[0].File.RelativePath != file.RelativePath
                    || back.Matches[0].Line != span.Line) return Refuse("native-inverse-not-exact");
            }
            verified = await source.VerifyCurrentAsync(request.Report, ct);
            if (!verified.Ok || !Current(request, authority, ct) || !QueryCurrent(forward, authority))
                return Refuse("stale-before-role-publication");
            var annotation = new RenderedRoleAnnotation(request.AnnotationId, role, page.PageIndex, start, end,
                e.FileId, e.Span, "literalSourceAndNativeMap", "confirmedObservation");
            var proof = new Proof(this, authority, request, annotation, rangeKind, facts.RequestId, facts.WorkerEpoch,
                runtime, bounds, span.Line, ct, page.Glyphs.Skip(start).Take(end - start));
            _issued.Add(proof, new object());
            return new(proof, "bound-literal-character-range");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Refuse(ct.IsCancellationRequested ? "cancelled" : "binding-unavailable"); }
    }

    private bool QueryCurrent(SyncTeXNavigationResult? query, RenderedExtentAuthority authority)
        => query is { Truncated: false, Matches.Length: > 0 } && ReferenceEquals(query.Output, authority.Output)
            && ReferenceEquals(query.Ticket, authority.Ticket) && navigation.IsCurrent(query);

    private bool Current(RenderedRoleBindingRequest? request, RenderedExtentAuthority authority, CancellationToken ct)
    {
        if (request is null || request.Report is null || ct.IsCancellationRequested || request.AnnotationId is < 1 or > 9007199254740991
            || !authority.Current(workspace, compile) || !source.IsStillCurrent(request.Report)
            || request.Report.Snapshot.Configuration?.WorkType is not { } workType
            || request.WorkType != WorkType(workType) || request.WorkType == "unspecified"
            || request.Report.Result.Thesis is not { } thesis || thesis.WorkType != workType
            || request.Report.Snapshot.ReferenceConfiguration?.RootFileId != thesis.RootFileId
            || thesis.RootFileId != authority.Output.Snapshot.MainFileId.ToString()) return false;
        var a = request.Report.Snapshot; var captured = authority.Output.Snapshot; var c = captured.Identity;
        if (a.ProjectId != c.ProjectId || a.SessionId != c.SessionId || a.WorkingGeneration != c.WorkingGeneration
            || a.BaseHeadRevisionId != c.BaseHeadRevisionId || a.Files.Length != captured.Files.Count(f => f.MediaKind == "text")) return false;
        foreach (var f in a.Files)
        {
            var matches = captured.Files.Where(c => c.FileId == f.FileId).ToArray();
            if (matches.Length != 1 || matches[0] is not { MediaKind: "text" } input
                || input.RelativePath != f.RelativePath || input.HasBom != f.HasBom || input.Hash != f.Sha256
                || input.ByteLength != f.ByteLength || f.Bytes.IsDefault || !input.ByteSpan.SequenceEqual(f.Bytes.AsSpan())) return false;
            var live = workspace.Files.Where(l => !l.Deleted && l.Id == f.FileId).ToArray();
            if (live.Length != 1 || live[0].MediaKind != "text" || live[0].RelativePath != f.RelativePath || live[0].HasBom != f.HasBom
                || !TextContent.TryDecode(f.Bytes.ToArray(), out var text, out _) || live[0].Content != text
                || live[0] is { IsNew: true, DirtyContent: false, PendingTextBytes: null, ImportedBytes: { } imported }
                    && !imported.AsSpan().SequenceEqual(f.Bytes.AsSpan())) return false;
        }
        return true;
    }

    private static string WorkType(AnalysisWorkType value) => value switch
    {
        AnalysisWorkType.Coursework => "coursework", AnalysisWorkType.BachelorQualification => "bachelorQualification",
        AnalysisWorkType.MasterQualification => "masterQualification", _ => "unspecified"
    };

    private sealed record CandidateValue(AnalysisThesisEventDto Event, string Role, string RangeKind,
        CapturedFile File, SpanMappingResult.Exact Span);
    private static bool Candidate(RenderedRoleBindingRequest request, RenderedExtentAuthority authority, out CandidateValue value)
    {
        value = null!;
        var report = request.Report; var t = report.Result.Thesis!;
        if (!t.CompleteLiteralScan || t.Limitations.IsDefault || !t.Limitations.IsEmpty || t.Events.IsDefault
            || request.EventIndex < 0 || request.EventIndex >= t.Events.Length || report.Result.Knu is not { } knu) return false;
        var e = t.Events[request.EventIndex];
        if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Length > 512 || !PdfHashes.ValidUtf16(e.Text)
            || e.Text.Any(c => char.IsControl(c) || c == '\ufffd')
            || t.Events.Count(other => other.Text == e.Text) != 1 || !t.ClosureFileIds.Contains(e.FileId)) return false;
        var file = report.Snapshot.Files.SingleOrDefault(f => f.FileId.ToString() == e.FileId);
        if (report.Result.Profile.Version == AnalysisCatalog.CaptionRolesVersion)
            return file is not null && ProjectCaption(request.EventIndex, e, t, file, authority, out value);
        var observations = knu.Files.Where(f => f.FileId == e.FileId).ToArray();
        if (file is null || observations.Length != 1 || observations[0] is not { Eligible: true, Complete: true } facts
            // Margin completeness does not classify a caption or heading. Every
            // semantic/include/package uncertainty still refuses this proof.
            || facts.Limitations.IsDefault || facts.Limitations.Any(l => l != AnalysisLimitation.MissingMarginSides)
            || facts.Observations.IsDefault) return false;
        var matched = facts.Observations.Where(o => o.ValueSpan == e.Span).ToArray();
        if (matched.Length != 1 || matched[0] is not { ExcerptTruncated: false, Reason: null } observation
            || observation.Excerpt != e.Text || e.Span.StartByte >= e.Span.EndByte
            || SpanMapping.Map(file, e.FileId, e.Span.StartByte, e.Span.EndByte) is not SpanMappingResult.Exact span
            || !TextContent.TryDecode(file.Bytes.ToArray(), out var text, out _)
            || text[span.SourceStart..span.SourceEnd] != e.Text
            || e.Text.Contains('\n') || e.Text.Contains('\r')) return false;
        string? role = null;
        if (e.Kind == "captionTitle" && observation.Method == "literal-caption-title"
            && observation.Value is AnalysisCaptionValueDto { FinalScalar: not null } caption)
            role = caption.Environment switch { AnalysisCaptionEnvironment.Figure => "figureCaption", AnalysisCaptionEnvironment.Table => "tableCaption", _ => null };
        else if (observation.Method == "literal-heading-argument"
            && observation.Value is AnalysisHeadingValueDto { TitleKind: AnalysisTitleKind.Display, FinalScalar: not null })
            role = e.Kind switch { "introduction" => "introduction", "conclusions" => "conclusions", "references" => "references", "appendix" => "appendix", _ => null };
        if (role is null) return false;
        value = new(e, role, e.Kind == "captionTitle" ? "captionText" : "headingText", file, span); return true;
    }

    private static bool ProjectCaption(int index, AnalysisThesisEventDto e, AnalysisThesisDto thesis,
        CapturedFile file, RenderedExtentAuthority authority, out CandidateValue value)
    {
        value = null!;
        if (e.Kind != "captionTitle" || thesis.CaptionRoleCapability != ThesisCaptionRoleCapability.Expected
            || thesis.CaptionRoles.IsDefaultOrEmpty || !CompatibleCaptionCompiler(authority)) return false;
        var typed = thesis.CaptionRoles.SingleOrDefault(r => r.EventIndex == index);
        if (typed is null || typed.FileId != e.FileId || typed.Span != e.Span || typed.ContextRootFileId != thesis.RootFileId
            || typed.Method != ThesisCaptionRoleCapability.Method || e.Span.StartByte >= e.Span.EndByte
            || SpanMapping.Map(file, e.FileId, e.Span.StartByte, e.Span.EndByte) is not SpanMappingResult.Exact span
            || !TextContent.TryDecode(file.Bytes.ToArray(), out var text, out _)
            || text[span.SourceStart..span.SourceEnd] != e.Text) return false;
        var role = typed.Environment switch
        {
            "figure" => "figureCaption",
            "table" => "tableCaption",
            _ => null,
        };
        if (role is null) return false;
        value = new(e, role, "captionText", file, span);
        return true;
    }

    private static bool CompatibleCaptionCompiler(RenderedExtentAuthority authority)
    {
        var snapshot = authority.Output.Snapshot;
        var selection = snapshot.Selection;
        if (authority.Ports.NativeProfile is not { } profile
            || profile != NativeInputBudgetContract.Profile && !NativeSelectedPdfContract.Supports(profile)) return false;
        // The additive runtime contains identical bytes for the 11 pinned article/T2A
        // caption context files. Membership and project shadowing are checked separately.
        var component = NativeCompilerComponents.For(profile, NativeCompilerEngine.PdfLaTeX);
        var engine = new CompilationDescriptor(NativeRuntimePackage.EngineId, component.EngineVersion, new(component.WasmHash));
        return selection.Bundles.Length == 1 && SyncTeXRuntimeBinding.ForProfile(profile, selection.Bundles[0]) is not null
            && selection.Engine == engine && authority.Ports.Engine == engine
            && authority.Output.OutputAdapter == component.OutputAdapter
            && snapshot.Files.All(file => file.MediaKind == "text" && ThesisCaptionRoleCapability.IsContextFile(file.RelativePath));
    }

    private static bool CharacterRange(PdfPageInspection page, string literal, out int start, out int end, out PdfPoint[] points)
    {
        start = end = 0; points = [];
        var text = new StringBuilder(); var offsets = new List<int>();
        for (int i = 0; i < page.Glyphs.Count; i++)
        {
            var glyph = page.Glyphs[i];
            if (glyph.Index != i || !Rune.IsValid(glyph.UnicodeValue)) return false;
            offsets.Add(text.Length); text.Append(new Rune(glyph.UnicodeValue));
        }
        offsets.Add(text.Length); string all = text.ToString(); int at = all.IndexOf(literal, StringComparison.Ordinal);
        if (at < 0 || all.IndexOf(literal, at + 1, StringComparison.Ordinal) >= 0) return false;
        start = offsets.IndexOf(at); end = offsets.IndexOf(at + literal.Length);
        if (start < 0 || end <= start) return false;
        var found = new List<PdfPoint>();
        for (int i = start; i < end; i++)
        {
            var g = page.Glyphs[i];
            if (Rune.IsWhiteSpace(new(g.UnicodeValue))) continue;
            if (g.BoxStatus != "available" || g.Box is not { } b || !Finite(b)
                || b.Right <= b.Left || b.Top <= b.Bottom) return false;
            found.Add(new((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2));
        }
        points = found.ToArray(); return points.Length > 0;
    }

    private bool Transform(SyncTeXNavigationResult forward, SyncTeXNavigationMatch match, PdfPageInspection page,
        out SourceMapMatrix matrix, out PdfBox bounds)
    {
        matrix = default; bounds = default;
        if (forward.Geometry is null)
        {
            if (forward.Snapshot.Selection.Engine.Id is not ("pdftex" or "pdflatex")
                || page.MediaBox.Left != 0 || page.MediaBox.Bottom != 0) return false;
            matrix = new(1, 0, 0, -1, 0, page.MediaBox.Top);
        }
        else
        {
            if (navigation.PageTransform(forward, match.Page) is not { Status: "available", Reason: "none", WriterToPdfRaw: { } m }) return false;
            matrix = m;
        }
        var b = match.Box;
        if (b.Width <= 0 || b.Height + b.Depth <= 0) return false;
        var transform = matrix;
        var corners = new[] { new PdfPoint(b.H, b.V - b.Height), new(b.H + b.Width, b.V - b.Height),
            new(b.H, b.V + b.Depth), new(b.H + b.Width, b.V + b.Depth) }.Select(p => Apply(transform, p)).ToArray();
        bounds = new(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
        return Finite(bounds);
    }
    private static PdfPoint Apply(SourceMapMatrix m, PdfPoint p) => new(m.A * p.X + m.C * p.Y + m.E, m.B * p.X + m.D * p.Y + m.F);
    private static bool Contains(PdfBox b, PdfPoint p) => p.X >= b.Left && p.X <= b.Right && p.Y >= b.Bottom && p.Y <= b.Top;
    private static bool Finite(PdfBox b) => double.IsFinite(b.Left) && double.IsFinite(b.Bottom) && double.IsFinite(b.Right) && double.IsFinite(b.Top);
}
