namespace Client.Compilation.Native;

/// <summary>Closed P14 thesis package and bounded project expansion. Adoption is separate.</summary>
public static class NativeThesisCompilerContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerThesis;
    public const int ProjectFiles = 128, CarriedFiles = 128, Outputs = 256, PhaseFileAllowance = 256;
    // Native Lua registers these before consuming the requested declarations.
    // Its 256 slots are the exact union, not 256 further requested names.
    private static readonly string[] LuaFixedOutputs = ["result.pdf", "result.log", "lualatex.fmt", "result.aux", "result.toc",
        "result.out", "result.bbl", "result.blg", "result.lof", "result.lot"];

    public static bool LuaOutputDeclarationsFit(IReadOnlyList<string> outputs)
    {
        if (outputs.Count > Outputs) return false;
        var declared = new HashSet<string>(LuaFixedOutputs, StringComparer.Ordinal);
        foreach (var name in outputs)
        {
            declared.Add(name);
            if (declared.Count > Outputs) return false;
        }
        return true;
    }

    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var previous = NativeBiberCompilerContract.Component(engine);
        return engine switch
        {
            NativeCompilerEngine.XeLaTeX => previous with
            {
                LoaderLength = NativeCompilerThesisPins.XeLoaderLength, LoaderHash = NativeCompilerThesisPins.XeLoaderHash,
                WasmLength = NativeCompilerThesisPins.XeWasmLength, WasmHash = NativeCompilerThesisPins.XeWasmHash
            },
            NativeCompilerEngine.LuaLaTeX => previous with
            {
                LoaderLength = NativeCompilerThesisPins.LuaLoaderLength, LoaderHash = NativeCompilerThesisPins.LuaLoaderHash,
                WasmLength = NativeCompilerThesisPins.LuaWasmLength, WasmHash = NativeCompilerThesisPins.LuaWasmHash,
                OutputAdapter = previous.OutputAdapter with { Hash = new(NativeCompilerThesisPins.LuaWasmHash) }
            },
            _ => previous
        };
    }

    public static bool NoticeName(string? name) => NativeBiberCompilerContract.NoticeName(name)
        || name is { Length: > 0 and <= 200 } && name.StartsWith("notices/thesis-packages/", StringComparison.Ordinal)
        && name.Split('/') is { Length: <= 12 } parts
        && parts.All(p => p.Length is > 0 and <= 100 && p is not ("." or "..")
            && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
}
