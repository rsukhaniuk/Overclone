using Client.Compilation.Native;

namespace Client.Analysis;

/// <summary>The producer's exact finite package and class context for caption title evidence.</summary>
internal static class ThesisCaptionRoleCapability
{
    internal const string Method = "literal-project-caption-title";
    internal static readonly AnalysisThesisCaptionRoleCapabilityDto Expected = new(
        "p16-article-t2a-ukrainian-caption-v1",
        (int)NativeInputBudgetContract.Profile,
        NativeCompilerInputBudgetPins.ClosureHash,
        "73d1e45bfccbc0048189e355a3a7989578d120e22eb1a95433fec1813bafa6d3");

    // The analyzer receives text files only. Some of the eleven pinned source
    // assets (such as .def and .ldf) can arrive as binary project inputs. Check
    // capture paths as well so such a file cannot shadow the confirmed context.
    private static readonly HashSet<string> PinnedSourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "article.cls", "babel-ukrainian.tex", "babel.sty", "fontenc.sty", "l3backend-pdftex.def",
        "size10.clo", "t2acmr.fd", "t2aenc.def", "t2aenc.dfu", "txtbabel.def", "ukraineb.ldf",
    };

    internal static bool IsContextFile(string relativePath)
        => AnalysisCatalog.IsTex(relativePath)
            && !PinnedSourceNames.Contains(relativePath[(relativePath.LastIndexOf('/') + 1)..]);
}
