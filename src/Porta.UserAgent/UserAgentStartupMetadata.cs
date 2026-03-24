namespace Porta.UserAgent;

public sealed record UserAgentStartupMetadata(
    string Source,
    string? LaunchReason,
    IReadOnlyList<string> RawArgs)
{
    public static UserAgentStartupMetadata Parse(string[] args)
    {
        string source = "unknown";
        string? launchReason = null;

        foreach (string arg in args)
        {
            const string sourcePrefix = "--startup-source=";
            if (arg.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = arg[sourcePrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    source = candidate;
                }

                continue;
            }

            const string reasonPrefix = "--launch-reason=";
            if (!arg.StartsWith(reasonPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string launchReasonCandidate = arg[reasonPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(launchReasonCandidate))
            {
                launchReason = launchReasonCandidate;
            }
        }

        return new UserAgentStartupMetadata(
            Source: source,
            LaunchReason: launchReason,
            RawArgs: args.Select(static value => value ?? string.Empty).ToArray());
    }

    public string RawArgsText => RawArgs.Count == 0 ? "(none)" : string.Join(' ', RawArgs);

    public string EffectiveLaunchReason => string.IsNullOrWhiteSpace(LaunchReason) ? Source : LaunchReason!;
}
