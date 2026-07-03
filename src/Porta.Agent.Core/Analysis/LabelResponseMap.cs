namespace Porta.Agent.Core.Analysis;

// Phase 1b — Step 3b: maps a widget one-tap answer to a durable label. PURE logic so the
// normalization (which is easy to get subtly wrong) is unit-tested in isolation.
// Convention: label_value +1 = Ari's guess was RIGHT, -1 = wrong, per label target.
internal static class LabelResponseMap
{
    // Returns an empty Source when the (target, answer) pair is not recognized.
    internal static (string Source, string ResponseKind, int Value) Map(string? target, string? answer)
    {
        string t = (target ?? string.Empty).Trim().ToLowerInvariant();
        string a = (answer ?? string.Empty).Trim().ToLowerInvariant();

        switch (t)
        {
            case "focus_state":
                if (a is "focused" or "yes")
                {
                    return ("focus_check", "focused", 1);
                }

                if (a is "not_focused" or "no")
                {
                    return ("focus_check", "not_focused", -1);
                }

                break;

            case "distraction":
                // Ari flagged a drift. "break"/"off_task" confirms it (Ari right); "working"/"on_task" refutes it.
                if (a is "break" or "off_task")
                {
                    return ("distraction_check", "off_task", 1);
                }

                if (a is "working" or "on_task")
                {
                    return ("distraction_check", "on_task", -1);
                }

                break;

            case "overload":
                if (a is "overloaded" or "yes")
                {
                    return ("overload_check", "overloaded", 1);
                }

                if (a is "fine" or "no")
                {
                    return ("overload_check", "fine", -1);
                }

                break;
        }

        return (string.Empty, string.Empty, 0);
    }
}
