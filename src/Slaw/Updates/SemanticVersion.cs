using System.Globalization;

namespace SlopstackLabs.Slaw.Updates;

/// <summary>
/// A version as release tags and the assembly spell it: <c>v0.2.0-beta.7</c>, <c>0.2.0</c>,
/// <c>0.2.0-beta.8+commit</c>. Ordered by Semantic Versioning 2.0 precedence, so beta.10
/// follows beta.9 and a release follows its own betas. The commit after the plus sign is
/// dropped: it says where a build came from, not how new it is.
/// </summary>
internal sealed record SemanticVersion(int Major, int Minor, int Patch, string PrereleaseLabel) : IComparable<SemanticVersion>
{
    public bool IsPrerelease => PrereleaseLabel.Length > 0;

    public bool IsNewerThan(SemanticVersion otherVersion) => CompareTo(otherVersion) > 0;

    /// <summary>Reads a tag or an assembly version; null for anything that is not major.minor.patch with an optional label.</summary>
    public static SemanticVersion? TryParse(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }
        var remainingText = versionText.Trim();
        if (remainingText[0] is 'v' or 'V')
        {
            remainingText = remainingText[1..];
        }
        var buildMetadataStart = remainingText.IndexOf('+');
        if (buildMetadataStart >= 0)
        {
            remainingText = remainingText[..buildMetadataStart];
        }
        var prereleaseLabel = string.Empty;
        var prereleaseStart = remainingText.IndexOf('-');
        if (prereleaseStart >= 0)
        {
            prereleaseLabel = remainingText[(prereleaseStart + 1)..];
            remainingText = remainingText[..prereleaseStart];
            if (!prereleaseLabel.Split('.').All(IsPrereleaseIdentifier))
            {
                return null;
            }
        }
        var coreNumbers = remainingText.Split('.');
        if (coreNumbers.Length != 3 ||
            !TryParseCoreNumber(coreNumbers[0], out var major) ||
            !TryParseCoreNumber(coreNumbers[1], out var minor) ||
            !TryParseCoreNumber(coreNumbers[2], out var patch))
        {
            return null;
        }
        return new SemanticVersion(major, minor, patch, prereleaseLabel);
    }

    // NumberStyles.None: digits only, so "-2" and " 2" are not numbers here.
    private static bool TryParseCoreNumber(string numberText, out int number) =>
        int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static bool IsPrereleaseIdentifier(string identifier) =>
        identifier.Length > 0 && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public int CompareTo(SemanticVersion? otherVersion)
    {
        if (otherVersion is null)
        {
            return 1;
        }
        var coreComparison = (Major, Minor, Patch).CompareTo((otherVersion.Major, otherVersion.Minor, otherVersion.Patch));
        return coreComparison != 0 ? coreComparison : ComparePrereleaseLabels(PrereleaseLabel, otherVersion.PrereleaseLabel);
    }

    private static int ComparePrereleaseLabels(string label, string otherLabel)
    {
        if (label.Length == 0 || otherLabel.Length == 0)
        {
            // The release itself outranks every prerelease of it.
            return otherLabel.Length.CompareTo(label.Length);
        }
        var identifiers = label.Split('.');
        var otherIdentifiers = otherLabel.Split('.');
        for (var identifierIndex = 0; identifierIndex < Math.Min(identifiers.Length, otherIdentifiers.Length); identifierIndex++)
        {
            var identifierComparison = ComparePrereleaseIdentifiers(identifiers[identifierIndex], otherIdentifiers[identifierIndex]);
            if (identifierComparison != 0)
            {
                return identifierComparison;
            }
        }
        return identifiers.Length.CompareTo(otherIdentifiers.Length);
    }

    private static int ComparePrereleaseIdentifiers(string identifier, string otherIdentifier)
    {
        var numeric = identifier.All(char.IsAsciiDigit);
        var otherNumeric = otherIdentifier.All(char.IsAsciiDigit);
        if (numeric && otherNumeric)
        {
            // Compared as digit strings, so no beta number is too large to rank.
            var digits = identifier.TrimStart('0');
            var otherDigits = otherIdentifier.TrimStart('0');
            return digits.Length != otherDigits.Length
                ? digits.Length.CompareTo(otherDigits.Length)
                : Math.Sign(string.CompareOrdinal(digits, otherDigits));
        }
        if (numeric != otherNumeric)
        {
            return numeric ? -1 : 1;
        }
        return Math.Sign(string.CompareOrdinal(identifier, otherIdentifier));
    }

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{PrereleaseLabel}" : $"{Major}.{Minor}.{Patch}";
}
