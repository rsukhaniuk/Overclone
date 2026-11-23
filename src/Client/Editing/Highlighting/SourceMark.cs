namespace Client.Editing.Highlighting;

public enum SourceMarkKind { Check, Ai }

/// <summary>A display-only half-open range in the unnormalized editor source.</summary>
public sealed record SourceMark(SourceRange Range, SourceMarkKind Kind, string Id);
