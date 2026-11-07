using System.Collections.Immutable;

namespace Client.Compilation.Native;

/// <summary>Closed P13 component and bibliography boundary. No default selection.</summary>
public static class NativeBiberCompilerContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerBiber;
    public const int PdfPhase = 23, DviPhase = 24, BiberPhase = 10;
    public const int InputFiles = 64, InputBytes = 8 * 1024 * 1024;
    public const int BootstrapBytes = 32 * 1024 * 1024, OutputBytes = 8 * 1024 * 1024;

    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var previous = NativeUnicodeCompilerContract.Component(engine);
        return engine switch
        {
            NativeCompilerEngine.PdfLaTeX or NativeCompilerEngine.LaTeXDvi => previous with
            {
                LoaderName = "semantic-engine.js", LoaderLength = NativeCompilerBiberPins.SemanticLoaderLength,
                LoaderHash = NativeCompilerBiberPins.SemanticLoaderHash,
                WasmName = "semantic-engine.wasm", WasmLength = NativeCompilerBiberPins.SemanticWasmLength,
                WasmHash = NativeCompilerBiberPins.SemanticWasmHash,
                FormatLength = engine == NativeCompilerEngine.PdfLaTeX ? NativeCompilerBiberPins.PdfFormatLength : NativeCompilerBiberPins.DviFormatLength,
                FormatHash = engine == NativeCompilerEngine.PdfLaTeX ? NativeCompilerBiberPins.PdfFormatHash : NativeCompilerBiberPins.DviFormatHash,
                EngineVersion = "semantics-abi1", NativeAbiVersion = 1, SupportsSourceMap = true,
                OutputAdapter = engine == NativeCompilerEngine.PdfLaTeX
                    ? new("native-pdftex", "semantics-abi1", new(NativeCompilerBiberPins.SemanticWasmHash))
                    : new("native-latex-dvi", "geometry-abi3", new(NativeCompilerBiberPins.ConverterWasmHash))
            },
            NativeCompilerEngine.LuaLaTeX => previous with
            {
                LoaderLength = NativeCompilerBiberPins.LuaLoaderLength, LoaderHash = NativeCompilerBiberPins.LuaLoaderHash,
                WasmLength = NativeCompilerBiberPins.LuaWasmLength, WasmHash = NativeCompilerBiberPins.LuaWasmHash,
                FormatLength = NativeCompilerBiberPins.LuaFormatLength, FormatHash = NativeCompilerBiberPins.LuaFormatHash,
                EngineVersion = "abi5-graphics1-harfbuzz1-fonts1-text1",
                OutputAdapter = previous.OutputAdapter with { Version = "abi5-graphics1-harfbuzz1-fonts1-text1", Hash = new(NativeCompilerBiberPins.LuaWasmHash) }
            },
            _ => previous
        };
    }

    public static bool NoticeName(string? name) => name is { Length: > 0 and <= 200 }
        && name.StartsWith("notices/biber/", StringComparison.Ordinal)
        && name.Split('/') is { Length: <= 12 } parts
        && parts.All(p => p.Length is > 0 and <= 100 && p is not ("." or "..")
            && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));

    // The original executable remains installed solely for genuine BibTeX.
    public static NativeCompilerComponent BibtexComponent => NativeUnicodeCompilerContract.Component(NativeCompilerEngine.PdfLaTeX);
    public static NativeSelectedEntry ExtendEntry(NativeSelectedEntry entry) => entry with
    {
        Outputs = entry.Outputs.Add(NativeBiberControlContract.BcfName(entry)).Add(NativeBiberControlContract.RunXmlName(entry))
        // Neither BCF nor volatile run.xml is carried. Each TeX pass must produce
        // its own BCF; stale seeded control can never masquerade as current data.
    };
    public static string? ValidateNamespace(NativeSelectedEntry entry, IEnumerable<string> inputs, IEnumerable<string> carried)
    {
        foreach (var name in entry.Outputs)
            if (name.EndsWith(".bib", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".bcf", StringComparison.OrdinalIgnoreCase) && name != NativeBiberControlContract.BcfName(entry)
                || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && name != NativeBiberControlContract.RunXmlName(entry))
                return "bibliography-invalid-output";
        return NativeSelectedEntryContract.ValidateNamespace(entry, inputs, carried);
    }
}
