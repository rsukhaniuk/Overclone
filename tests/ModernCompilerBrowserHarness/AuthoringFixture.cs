namespace ModernCompilerBrowserHarness;

// Explicit test document configuration, not default product typography.
internal static class AuthoringFixture
{
    internal const string Main = """
        \documentclass[11pt]{article}
        \renewcommand{\rmdefault}{n6dv}
        \renewcommand{\familydefault}{n6dv}
        \fontfamily{n6dv}\selectfont
        \usepackage[T2A,OT1]{fontenc}
        \DeclareFontSubstitution{T2A}{n6dv}{m}{n}
        \usepackage[utf8]{inputenc}
        \usepackage{amsmath}
        % Explicit available CM math sizes; not a claimed thesis standard.
        \DeclareMathSizes{11}{10}{7}{5}
        \pagestyle{empty}
        \pdfcompresslevel=0
        \pdfobjcompresslevel=0
        \tracinglostchars=2
        \pdfmapfile{+n6.map}
        \input n6-unicode.tex
        \newcommand{\ukr}[1]{{\fontencoding{T2A}\selectfont #1}}
        \makeatletter
        \typeout{PROFILE-KERNEL=\fmtversion;PATCH=\patch@level}
        \makeatother
        \begin{document}
        \fontsize{11}{15}\selectfont
        \section{Combined authoring}\label{sec:main}
        ASCII context: Kyiv 123.\par
        \noindent\ukr{Іі Її Єє Ґґ}\par
        \noindent\ukr{Привіт, Україно! Українська мова: Київ, їжак, ґанок, Європа.}\par
        {\bfseries
        ASCII context: Kyiv 123.\par
        \noindent\ukr{Іі Її Єє Ґґ}\par
        \noindent\ukr{Привіт, Україно! Українська мова: Київ, їжак, ґанок, Європа.}\par}
        \begin{equation}\label{eq:probe} x^2+y^2=z^2.\end{equation}
        Main cites book \cite{bookTwo}. Included section \ref{sec:included}.
        \input{chapter}
        % Explicit CM bibliography supports plain.bst's Latin italics. No fake
        % DejaVu italic definition or silent NFSS shape substitution is supplied.
        {\fontencoding{OT1}\fontfamily{cmr}\selectfont
        \bibliographystyle{plain}\bibliography{references}}
        \end{document}
        """;
    internal const string Chapter = """
        \section{Included chapter}\label{sec:included}
        Chapter cites article \cite{articleOne}. Back reference \ref{sec:main}.
        \par Equation~\eqref{eq:probe} is resolved.
        \par\ukr{Київ: український текст у другому файлі.}
        """;
    internal const string Bibliography = """
        @article{articleOne,
          author = {Adams, Alice and Baker, Bob},
          title = {{Offline Citation Evidence}},
          journal = {Journal of Offline Tests},
          year = {2024},
          volume = {7}, number = {2}, pages = {11--19}
        }
        @book{bookTwo,
          author = {Zimmer, Zoe},
          title = {{Deterministic Reference Manual}},
          publisher = {Local Proof Press}, address = {Kyiv}, year = {2023}
        }
        """;
}
