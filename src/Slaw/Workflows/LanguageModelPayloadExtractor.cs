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

    public static string? ExtractJsonObject(string modelOutput) => ExtractDelimitedPayload(modelOutput, '{', '}');

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
