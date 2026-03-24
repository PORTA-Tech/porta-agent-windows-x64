namespace Porta.UserAgent;

public sealed record UserAgentStartupMetadata(
    string Source,
    IReadOnlyList<string> RawArgs)
{
    public static UserAgentStartupMetadata Parse(string[] args)
    {
        string source = "unknown";

        foreach (string arg in args)
        {
            const string prefix = "--startup-source=";
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string candidate = arg[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                source = candidate;
            }
        }

        return new UserAgentStartupMetadata(
            Source: source,
            RawArgs: args.Select(static value => value ?? string.Empty).ToArray());
    }

    public string RawArgsText => RawArgs.Count == 0 ? "(none)" : string.Join(' ', RawArgs);
}
