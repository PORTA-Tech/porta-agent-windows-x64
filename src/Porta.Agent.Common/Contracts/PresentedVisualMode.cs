namespace Porta.Agent.Common.Contracts
{
    public enum PresentedVisualMode
    {
        NORMAL = 0,
        DEEP_WORK = 1,
        OVERLOAD = 2
    }

    public static class PresentedVisualModeMapper
    {
        public static PresentedVisualMode Map(bool overloadActive, bool deepWorkActive)
        {
            if (overloadActive)
            {
                return PresentedVisualMode.OVERLOAD;
            }

            if (deepWorkActive)
            {
                return PresentedVisualMode.DEEP_WORK;
            }

            return PresentedVisualMode.NORMAL;
        }

        public static string ToToken(PresentedVisualMode mode)
        {
            return mode switch
            {
                PresentedVisualMode.OVERLOAD => "OVERLOAD",
                PresentedVisualMode.DEEP_WORK => "DEEP_WORK",
                _ => "NORMAL"
            };
        }

        public static bool TryParseToken(string? token, out PresentedVisualMode mode)
        {
            mode = PresentedVisualMode.NORMAL;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string normalized = token.Trim().ToUpperInvariant();
            switch (normalized)
            {
                case "OVERLOAD":
                    mode = PresentedVisualMode.OVERLOAD;
                    return true;
                case "DEEP_WORK":
                case "DEEPWORK":
                    mode = PresentedVisualMode.DEEP_WORK;
                    return true;
                case "NORMAL":
                    mode = PresentedVisualMode.NORMAL;
                    return true;
                default:
                    return false;
            }
        }
    }
}
