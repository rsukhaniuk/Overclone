//! Authored concise signatures, not copied manuals or compile capability claims.
//! Catalog 1.0 is independent of provisioned runtime resources. Every package
//! entry is gated by a literal selected-root mention; local overrides remain
//! uncertain. URLs are provenance only: this module performs no IO.
//! Source observations: native/tex/package-compatibility/fixtures/{thesis-packages,
//! biblatex-bibtex,local-override}.tex; existing C# LatexCommandCompletion catalog;
//! Rust formatting/geometry/reference fixtures and heading grammar;
//! native/tex/suite-resources/fixtures/{amsmath,authoring}.tex and
//! native/tex/ukrainian-resources/fixtures/unicode.tex.
//! Official provenance below requires master
//! reference verification before integrated catalog promotion.
pub const VERSION: &str = "1.0";

pub struct Entry {
    pub name: &'static str,
    pub kind: &'static str,
    pub package: Option<&'static str>,
    pub insertion: &'static str,
    pub signature: &'static str,
    pub provenance: &'static str,
    pub engines: &'static str,
}

macro_rules! command {
    ($name:literal, $insert:literal, $sig:literal) => {
        Entry {
            name: $name,
            kind: "command",
            package: None,
            insertion: $insert,
            signature: $sig,
            provenance: "https://latex-project.org/help/documentation/source2e.pdf",
            engines: "all",
        }
    };
}
macro_rules! package {
    ($pkg:literal, $name:literal, $kind:literal, $insert:literal, $sig:literal, $url:literal, $engines:literal) => {
        Entry {
            name: $name,
            kind: $kind,
            package: Some($pkg),
            insertion: $insert,
            signature: $sig,
            provenance: $url,
            engines: $engines,
        }
    };
}

