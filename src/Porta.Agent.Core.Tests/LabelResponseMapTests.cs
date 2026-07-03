using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

// Phase 1b — Step 3b: the pure (target, answer) -> (source, response, value) mapping.
public sealed class LabelResponseMapTests
{
    [Fact]
    public void Map_ProducesCorrectLabels()
    {
        Assert.Equal(("focus_check", "focused", 1), LabelResponseMap.Map("focus_state", "focused"));
        Assert.Equal(("focus_check", "not_focused", -1), LabelResponseMap.Map("focus_state", "not_focused"));
        Assert.Equal(("distraction_check", "off_task", 1), LabelResponseMap.Map("distraction", "break"));
        Assert.Equal(("distraction_check", "on_task", -1), LabelResponseMap.Map("distraction", "working"));
        Assert.Equal(("overload_check", "overloaded", 1), LabelResponseMap.Map("overload", "overloaded"));
        Assert.Equal(("overload_check", "fine", -1), LabelResponseMap.Map("overload", "fine"));
    }

    [Fact]
    public void Map_UnknownPairs_ReturnEmptySource()
    {
        Assert.Equal(string.Empty, LabelResponseMap.Map("focus_state", "banana").Source);
        Assert.Equal(string.Empty, LabelResponseMap.Map("unknown", "focused").Source);
        Assert.Equal(string.Empty, LabelResponseMap.Map("", "").Source);
        Assert.Equal(string.Empty, LabelResponseMap.Map(null, null).Source);
    }

    [Fact]
    public void Map_IsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(1, LabelResponseMap.Map("  Focus_State ", " FOCUSED ").Value);
    }
}
