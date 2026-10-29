namespace ConflictStudio.App;

public sealed record CodeComparisonLine(int? LineNumber, string Text, int DifferenceStart, int DifferenceLength, bool IsDifferent);

public sealed record CodeComparisonLines(CodeComparisonLine[] Left, CodeComparisonLine[] Right, bool LineAlignmentLimited);

public static class CodeComparisonDiff
{
    private const long MaximumLcsCells = 40000;

    public static CodeComparisonLines Compare(IReadOnlyList<string> left, int leftStartLine, IReadOnlyList<string> right, int rightStartLine)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return HasBoundedLcs(left.Count, right.Count)
            ? ByLcs(left, leftStartLine, right, rightStartLine)
            : ByPosition(left, leftStartLine, right, rightStartLine, true);
    }

    public static CodeComparisonLines Compare(IReadOnlyList<string> left, int leftStartLine, IReadOnlyList<string> right, int rightStartLine, int leftFocusStart, int leftFocusEnd, int rightFocusStart, int rightFocusEnd)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        (int leftFocusFrom, int leftFocusTo) = FocusRange(leftStartLine, left.Count, leftFocusStart, leftFocusEnd);
        (int rightFocusFrom, int rightFocusTo) = FocusRange(rightStartLine, right.Count, rightFocusStart, rightFocusEnd);
        CodeComparisonLines before = Compare(left.Take(leftFocusFrom).ToArray(), leftStartLine, right.Take(rightFocusFrom).ToArray(), rightStartLine);
        CodeComparisonLines focus = Compare(left.Skip(leftFocusFrom).Take(leftFocusTo - leftFocusFrom).ToArray(), leftStartLine + leftFocusFrom, right.Skip(rightFocusFrom).Take(rightFocusTo - rightFocusFrom).ToArray(), rightStartLine + rightFocusFrom);
        CodeComparisonLines after = Compare(left.Skip(leftFocusTo).ToArray(), leftStartLine + leftFocusTo, right.Skip(rightFocusTo).ToArray(), rightStartLine + rightFocusTo);
        return new CodeComparisonLines([.. before.Left, .. focus.Left, .. after.Left], [.. before.Right, .. focus.Right, .. after.Right], before.LineAlignmentLimited || focus.LineAlignmentLimited || after.LineAlignmentLimited);
    }

    private static bool HasBoundedLcs(int leftCount, int rightCount)
        => ((long)leftCount + 1) * ((long)rightCount + 1) <= MaximumLcsCells;

    private static (int From, int To) FocusRange(int sourceStartLine, int sourceCount, int focusStart, int focusEnd)
    {
        if (sourceCount == 0) return (0, 0);

        long sourceEndLine = (long)sourceStartLine + sourceCount - 1;
        long from = Math.Clamp((long)focusStart, sourceStartLine, sourceEndLine);
        long to = Math.Clamp((long)focusEnd, sourceStartLine, sourceEndLine);
        if (to < from) return ((int)(from - sourceStartLine), (int)(from - sourceStartLine));
        return ((int)(from - sourceStartLine), (int)(to - sourceStartLine + 1));
    }

    private static CodeComparisonLines ByLcs(IReadOnlyList<string> left, int leftStartLine, IReadOnlyList<string> right, int rightStartLine)
    {
        int width = right.Count + 1;
        int[] lengths = new int[(left.Count + 1) * width];
        for (int leftIndex = left.Count - 1; leftIndex >= 0; leftIndex--)
        {
            for (int rightIndex = right.Count - 1; rightIndex >= 0; rightIndex--)
            {
                int index = leftIndex * width + rightIndex;
                lengths[index] = string.Equals(left[leftIndex], right[rightIndex], StringComparison.Ordinal)
                    ? lengths[(leftIndex + 1) * width + rightIndex + 1] + 1
                    : Math.Max(lengths[(leftIndex + 1) * width + rightIndex], lengths[leftIndex * width + rightIndex + 1]);
            }
        }

        List<(int Left, int Right)> matches = [];
        int leftCursor = 0;
        int rightCursor = 0;
        while (leftCursor < left.Count && rightCursor < right.Count)
        {
            if (string.Equals(left[leftCursor], right[rightCursor], StringComparison.Ordinal))
            {
                matches.Add((leftCursor, rightCursor));
                leftCursor++;
                rightCursor++;
            }
            else if (lengths[(leftCursor + 1) * width + rightCursor] >= lengths[leftCursor * width + rightCursor + 1]) leftCursor++;
            else rightCursor++;
        }

        List<CodeComparisonLine> alignedLeft = [];
        List<CodeComparisonLine> alignedRight = [];
        leftCursor = 0;
        rightCursor = 0;
        foreach ((int matchedLeft, int matchedRight) in matches)
        {
            AddUnmatched(left, leftStartLine, leftCursor, matchedLeft, right, rightStartLine, rightCursor, matchedRight, alignedLeft, alignedRight);
            AddMatching(left[matchedLeft], leftStartLine + matchedLeft, right[matchedRight], rightStartLine + matchedRight, alignedLeft, alignedRight);
            leftCursor = matchedLeft + 1;
            rightCursor = matchedRight + 1;
        }
        AddUnmatched(left, leftStartLine, leftCursor, left.Count, right, rightStartLine, rightCursor, right.Count, alignedLeft, alignedRight);
        return new CodeComparisonLines([.. alignedLeft], [.. alignedRight], false);
    }

    private static CodeComparisonLines ByPosition(IReadOnlyList<string> left, int leftStartLine, IReadOnlyList<string> right, int rightStartLine, bool limited)
    {
        List<CodeComparisonLine> alignedLeft = [];
        List<CodeComparisonLine> alignedRight = [];
        AddUnmatched(left, leftStartLine, 0, left.Count, right, rightStartLine, 0, right.Count, alignedLeft, alignedRight);
        return new CodeComparisonLines([.. alignedLeft], [.. alignedRight], limited);
    }

    private static void AddUnmatched(IReadOnlyList<string> left, int leftStartLine, int leftFrom, int leftTo, IReadOnlyList<string> right, int rightStartLine, int rightFrom, int rightTo, List<CodeComparisonLine> alignedLeft, List<CodeComparisonLine> alignedRight)
    {
        int count = Math.Max(leftTo - leftFrom, rightTo - rightFrom);
        for (int offset = 0; offset < count; offset++)
        {
            bool hasLeft = leftFrom + offset < leftTo;
            bool hasRight = rightFrom + offset < rightTo;
            if (hasLeft && hasRight)
            {
                string leftText = left[leftFrom + offset];
                string rightText = right[rightFrom + offset];
                if (string.Equals(leftText, rightText, StringComparison.Ordinal)) AddMatching(leftText, leftStartLine + leftFrom + offset, rightText, rightStartLine + rightFrom + offset, alignedLeft, alignedRight);
                else AddDifferent(leftText, leftStartLine + leftFrom + offset, rightText, rightStartLine + rightFrom + offset, alignedLeft, alignedRight);
            }
            else if (hasLeft) AddPadding(left[leftFrom + offset], leftStartLine + leftFrom + offset, alignedLeft, alignedRight);
            else AddPadding(right[rightFrom + offset], rightStartLine + rightFrom + offset, alignedRight, alignedLeft);
        }
    }

    private static void AddMatching(string left, int leftLine, string right, int rightLine, List<CodeComparisonLine> alignedLeft, List<CodeComparisonLine> alignedRight)
    {
        alignedLeft.Add(new CodeComparisonLine(leftLine, left, 0, 0, false));
        alignedRight.Add(new CodeComparisonLine(rightLine, right, 0, 0, false));
    }

    private static void AddDifferent(string left, int leftLine, string right, int rightLine, List<CodeComparisonLine> alignedLeft, List<CodeComparisonLine> alignedRight)
    {
        (int start, int leftLength, int rightLength) = Difference(left, right);
        alignedLeft.Add(new CodeComparisonLine(leftLine, left, start, leftLength, true));
        alignedRight.Add(new CodeComparisonLine(rightLine, right, start, rightLength, true));
    }

    private static void AddPadding(string text, int line, List<CodeComparisonLine> source, List<CodeComparisonLine> padding)
    {
        source.Add(new CodeComparisonLine(line, text, 0, text.Length, true));
        padding.Add(new CodeComparisonLine(null, string.Empty, 0, 0, true));
    }

    private static (int Start, int LeftLength, int RightLength) Difference(string left, string right)
    {
        int prefix = 0;
        int sharedLength = Math.Min(left.Length, right.Length);
        while (prefix < sharedLength && left[prefix] == right[prefix]) prefix++;

        int suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix && left[left.Length - suffix - 1] == right[right.Length - suffix - 1]) suffix++;

        prefix = AdjustPrefix(left, right, prefix);
        suffix = AdjustSuffix(left, right, suffix);
        return (prefix, left.Length - prefix - suffix, right.Length - prefix - suffix);
    }

    private static int AdjustPrefix(string left, string right, int prefix)
        => SplitsSurrogatePair(left, prefix) || SplitsSurrogatePair(right, prefix) ? prefix - 1 : prefix;

    private static int AdjustSuffix(string left, string right, int suffix)
        => SplitsSurrogatePair(left, left.Length - suffix) || SplitsSurrogatePair(right, right.Length - suffix) ? suffix - 1 : suffix;

    private static bool SplitsSurrogatePair(string text, int index)
        => index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]);
}
