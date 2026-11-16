namespace Client.Compilation.Native;

/// <summary>Closed origin/neutral writer and area64 query derivative of P14.
/// Native schema1 matrices stay in visible-bp to unrotated PDF raw space.</summary>
public static class NativeOriginCompilerContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerOrigins;
    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var prior = NativeThesisCompilerContract.Component(engine) with
        {
            QueryLoaderHash = NativeCompilerOriginPins.QueryLoaderHash,
            QueryWasmHash = NativeCompilerOriginPins.QueryWasmHash
        };
        return engine switch
        {
            NativeCompilerEngine.LaTeXDvi => prior with
            {
                OutputAdapter = prior.OutputAdapter with { Version = prior.OutputAdapter.Version + "-origin1-neutral1", Hash = new(NativeCompilerOriginPins.DviConverterWasmHash) }
            },
            NativeCompilerEngine.XeLaTeX => prior with
            {
                OutputAdapter = prior.OutputAdapter with { Version = prior.OutputAdapter.Version + "-origin1-neutral1", Hash = new(NativeCompilerOriginPins.XeConverterWasmHash) }
            },
            NativeCompilerEngine.LuaLaTeX => prior with
            {
                LoaderLength = NativeCompilerOriginPins.LuaLoaderLength, LoaderHash = NativeCompilerOriginPins.LuaLoaderHash,
                WasmLength = NativeCompilerOriginPins.LuaWasmLength, WasmHash = NativeCompilerOriginPins.LuaWasmHash,
                EngineVersion = prior.EngineVersion + "-origin1-neutral1",
                OutputAdapter = prior.OutputAdapter with { Version = prior.OutputAdapter.Version + "-origin1-neutral1", Hash = new(NativeCompilerOriginPins.LuaWasmHash) }
            },
            _ => prior
        };
    }
}
