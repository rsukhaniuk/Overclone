using System.Collections.Immutable;

namespace Client.Compilation.Native;

public enum NativeCompilerEngine { PdfLaTeX, LaTeXDvi, XeLaTeX, LuaLaTeX }

/// <summary>Closed suite phase choices (profiles5/6). C# owns finite passes, not the worker.</summary>
public static class NativeCompilerSuiteContract
{
    public const int Files = 8192, FileBytes = 64 * 1024 * 1024, TotalBytes = 256 * 1024 * 1024;
    public const string ResourcePrefix = "__oop_runtime__/";
    public static TimeSpan JobDeadline => TimeSpan.FromSeconds(90);
    public static TimeSpan LifecycleDeadline => TimeSpan.FromMinutes(2);
    public static int Phase(NativeCompilerEngine engine) => engine switch
    { NativeCompilerEngine.PdfLaTeX => 6, NativeCompilerEngine.LaTeXDvi => 7, NativeCompilerEngine.XeLaTeX => 8, NativeCompilerEngine.LuaLaTeX => 9, _ => 0 };
    public static string EngineId(NativeCompilerEngine engine) => engine switch
    { NativeCompilerEngine.PdfLaTeX => "pdftex", NativeCompilerEngine.LaTeXDvi => "latex-dvi", NativeCompilerEngine.XeLaTeX => "xelatex", NativeCompilerEngine.LuaLaTeX => "lualatex", _ => "" };
    public static int Phase(NativeRuntimeProfile profile, NativeCompilerEngine engine) => !NativeRuntimeProfiles.IsSuite(profile) ? 0
        : NativeRuntimeProfiles.SupportsBiber(profile) ? engine switch
        { NativeCompilerEngine.PdfLaTeX => NativeBiberCompilerContract.PdfPhase, NativeCompilerEngine.LaTeXDvi => NativeBiberCompilerContract.DviPhase, NativeCompilerEngine.XeLaTeX => NativeUnicodeCompilerContract.XePhase, NativeCompilerEngine.LuaLaTeX => NativeUnicodeCompilerContract.LuaPhase, _ => 0 }
        : profile == NativeUnicodeCompilerContract.Profile ? engine switch
        { NativeCompilerEngine.PdfLaTeX => 6, NativeCompilerEngine.LaTeXDvi => NativeGraphicsNavigationContract.DviPhase, NativeCompilerEngine.XeLaTeX => NativeUnicodeCompilerContract.XePhase, NativeCompilerEngine.LuaLaTeX => NativeUnicodeCompilerContract.LuaPhase, _ => 0 }
        : profile == NativeGraphicsNavigationContract.Profile ? engine switch
        { NativeCompilerEngine.PdfLaTeX => 6, NativeCompilerEngine.LaTeXDvi => NativeGraphicsNavigationContract.DviPhase, NativeCompilerEngine.XeLaTeX => NativeGraphicsNavigationContract.XePhase, NativeCompilerEngine.LuaLaTeX => NativeGraphicsNavigationContract.LuaPhase, _ => 0 }
        : profile == NativeXeNavigationContract.Profile ? engine switch
        { NativeCompilerEngine.PdfLaTeX => 6, NativeCompilerEngine.LaTeXDvi => 12, NativeCompilerEngine.XeLaTeX => 13, NativeCompilerEngine.LuaLaTeX => 14, _ => 0 }
        : profile == NativeNavigationContract.Profile ? engine switch
        { NativeCompilerEngine.PdfLaTeX => 6, NativeCompilerEngine.LaTeXDvi => 12, NativeCompilerEngine.XeLaTeX => 8, NativeCompilerEngine.LuaLaTeX => 14, _ => 0 }
        : Phase(engine);
    public static CompilationOption[] SelectionOptions(NativeRuntimeProfile profile, NativeCompilerEngine engine) =>
        [new("overclone-native-phase-contract", Phase(profile, engine).ToString(System.Globalization.CultureInfo.InvariantCulture))];
    public static bool MatchesOptions(IEnumerable<CompilationOption> options, NativeRuntimeProfile profile, NativeCompilerEngine engine) =>
        Phase(profile, engine) != 0 && options.SequenceEqual(SelectionOptions(profile, engine));
    public static bool MatchesOptions(IEnumerable<CompilationOption> options, NativeRuntimeProfile profile) =>
        Enum.GetValues<NativeCompilerEngine>().Any(engine => MatchesOptions(options, profile, engine));
    public static CompilationOption[] SelectionOptions(NativeCompilerEngine engine) => [new("overclone-native-phase-contract", Phase(engine).ToString(System.Globalization.CultureInfo.InvariantCulture))];
    public static bool MatchesOptions(IEnumerable<CompilationOption> options, NativeCompilerEngine engine) => Phase(engine) != 0 && options.SequenceEqual(SelectionOptions(engine));
    public static bool MatchesOptions(IEnumerable<CompilationOption> options) => Enum.GetValues<NativeCompilerEngine>().Any(e => MatchesOptions(options, e));
    public static NativeCompilePlan CreatePlan(NativeCompilerEngine engine, int maxLatexPasses = 6, TimeSpan? deadline = null,
        NativeRuntimeProfile profile = NativeRuntimeProfile.LocalCompilerSuite) => new(false,
        maxLatexPasses, deadline ?? JobDeadline, Files + NativeRuntimeProfiles.PhaseFileAllowance(profile), TotalBytes, FileBytes,
        UseSelectionTools: true, Profile: profile, PhaseProtocolVersion: Phase(profile, engine),
        DviOutput: engine == NativeCompilerEngine.LaTeXDvi, Engine: engine);
    public static CompileOutputDescriptor DviOutputAdapter => new("native-latex-dvi", "dvi-v1", new(NativeCompilerSuitePins.ConverterWasmHash));
    public static CompileOutputDescriptor XeOutputAdapter => new("native-xetex", "xdv-v1", new(NativeCompilerSuitePins.ConverterWasmHash));
    // Access only once final pins are ready: no invented digest represents a working engine.
    public static CompileOutputDescriptor LuaOutputAdapter => new("native-luatex", "abi2", new(NativeCompilerSuitePins.LuaWasmHash));
    public static bool ResourceName(string? name)
    {
        if (name is not { Length: > 0 and <= 200 }) return false;
        var parts = name.Split('/');
        return parts.Length <= 12 && (parts.Length == 1 || name.EndsWith(".lua", StringComparison.Ordinal))
            && parts.All(p => p.Length is > 0 and <= 100 && p is not ("." or "..")
                && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
    }
    public static NativeSelectedEntry? CreateEntry(string selected, IEnumerable<string> projectNames, NativeCompilerEngine engine, out string? error)
        => CreateEntry(selected, projectNames, engine, out error, NativeRuntimeProfile.LocalCompilerSuite);
    public static NativeSelectedEntry? CreateEntry(string selected, IEnumerable<string> projectNames, NativeCompilerEngine engine,
        out string? error, NativeRuntimeProfile profile)
    {
        if (Phase(profile, engine) == 0) { error = "unsupported-suite-engine"; return null; }
        error = NativeSelectedEntryContract.ValidateEntry(selected, profile, engine);
        if (error is not null) return null;
        if (engine == NativeCompilerEngine.XeLaTeX && profile is not (NativeUnicodeCompilerContract.Profile or NativeBiberCompilerContract.Profile or NativeThesisCompilerContract.Profile or NativeOriginCompilerContract.Profile) && selected.Any(char.IsSurrogate))
        { error = "xetex-non-bmp-main-unsupported"; return null; }
        if (engine != NativeCompilerEngine.LuaLaTeX)
        {
            var entry = NativeSelectedEntryContract.Create(selected, projectNames, out error,
                engine == NativeCompilerEngine.LaTeXDvi, profile, engine);
            if (entry is null || engine != NativeCompilerEngine.XeLaTeX) return entry;
            return entry with { LatexEntry = selected, Outputs = entry.Outputs.Add(entry.JobName + ".xdv") };
        }
        // ABI2 has a fixed native jobname; never rewrite the source to change it.
        var outputs = new[] { ".pdf", ".aux", ".bbl", ".blg", ".log", ".toc", ".lof", ".lot", ".out" }
            .Select(ext => "result" + ext).Concat(projectNames.Where(n => n != selected && n.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[..^4] + ".aux")).ToImmutableArray();
        var state = outputs.Where(n => Path.GetExtension(n) is ".aux" or ".bbl" or ".toc" or ".lof" or ".lot" or ".out").ToImmutableArray();
        var result = new NativeSelectedEntry(selected, selected, "result.tex", "result", outputs, state);
        error = NativeSelectedEntryContract.ValidateNamespace(result, projectNames, []);
        return error is null ? result : null;
    }
}
