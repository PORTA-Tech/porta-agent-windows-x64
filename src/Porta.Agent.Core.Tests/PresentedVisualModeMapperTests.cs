using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Core.Tests;

public sealed class PresentedVisualModeMapperTests
{
    [Fact]
    public void Map_ReturnsOverload_WhenBothStatesAreTrue()
    {
        PresentedVisualMode mode = PresentedVisualModeMapper.Map(
            overloadActive: true,
            deepWorkActive: true);

        Assert.Equal(PresentedVisualMode.OVERLOAD, mode);
    }

    [Fact]
    public void Map_ReturnsDeepWork_WhenOnlyDeepWorkIsTrue()
    {
        PresentedVisualMode mode = PresentedVisualModeMapper.Map(
            overloadActive: false,
            deepWorkActive: true);

        Assert.Equal(PresentedVisualMode.DEEP_WORK, mode);
    }

    [Fact]
    public void Map_ReturnsNormal_WhenNoStateIsActive()
    {
        PresentedVisualMode mode = PresentedVisualModeMapper.Map(
            overloadActive: false,
            deepWorkActive: false);

        Assert.Equal(PresentedVisualMode.NORMAL, mode);
    }

    [Theory]
    [InlineData(PresentedVisualMode.NORMAL, "NORMAL")]
    [InlineData(PresentedVisualMode.DEEP_WORK, "DEEP_WORK")]
    [InlineData(PresentedVisualMode.OVERLOAD, "OVERLOAD")]
    public void ToToken_ReturnsPinnedToken(PresentedVisualMode mode, string expectedToken)
    {
        Assert.Equal(expectedToken, PresentedVisualModeMapper.ToToken(mode));
    }

    [Theory]
    [InlineData("normal", PresentedVisualMode.NORMAL)]
    [InlineData("DEEP_WORK", PresentedVisualMode.DEEP_WORK)]
    [InlineData("deepwork", PresentedVisualMode.DEEP_WORK)]
    [InlineData("OVERLOAD", PresentedVisualMode.OVERLOAD)]
    public void TryParseToken_ParsesKnownTokens(string token, PresentedVisualMode expectedMode)
    {
        bool parsed = PresentedVisualModeMapper.TryParseToken(token, out PresentedVisualMode mode);

        Assert.True(parsed);
        Assert.Equal(expectedMode, mode);
    }
}
