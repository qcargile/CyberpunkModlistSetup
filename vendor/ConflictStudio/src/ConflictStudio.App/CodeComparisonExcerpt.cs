using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed record CodeComparisonExcerpt(int StartLine, int EndLine, string[] Lines, bool RangeLimited, bool LinesShortened)
{
    public const int MaximumLineCharacters = 1600;

    public static CodeComparisonExcerpt Create(CodeSourceDocument document, CodeSourceEvidence evidence, int contextLines, int lineLimit)
    {
        int start = Math.Max(1, evidence.StartLine - contextLines);
        int requestedEnd = Math.Min(document.Lines.Length, evidence.EndLine + contextLines);
        if (evidence.FocusStartLine >= start + lineLimit) start = Math.Max(1, evidence.FocusStartLine - contextLines);
        int end = Math.Min(requestedEnd, start + lineLimit - 1);
        string[] lines = document.Lines.Skip(start - 1).Take(Math.Max(0, end - start + 1)).ToArray();
        bool shortened = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length <= MaximumLineCharacters) continue;
            int length = char.IsHighSurrogate(lines[index][MaximumLineCharacters - 1]) ? MaximumLineCharacters - 1 : MaximumLineCharacters;
            lines[index] = lines[index][..length] + " …";
            shortened = true;
        }
        return new CodeComparisonExcerpt(start, end, lines, start > evidence.StartLine || end < requestedEnd, shortened);
    }
}
