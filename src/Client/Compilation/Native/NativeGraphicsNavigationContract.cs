namespace Client.Compilation.Native;

/// <summary>Only explicit P10 cells select graphics1; correlation data is not execution authority.</summary>
public static class NativeGraphicsNavigationContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerGraphicsNavigation;
    public const int DviPhase = 15, XePhase = 16, LuaPhase = 17;
    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var previous = NativeXeNavigationContract.Component(engine);
        return engine switch
        {
            NativeCompilerEngine.LaTeXDvi => previous with
            {
                OutputAdapter = new("native-latex-dvi", "geometry-abi2-graphics1", new(NativeCompilerGraphicsNavigationPins.DviWasmHash))
            },
            NativeCompilerEngine.XeLaTeX => previous with
            {
                OutputAdapter = new("native-xetex", "geometry-abi3-graphics1", new(NativeCompilerGraphicsNavigationPins.XeWasmHash))
            },
            NativeCompilerEngine.LuaLaTeX => previous with
            {
                LoaderLength = NativeCompilerGraphicsNavigationPins.LuaLoaderLength, LoaderHash = NativeCompilerGraphicsNavigationPins.LuaLoaderHash,
                WasmLength = NativeCompilerGraphicsNavigationPins.LuaWasmLength, WasmHash = NativeCompilerGraphicsNavigationPins.LuaWasmHash,
                EngineVersion = "abi5-graphics1", OutputAdapter = new("native-luatex", "abi5-graphics1", new(NativeCompilerGraphicsNavigationPins.LuaWasmHash))
            },
            _ => previous
        };
    }
}
