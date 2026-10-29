using System.Globalization;
using System.Text.RegularExpressions;

namespace SlopstackLabs.Slaw.Workflows;

/// <summary>
/// Digs structured data out of model output, which arrives wrapped in code
/// fences, apologies, "Sure! Here's your arrangement:", and occasionally the
/// requested payload.
/// </summary>
internal static partial class LanguageModelPayloadExtractor
{
    /// <summary>
    /// Returns the span from the first <paramref name="openingDelimiter"/> to the
    /// last <paramref name="closingDelimiter"/>, or null if no such span exists.
    /// Crude, and correct more often than the model is.
    /// </summary>
    public static string? ExtractDelimitedPayload(string modelOutput, char openingDelimiter, char closingDelimiter)
    {
        var openingOffset = modelOutput.IndexOf(openingDelimiter);
        var closingOffset = modelOutput.LastIndexOf(closingDelimiter);
        return openingOffset >= 0 && closingOffset > openingOffset
            ? modelOutput[openingOffset..(closingOffset + 1)]
            : null;
    }

    /// <summary>
    /// Returns the first complete JSON object in the output, found by matching
    /// braces outside of strings — so prose with braces after the object, or a
    /// "}" inside a string, cannot derail it. If the model stopped before closing
    /// everything it opened, the missing brackets are supplied: truncation is a
    /// misalignment, not a reason to throw the whole answer away.
    /// </summary>
    public static string? ExtractJsonObject(string modelOutput)
    {
        var objectStart = modelOutput.IndexOf('{');
        if (objectStart < 0)
        {
            return null;
        }
        var openBrackets = new Stack<char>();
        var insideString = false;
        var escapeNext = false;
        for (var characterIndex = objectStart; characterIndex < modelOutput.Length; characterIndex++)
        {
            var currentCharacter = modelOutput[characterIndex];
            if (insideString)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                }
                else if (currentCharacter == '\\')
                {
                    escapeNext = true;
                }
                else if (currentCharacter == '"')
                {
                    insideString = false;
                }
                continue;
            }
            switch (currentCharacter)
            {
                case '"':
                    insideString = true;
                    break;
                case '{' or '[':
                    openBrackets.Push(currentCharacter == '{' ? '}' : ']');
                    break;
                case '}' or ']' when openBrackets.Count > 0 && openBrackets.Peek() == currentCharacter:
                    openBrackets.Pop();
                    if (openBrackets.Count == 0)
                    {
                        return modelOutput[objectStart..(characterIndex + 1)];
                    }
                    break;
            }
        }

        // Unterminated: close the string, drop a dangling comma or key, then close every bracket.
        var repairedPayload = new System.Text.StringBuilder(modelOutput[objectStart..].TrimEnd());
        if (insideString)
        {
            repairedPayload.Append('"');
        }
        var repairedText = repairedPayload.ToString().TrimEnd();
        repairedText = DanglingTailPattern().Replace(repairedText, string.Empty);
        return repairedText + string.Concat(openBrackets);
    }

    [GeneratedRegex(@"(,\s*""[^""]*""\s*:?\s*|,\s*|:\s*)$")]
    private static partial Regex DanglingTailPattern();

    public static string? ExtractJsonArray(string modelOutput) => ExtractDelimitedPayload(modelOutput, '[', ']');

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex DecimalNumberPattern();

    /// <summary>Returns the first number that appears anywhere in the output.</summary>
    public static double? ExtractFirstNumber(string modelOutput)
    {
        var numberMatch = DecimalNumberPattern().Match(modelOutput);
        return numberMatch.Success
            ? double.Parse(numberMatch.Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Reduces a chatty single-line answer to its first meaningful line, minus
    /// quotes, markdown emphasis, and "Track name:" style preambles.
    /// </summary>
    public static string ExtractSingleLine(string modelOutput, int maximumLength = 64)
    {
        var firstMeaningfulLine = modelOutput
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(candidateLine => !candidateLine.StartsWith("```", StringComparison.Ordinal)) ?? string.Empty;
        var preambleSeparatorOffset = firstMeaningfulLine.IndexOf(':');
        if (preambleSeparatorOffset is > 0 and < 24 && preambleSeparatorOffset < firstMeaningfulLine.Length - 1)
        {
            firstMeaningfulLine = firstMeaningfulLine[(preambleSeparatorOffset + 1)..];
        }
        var sanitizedLine = firstMeaningfulLine.Trim().Trim('"', '\'', '*', '`', '“', '”', '.').Trim();
        return sanitizedLine.Length > maximumLength ? sanitizedLine[..maximumLength].TrimEnd() + "…" : sanitizedLine;
    }
}