pub const ENTRIES: &[Entry] = &[
    command!("section", "\\section{}", "\\section{title}"),
    command!("subsection", "\\subsection{}", "\\subsection{title}"),
    command!(
        "subsubsection",
        "\\subsubsection{}",
        "\\subsubsection{title}"
    ),
    command!("paragraph", "\\paragraph{}", "\\paragraph{title}"),
    command!("textbf", "\\textbf{}", "\\textbf{text}"),
    command!("textit", "\\textit{}", "\\textit{text}"),
    command!("emph", "\\emph{}", "\\emph{text}"),
    command!("label", "\\label{}", "\\label{key}"),
    command!("ref", "\\ref{}", "\\ref{key}"),
    command!("pageref", "\\pageref{}", "\\pageref{key}"),
    command!("cite", "\\cite{}", "\\cite{key-list}"),
    command!("footnote", "\\footnote{}", "\\footnote{text}"),
    command!("caption", "\\caption{}", "\\caption{text}"),
    command!("title", "\\title{}", "\\title{text}"),
    command!("author", "\\author{}", "\\author{text}"),
    command!("date", "\\date{}", "\\date{text}"),
    command!("input", "\\input{}", "\\input{project-path}"),
    command!(
        "include",
        "\\include{}",
        "\\include{extensionless-project-path}"
    ),
    package!(
        "article",
        "section",
        "command",
        "\\section{}",
        "\\section{title}",
        "https://latex-project.org/help/documentation/classes.pdf",
        "all"
    ),
    package!(
        "report",
        "chapter",
        "command",
        "\\chapter{}",
        "\\chapter{title}",
        "https://latex-project.org/help/documentation/classes.pdf",
        "all"
    ),
    package!(
        "book",
        "chapter",
        "command",
        "\\chapter{}",
        "\\chapter{title}",
        "https://latex-project.org/help/documentation/classes.pdf",
        "all"
    ),
    package!(
        "amsmath",
        "eqref",
        "command",
        "\\eqref{}",
        "\\eqref{key}",
        "https://ctan.org/pkg/amsmath",
        "all"
    ),
    package!(
        "amsmath",
        "align",
        "environment",
        "align",
        "\\begin{align} … \\end{align}",
        "https://ctan.org/pkg/amsmath",
        "all"
    ),
    package!(
        "graphicx",
        "includegraphics",
        "command",
        "\\includegraphics{}",
        "\\includegraphics[options]{path}",
        "https://ctan.org/pkg/graphicx",
        "all"
    ),
    package!(
        "hyperref",
        "href",
        "command",
        "\\href{}{}",
        "\\href{url}{text}",
        "https://ctan.org/pkg/hyperref",
        "all; driver dependent"
    ),
    package!(
        "cleveref",
        "cref",
        "command",
        "\\cref{}",
        "\\cref{key-list}",
        "https://ctan.org/pkg/cleveref",
        "all"
    ),
    package!(
        "booktabs",
        "toprule",
        "command",
        "\\toprule",
        "\\toprule",
        "https://ctan.org/pkg/booktabs",
        "all"
    ),
    package!(
        "booktabs",
        "midrule",
        "command",
        "\\midrule",
        "\\midrule",
        "https://ctan.org/pkg/booktabs",
        "all"
    ),
    package!(
        "booktabs",
        "bottomrule",
        "command",
        "\\bottomrule",
        "\\bottomrule",
        "https://ctan.org/pkg/booktabs",
        "all"
    ),
    package!(
        "caption",
        "caption",
        "command",
        "\\caption{}",
        "\\caption[short]{text}",
        "https://ctan.org/pkg/caption",
        "all"
    ),
    package!(
        "subcaption",
        "subfigure",
        "environment",
        "subfigure",
        "\\begin{subfigure}{width} … \\end{subfigure}",
        "https://ctan.org/pkg/subcaption",
        "all"
    ),
    package!(
        "listings",
        "lstlisting",
        "environment",
        "lstlisting",
        "\\begin{lstlisting}[options] … \\end{lstlisting}",
        "https://ctan.org/pkg/listings",
        "all"
    ),
    package!(
        "enumitem",
        "enumerate",
        "environment",
        "enumerate",
        "\\begin{enumerate}[options] … \\end{enumerate}",
        "https://ctan.org/pkg/enumitem",
        "all"
    ),
    package!(
        "setspace",
        "onehalfspacing",
        "command",
        "\\onehalfspacing",
        "\\onehalfspacing",
        "https://ctan.org/pkg/setspace",
        "all"
    ),
    package!(
        "setspace",
        "spacing",
        "environment",
        "spacing",
        "\\begin{spacing}{factor} … \\end{spacing}",
        "https://ctan.org/pkg/setspace",
        "all"
    ),
    package!(
        "geometry",
        "geometry",
        "command",
        "\\geometry{}",
        "\\geometry{options}",
        "https://ctan.org/pkg/geometry",
        "all"
    ),
    package!(
        "babel",
        "selectlanguage",
        "command",
        "\\selectlanguage{}",
        "\\selectlanguage{language}",
        "https://ctan.org/pkg/babel",
        "all; language resources required"
    ),
    package!(
        "fontspec",
        "setmainfont",
        "command",
        "\\setmainfont{}",
        "\\setmainfont{font}[options]",
        "https://ctan.org/pkg/fontspec",
        "XeLaTeX/LuaLaTeX only; fonts required"
    ),
    package!(
        "biblatex",
        "addbibresource",
        "command",
        "\\addbibresource{}",
        "\\addbibresource{project-path.bib}",
        "https://ctan.org/pkg/biblatex",
        "all; selected bibliography backend required"
    ),
    package!(
        "biblatex",
        "printbibliography",
        "command",
        "\\printbibliography",
        "\\printbibliography",
        "https://ctan.org/pkg/biblatex",
        "all; selected bibliography backend required"
    ),
];

pub fn known_package(name: &str) -> bool {
    ENTRIES.iter().any(|e| e.package == Some(name))
}

pub fn known_command(name: &str) -> bool {
    ENTRIES
        .iter()
        .any(|e| e.kind == "command" && e.name == name)
        || matches!(
            name,
            "documentclass"
                | "usepackage"
                | "RequirePackage"
                | "LoadClass"
                | "begin"
                | "end"
                | "bibliography"
                | "bibliographystyle"
                | "item"
                | "tableofcontents"
                | "maketitle"
                | "newpage"
                | "clearpage"
                | "appendix"
        )
}
