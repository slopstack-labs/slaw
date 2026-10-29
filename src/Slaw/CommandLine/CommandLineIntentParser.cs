using System.Globalization;

namespace SlopstackLabs.Slaw.CommandLine;

/// <summary>Everything the user expressed on the command line, before any of it is validated.</summary>
internal sealed class CommandLineIntent
{
    public string? Command { get; set; }
    public List<string> PositionalArguments { get; } = new();
    public Dictionary<string, string> ValuedOptions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> BooleanFlags { get; } = new(StringComparer.Ordinal);

    public string? Option(string optionName) => ValuedOptions.GetValueOrDefault(optionName);

    public bool Flag(string flagName) => BooleanFlags.Contains(flagName);

    public int IntegerOption(string optionName, int defaultValue)
    {
        var rawOptionValue = Option(optionName);
        if (rawOptionValue is null)
        {
            return defaultValue;
        }
        return int.TryParse(rawOptionValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue) && parsedValue >= 0
            ? parsedValue
            : throw new CommandLineMisalignmentException($"--{optionName} expects a non-negative integer, got \"{rawOptionValue}\"");
    }
}

internal sealed class CommandLineMisalignmentException : Exception
{
    public CommandLineMisalignmentException(string misalignmentDescription)
        : base(misalignmentDescription)
    {
    }
}

/// <summary>
/// Accepts <c>-flag</c>, <c>--flag</c>, <c>--flag value</c>, and <c>--flag=value</c>,
/// with options and positional arguments in any order. The first positional
/// argument is the command.
/// </summary>
internal static class CommandLineIntentParser
{
    public static readonly IReadOnlySet<string> ValuedOptionNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "provider", "api-key", "model", "ollama", "base-url", "timeout", "o", "loop", "force-iterate", "view", "mcp-port",
    };

    public static readonly IReadOnlySet<string> BooleanFlagNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "optimistic", "verbose", "no-copilot", "help", "h", "version", "mcp",
    };

    public static CommandLineIntent Parse(IReadOnlyList<string> rawArguments)
    {
        var parsedIntent = new CommandLineIntent();
        for (var argumentIndex = 0; argumentIndex < rawArguments.Count; argumentIndex++)
        {
            var rawArgument = rawArguments[argumentIndex];
            if (rawArgument == "--")
            {
                parsedIntent.PositionalArguments.AddRange(rawArguments.Skip(argumentIndex + 1));
                break;
            }
            if (rawArgument.Length < 2 || rawArgument[0] != '-')
            {
                parsedIntent.PositionalArguments.Add(rawArgument);
                continue;
            }

            var optionBody = rawArgument.TrimStart('-');
            string? inlineOptionValue = null;
            var inlineValueSeparatorOffset = optionBody.IndexOf('=');
            if (inlineValueSeparatorOffset >= 0)
            {
                inlineOptionValue = optionBody[(inlineValueSeparatorOffset + 1)..];
                optionBody = optionBody[..inlineValueSeparatorOffset];
            }

            if (ValuedOptionNames.Contains(optionBody))
            {
                if (inlineOptionValue is null)
                {
                    if (argumentIndex + 1 >= rawArguments.Count)
                    {
                        throw new CommandLineMisalignmentException($"--{optionBody} expects a value");
                    }
                    inlineOptionValue = rawArguments[++argumentIndex];
                }
                parsedIntent.ValuedOptions[optionBody] = inlineOptionValue;
            }
            else if (BooleanFlagNames.Contains(optionBody))
            {
                if (inlineOptionValue is null || bool.TryParse(inlineOptionValue, out var explicitFlagValue) && explicitFlagValue)
                {
                    parsedIntent.BooleanFlags.Add(optionBody);
                }
            }
            else
            {
                throw new CommandLineMisalignmentException($"unknown option \"{rawArgument}\" — see slaw --help");
            }
        }

        if (parsedIntent.PositionalArguments.Count > 0)
        {
            parsedIntent.Command = parsedIntent.PositionalArguments[0];
            parsedIntent.PositionalArguments.RemoveAt(0);
        }
        return parsedIntent;
    }
}
