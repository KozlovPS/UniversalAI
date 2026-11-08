using System.Globalization;
using System.Text.RegularExpressions;

namespace UniversalAI.Benchmarks;

/// <summary>Shared answer extraction and exact numeric grading for benchmark tasks.</summary>
public static class BenchmarkAnswerScorer
{
    private static readonly Regex AnswerLineRegex = new(
        @"(?im)^\s*(?:ANSWER|ОТВЕТ)\s*[:：-]\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberRegex = new(
        @"[-+]?\d+(?:[.,]\d+)?\s*/\s*\d+(?:[.,]\d+)?|[-+]?\d+(?:[.,]\d+)?\s*%?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<string> ExtractAnswers(string content)
    {
        MatchCollection matches = AnswerLineRegex.Matches(content);
        List<string> answers = [];
        foreach (Match match in matches)
        {
            answers.AddRange(ExtractFinalNumericValues(match.Groups[1].Value, 1));
        }

        return answers;
    }

    public static List<string> ExtractFinalNumericValues(string content, int maxValues)
    {
        MatchCollection matches = NumberRegex.Matches(content);
        if (matches.Count == 0)
        {
            return [];
        }

        int startIndex = Math.Max(0, matches.Count - maxValues);
        return matches
            .Skip(startIndex)
            .Select(match => Regex.Replace(match.Value.Trim(), @"\s+", ""))
            .ToList();
    }

    public static bool IsExpectedAnswer(
        string answer,
        IReadOnlyList<string> expectedAnswers)
    {
        return TryParseNumericAnswer(answer, out decimal actual) && expectedAnswers.Any(expected =>
            TryParseNumericAnswer(expected, out decimal parsedExpected) &&
            Math.Abs(actual - parsedExpected) <= 0.0005m);
    }

    private static bool TryParseNumericAnswer(string answer, out decimal value)
    {
        string normalized = answer.Trim().Replace(',', '.');
        bool isPercent = normalized.EndsWith('%');
        if (isPercent)
        {
            normalized = normalized[..^1];
        }

        string[] fraction = normalized.Split('/');
        if (fraction.Length == 2 &&
            decimal.TryParse(fraction[0], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal numerator) &&
            decimal.TryParse(fraction[1], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal denominator) &&
            denominator != 0)
        {
            value = numerator / denominator;
        }
        else if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (isPercent)
        {
            value /= 100m;
        }

        return true;
    }
}
