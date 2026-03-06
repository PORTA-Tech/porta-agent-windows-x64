using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Service;

internal sealed class WindowsBrightnessController : IBrightnessController
{
    public bool IsSupported()
    {
        return false;
    }

    public BrightnessControllerLevelResult GetLevel()
    {
        return new BrightnessControllerLevelResult(
            Succeeded: false,
            ErrorCode: "brightness_unsupported",
            ErrorDetail: "Windows brightness adapter is not enabled on this build/device yet.",
            IsSupported: false,
            Level: 0);
    }

    public BrightnessControllerSetResult SetLevel(int level)
    {
        _ = level;
        return new BrightnessControllerSetResult(
            Succeeded: false,
            ErrorCode: "brightness_unsupported",
            ErrorDetail: "Windows brightness adapter is not enabled on this build/device yet.",
            IsSupported: false,
            StateChanged: false,
            AppliedLevel: 0);
    }
}
