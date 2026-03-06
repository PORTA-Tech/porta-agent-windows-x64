namespace Porta.Agent.Core.Outputs;

public readonly record struct BrightnessControllerLevelResult(
    bool Succeeded,
    string ErrorCode,
    string ErrorDetail,
    bool IsSupported,
    int Level);

public readonly record struct BrightnessControllerSetResult(
    bool Succeeded,
    string ErrorCode,
    string ErrorDetail,
    bool IsSupported,
    bool StateChanged,
    int AppliedLevel);

public interface IBrightnessController
{
    bool IsSupported();
    BrightnessControllerLevelResult GetLevel();
    BrightnessControllerSetResult SetLevel(int level);
}

public sealed class NoOpBrightnessController : IBrightnessController
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
            ErrorDetail: "Brightness control is not available in no-op mode.",
            IsSupported: false,
            Level: 0);
    }

    public BrightnessControllerSetResult SetLevel(int level)
    {
        int clampedLevel = Math.Clamp(level, 0, 100);
        return new BrightnessControllerSetResult(
            Succeeded: false,
            ErrorCode: "brightness_unsupported",
            ErrorDetail: "Brightness control is not available in no-op mode.",
            IsSupported: false,
            StateChanged: false,
            AppliedLevel: clampedLevel);
    }
}
