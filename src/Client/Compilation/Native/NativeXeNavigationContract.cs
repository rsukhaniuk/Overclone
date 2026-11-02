namespace Client.Compilation.Native;

/// <summary>Only P9/Xe phase13 selects this producer, format and XDV7 converter.</summary>
public static class NativeXeNavigationContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerXeNavigation;
    public const int Phase = 13;
    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var previous = NativeNavigationContract.Component(engine);
        return engine != NativeCompilerEngine.XeLaTeX ? previous : previous with
        {
            LoaderLength = NativeCompilerXeNavigationPins.LoaderLength, LoaderHash = NativeCompilerXeNavigationPins.LoaderHash,
            WasmLength = NativeCompilerXeNavigationPins.WasmLength, WasmHash = NativeCompilerXeNavigationPins.WasmHash,
            FormatLength = NativeCompilerXeNavigationPins.FormatLength, FormatHash = NativeCompilerXeNavigationPins.FormatHash,
            EngineVersion = "maps-abi1", NativeAbiVersion = 1, SupportsSourceMap = true,
            OutputAdapter = new("native-xetex", "geometry-abi3", new(NativeCompilerXeNavigationPins.ConverterWasmHash))
        };
    }
}
