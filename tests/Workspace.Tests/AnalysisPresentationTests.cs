using Client.Analysis;
using Xunit;

namespace Workspace.Tests;

public class AnalysisPresentationTests
{
    [Theory]
    [InlineData(AnalysisWorkType.Unspecified, "unspecified", "Unspecified")]
    [InlineData(AnalysisWorkType.Coursework, "coursework", "Coursework")]
    [InlineData(AnalysisWorkType.BachelorQualification, "bachelorQualification", "Bachelor")]
    [InlineData(AnalysisWorkType.MasterQualification, "masterQualification", "Master")]
    public void Work_type_labels_preserve_explicit_unspecified_and_wire_values(AnalysisWorkType work, string wire, string label)
    {
        Assert.Equal(wire, AnalysisPresentation.WorkValue(work));
        Assert.Contains(label, AnalysisPresentation.WorkType(work));
    }

    [Fact]
    public void Every_typed_limitation_has_readable_honest_display_text()
    {
        foreach (var reason in Enum.GetValues<AnalysisLimitation>())
        {
            var text = AnalysisPresentation.Limitation(reason);
            Assert.NotEqual("Unsupported analysis context.", text);
            Assert.True(text.Length > 25);
        }
        Assert.Contains("not expanded", AnalysisPresentation.Limitation(AnalysisLimitation.UnknownSemantics));
        Assert.Contains("not resolved", AnalysisPresentation.Limitation(AnalysisLimitation.IncludeNotResolved));
        Assert.Contains("no conversion", AnalysisPresentation.Limitation(AnalysisLimitation.UnsupportedLength));
    }
}
