namespace Client.Compilation.Native;

/// <summary>Trusted application composition only; never selected by a project or catalog.</summary>
public enum NativeRuntimeProfile { Legacy = 0, LocalLatex2023 = 1, LocalLatex2023Authoring = 2, LocalLatex2023SyncTeX = 3, LocalLatex2023Dvi = 4, LocalCompilerSuite = 5, LocalCompilerCompatibility = 6, LocalCompilerBibliography = 7 }

/// <summary>Closed, versioned technical profiles, not adoption/license/isolation approval.</summary>
public static class NativeRuntimeProfiles
{
    public const int ContractVersion = 1;
    public static bool IsSuite(NativeRuntimeProfile profile) => profile is NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography;
    public static bool HasBibliographyCarry(NativeRuntimeProfile profile) => profile == NativeRuntimeProfile.LocalCompilerBibliography;
    public static bool SuitePinsReady(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerSuite => NativeCompilerSuitePins.Ready, NativeRuntimeProfile.LocalCompilerCompatibility => NativeCompilerCompatibilityPins.Ready, NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerBibliographyPins.Ready, _ => false };
    public static int SuiteTotalBytes(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerSuite => NativeCompilerSuitePins.TotalBytes, NativeRuntimeProfile.LocalCompilerCompatibility => NativeCompilerCompatibilityPins.TotalBytes, NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerBibliographyPins.TotalBytes, _ => 0 };
    public static bool MatchesSuiteComponents(NativeRuntimeProfile profile, IReadOnlyDictionary<string, NativeResourcePin> pins) => profile switch
    {
        NativeRuntimeProfile.LocalCompilerSuite => NativeCompilerSuitePins.MatchesComponents(pins)
            && pins.TryGetValue("latex-dvi.fmt", out var format) && format.Length == DviFormatLength && format.Sha256 == DviFormatHash,
        NativeRuntimeProfile.LocalCompilerCompatibility => NativeCompilerCompatibilityPins.MatchesComponents(pins),
        NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerBibliographyPins.MatchesComponents(pins),
        _ => false
    };
    public const int DviResourceCount = 507;
    public const string DviClosureHash = "f48f93c5176be1337486c2b432b829637a0a231737dba7eb7d65c69b6d4d0481";
    public const int DviFormatLength = 22342701;
    public const string DviFormatHash = "04c29d7c297a114c4173e3597dc2f886cefa84889f040e5d4ad897ce5e5d72d0";
    public const string DviConverterLoaderHash = "dc31543194583c105228dc791040c2f09afd7aa64311ce254b215c9fcd24e54a";
    public const string DviConverterWasmHash = "6dc117f6ca3b16cea8e675683486e1f641a933fbfdccc9de8f94f51224e18c61";
    public const int ModernResourceCount = 481;
    public const int AuthoringResourceCount = 497;
    public const int ModernFormatLength = 22341856;
    public const string ModernFormatHash = "e2b5174b5aa6e552e03e9cf5dae7a7594e68d75962f48a6a30dc55da588b2595";
    // Independently pinned accepted closures, not computed from received metadata.
    public const string ModernClosureHash = "fba51a137028de8370d608b3d90db6430bf62941657e57644f800432695bff0d";
    public const string AuthoringClosureHash = "0f6b7b885ff15fb3bcd11a49140d9f486406775fe0222f5925709eb5c9680a2e";
    public const int SyncTeXResourceCount = 499;
    public const int SyncTeXFormatLength = 22342013;
    public const string SyncTeXFormatHash = "2bdb429a75bd93ea2ffdaa4993a6a0d69ec81d39222f1244170da412c6ebb905";
    public const string SyncTeXClosureHash = "c451d32d6c4a2fd998d27f889fd68ea9b3f68afafee9594f8eea4a3157c7178f";
    public const string SyncTeXCompilerLoaderHash = "b514d2b72aa44ec47923735f8f1be43a93351931beb183d57a7429b73e74a9a9";
    public const string SyncTeXCompilerWasmHash = "70008f66d8acb88ee40e8c17b5a48dc81003852e569833444dd3cda7539ce235";
    public const string SyncTeXQueryLoaderHash = "94fb846cc1c474b2ee2c8334b060f108a6c4fc31def740022a7768767039eb33";
    public const string SyncTeXQueryWasmHash = "502f46777a8864280adc110dacea40b0bd842aaa785542dc7447e4789839d485";
    public const int SyncTeXCompilerLoaderLength = 72583;
    public const int SyncTeXCompilerWasmLength = 1782796;
    public const int SyncTeXQueryLoaderLength = 59378;
    public const int SyncTeXQueryWasmLength = 191653;
    public static string CompilerLoaderHash(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi or NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => SyncTeXCompilerLoaderHash, _ => Known(profile) ? NativeRuntimePackage.LoaderHash : "" };
    public static string CompilerWasmHash(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi or NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => SyncTeXCompilerWasmHash, _ => Known(profile) ? NativeRuntimePackage.WasmHash : "" };
    public static int CompilerLoaderLength(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi or NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => SyncTeXCompilerLoaderLength, _ => Known(profile) ? 72275 : 0 };
    public static int CompilerWasmLength(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi or NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => SyncTeXCompilerWasmLength, _ => Known(profile) ? 1779444 : 0 };
    public static bool Modern(NativeRuntimeProfile profile) => IsSuite(profile) || profile is NativeRuntimeProfile.LocalLatex2023 or NativeRuntimeProfile.LocalLatex2023Authoring or NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi;
    public static int MaxFiles(NativeRuntimeProfile profile) => IsSuite(profile) ? NativeCompilerSuiteContract.Files : 512;
    public static bool Known(NativeRuntimeProfile profile) => profile == NativeRuntimeProfile.Legacy || Modern(profile);
    public static int MaxFileBytes(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerSuiteContract.FileBytes, NativeRuntimeProfile.Legacy => 16777216, NativeRuntimeProfile.LocalLatex2023 or NativeRuntimeProfile.LocalLatex2023Authoring or NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi => 33554432, _ => 0 };
    public static int MaxTotalBytes(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerSuiteContract.TotalBytes, NativeRuntimeProfile.Legacy => 33554432, NativeRuntimeProfile.LocalLatex2023 or NativeRuntimeProfile.LocalLatex2023Authoring or NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi => 67108864, _ => 0 };
    public static int FormatLength(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerCompatibilityPins.PdfFormatLength, NativeRuntimeProfile.LocalCompilerSuite => SyncTeXFormatLength, NativeRuntimeProfile.Legacy => 715877, NativeRuntimeProfile.LocalLatex2023 or NativeRuntimeProfile.LocalLatex2023Authoring => ModernFormatLength, NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi => SyncTeXFormatLength, _ => 0 };
    public static string FormatHash(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerCompatibilityPins.PdfFormatHash, NativeRuntimeProfile.LocalCompilerSuite => SyncTeXFormatHash, NativeRuntimeProfile.Legacy => NativeRuntimePackage.FormatHash, NativeRuntimeProfile.LocalLatex2023 or NativeRuntimeProfile.LocalLatex2023Authoring => ModernFormatHash, NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi => SyncTeXFormatHash, _ => "" };
    public static int InstalledCount(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerBibliographyPins.Ready ? NativeCompilerBibliographyPins.ResourceCount : 0, NativeRuntimeProfile.LocalCompilerCompatibility => NativeCompilerCompatibilityPins.Ready ? NativeCompilerCompatibilityPins.ResourceCount : 0, NativeRuntimeProfile.LocalCompilerSuite => NativeCompilerSuitePins.Ready ? NativeCompilerSuitePins.ResourceCount : 0, NativeRuntimeProfile.Legacy => InstalledNativeRuntimeProvider.ResourceCount, NativeRuntimeProfile.LocalLatex2023 => ModernResourceCount, NativeRuntimeProfile.LocalLatex2023Authoring => AuthoringResourceCount, NativeRuntimeProfile.LocalLatex2023SyncTeX => SyncTeXResourceCount, NativeRuntimeProfile.LocalLatex2023Dvi => DviResourceCount, _ => 0 };
    public static string ClosureHash(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerBibliography => NativeCompilerBibliographyPins.Ready ? NativeCompilerBibliographyPins.ClosureHash : "", NativeRuntimeProfile.LocalCompilerCompatibility => NativeCompilerCompatibilityPins.Ready ? NativeCompilerCompatibilityPins.ClosureHash : "", NativeRuntimeProfile.LocalCompilerSuite => NativeCompilerSuitePins.Ready ? NativeCompilerSuitePins.ClosureHash : "", NativeRuntimeProfile.LocalLatex2023 => ModernClosureHash, NativeRuntimeProfile.LocalLatex2023Authoring => AuthoringClosureHash, NativeRuntimeProfile.LocalLatex2023SyncTeX => SyncTeXClosureHash, NativeRuntimeProfile.LocalLatex2023Dvi => DviClosureHash, _ => "" };
    public static string IdentityName(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerBibliography => "local-compiler-suite-v3", NativeRuntimeProfile.LocalCompilerCompatibility => "local-compiler-suite-v2", NativeRuntimeProfile.LocalCompilerSuite => "local-compiler-suite-v1", NativeRuntimeProfile.LocalLatex2023 => "local-latex-2023-11-01-p1", NativeRuntimeProfile.LocalLatex2023Authoring => "local-latex-2023-11-01-p1-authoring", NativeRuntimeProfile.LocalLatex2023SyncTeX => "local-latex-2023-11-01-p1-synctex", NativeRuntimeProfile.LocalLatex2023Dvi => "local-latex-2023-11-01-p1-dvi", _ => "" };
    public static int ProtocolVersion(NativeRuntimeProfile profile) => profile switch
    { NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography => 6, NativeRuntimeProfile.Legacy => 1, NativeRuntimeProfile.LocalLatex2023 => 2, NativeRuntimeProfile.LocalLatex2023Authoring => 3, NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi => 6, _ => 0 };
}
