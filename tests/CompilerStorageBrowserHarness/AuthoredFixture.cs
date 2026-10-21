namespace CompilerStorageBrowserHarness;
// Exact authored MainSource/Chapter/Bib declarations from accepted N11 fixture.
public static class AuthoredFixture
{
    public static string MainSource(string marker) => "% Україна 😀 ordinary UTF8 source comment\n\\documentclass{article}\n\\begin{document}\n\\section{Root}\\label{sec:root}\nDIRTY-MAIN-" + marker + ". Included section \\ref{sec:chapter}. Book~\\cite{adams}. Article~\\cite{baker}.\n$\\int_0^1 x^2\\,dx=1/3$.\n\\include{chapter}\n\\bibliographystyle{plain}\n\\bibliography{refs}\n\\end{document}\n";
    public static string Chapter(string marker) => "% Київ 😀 dirty include comment\n\\section{Included}\\label{sec:chapter}\nDIRTY-INCLUDE-" + marker + ". Back reference \\ref{sec:root}.\n";
    public const string Bib = "@book{adams,author={Alice Adams},title={N11 Ordinary Book},year={2024},publisher={Example Press}}\n@article{baker,author={Bob Baker},title={N11 Ordinary Article},journal={Example Journal},year={2023},volume={1},pages={1--2}}\n";
}
