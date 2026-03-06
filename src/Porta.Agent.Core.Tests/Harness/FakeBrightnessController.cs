using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests.Harness;

public readonly record struct BrightnessControllerCall(
    string Operation,
    DateTimeOffset TimestampUtc,
    int RequestedLevel,
    int LevelBefore,
    int LevelAfter,
    bool SupportedAfter,
    bool Succeeded,
    string ErrorCode,
    bool StateChanged);

public sealed class FakeBrightnessController : IBrightnessController
{
    private readonly List<BrightnessControllerCall> _calls = [];

    public FakeBrightnessController(DeterministicTestClock clock)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public DeterministicTestClock Clock { get; }

    public bool Supported { get; set; } = true;

    public int CurrentLevel { get; set; } = 55;

    public bool FailNextGetLevel { get; set; }

    public bool FailNextSetLevel { get; set; }

    public bool FailNextRampDown { get; set; }

    public bool FailNextRampUp { get; set; }

    public string GetLevelFailureCode { get; set; } = "brightness_get_failed";

    public string SetLevelFailureCode { get; set; } = "brightness_set_failed";

    public int IsSupportedCalls { get; private set; }

    public int GetLevelCalls { get; private set; }

    public int SetLevelCalls { get; private set; }

    public int LevelChangeCount { get; private set; }

    public string LastErrorCode { get; private set; } = string.Empty;

    public IReadOnlyList<BrightnessControllerCall> Calls => _calls;

    public bool IsSupported()
    {
        IsSupportedCalls++;
        _calls.Add(new BrightnessControllerCall(
            Operation: nameof(IsSupported),
            TimestampUtc: Clock.UtcNow,
            RequestedLevel: -1,
            LevelBefore: CurrentLevel,
            LevelAfter: CurrentLevel,
            SupportedAfter: Supported,
            Succeeded: true,
            ErrorCode: string.Empty,
            StateChanged: false));
        return Supported;
    }

    public BrightnessControllerLevelResult GetLevel()
    {
        GetLevelCalls++;

        if (FailNextGetLevel)
        {
            FailNextGetLevel = false;
            LastErrorCode = GetLevelFailureCode;
            _calls.Add(new BrightnessControllerCall(
                Operation: nameof(GetLevel),
                TimestampUtc: Clock.UtcNow,
                RequestedLevel: -1,
                LevelBefore: CurrentLevel,
                LevelAfter: CurrentLevel,
                SupportedAfter: Supported,
                Succeeded: false,
                ErrorCode: GetLevelFailureCode,
                StateChanged: false));
            return new BrightnessControllerLevelResult(
                Succeeded: false,
                ErrorCode: GetLevelFailureCode,
                ErrorDetail: "Injected get-level failure.",
                IsSupported: Supported,
                Level: CurrentLevel);
        }

        if (!Supported)
        {
            LastErrorCode = "brightness_unsupported";
            _calls.Add(new BrightnessControllerCall(
                Operation: nameof(GetLevel),
                TimestampUtc: Clock.UtcNow,
                RequestedLevel: -1,
                LevelBefore: CurrentLevel,
                LevelAfter: CurrentLevel,
                SupportedAfter: Supported,
                Succeeded: false,
                ErrorCode: "brightness_unsupported",
                StateChanged: false));
            return new BrightnessControllerLevelResult(
                Succeeded: false,
                ErrorCode: "brightness_unsupported",
                ErrorDetail: "Brightness is unsupported in fake controller.",
                IsSupported: false,
                Level: CurrentLevel);
        }

        _calls.Add(new BrightnessControllerCall(
            Operation: nameof(GetLevel),
            TimestampUtc: Clock.UtcNow,
            RequestedLevel: -1,
            LevelBefore: CurrentLevel,
            LevelAfter: CurrentLevel,
            SupportedAfter: Supported,
            Succeeded: true,
            ErrorCode: string.Empty,
            StateChanged: false));
        LastErrorCode = string.Empty;
        return new BrightnessControllerLevelResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            IsSupported: true,
            Level: CurrentLevel);
    }

    public BrightnessControllerSetResult SetLevel(int level)
    {
        SetLevelCalls++;
        int clamped = Math.Clamp(level, 0, 100);
        int before = CurrentLevel;
        bool rampingDown = clamped < before;
        bool rampingUp = clamped > before;

        if (!Supported)
        {
            LastErrorCode = "brightness_unsupported";
            _calls.Add(new BrightnessControllerCall(
                Operation: nameof(SetLevel),
                TimestampUtc: Clock.UtcNow,
                RequestedLevel: level,
                LevelBefore: before,
                LevelAfter: before,
                SupportedAfter: Supported,
                Succeeded: false,
                ErrorCode: "brightness_unsupported",
                StateChanged: false));
            return new BrightnessControllerSetResult(
                Succeeded: false,
                ErrorCode: "brightness_unsupported",
                ErrorDetail: "Brightness is unsupported in fake controller.",
                IsSupported: false,
                StateChanged: false,
                AppliedLevel: CurrentLevel);
        }

        if (FailNextRampDown && rampingDown)
        {
            FailNextRampDown = false;
            LastErrorCode = SetLevelFailureCode;
            _calls.Add(new BrightnessControllerCall(
                Operation: nameof(SetLevel),
                TimestampUtc: Clock.UtcNow,
                RequestedLevel: level,
                LevelBefore: before,
                LevelAfter: before,
                SupportedAfter: Supported,
                Succeeded: false,
                ErrorCode: SetLevelFailureCode,
                StateChanged: false));
            return new BrightnessControllerSetResult(
                Succeeded: false,
                ErrorCode: SetLevelFailureCode,
                ErrorDetail: "Injected ramp-down failure.",
                IsSupported: true,
                StateChanged: false,
                AppliedLevel: before);
        }

        if (FailNextRampUp && rampingUp)
        {
            FailNextRampUp = false;
            LastErrorCode = SetLevelFailureCode;
            _calls.Add(new BrightnessControllerCall(
                Operation: nameof(SetLevel),
                TimestampUtc: Clock.UtcNow,
                RequestedLevel: level,
                LevelBefore: before,
                LevelAfter: before,
                SupportedAfter: Supported,
                Succeeded: false,
                ErrorCode: SetLevelFailureCode,
                StateChanged: false));
            return new BrightnessControllerSetResult(
                Succeeded: false,
                ErrorCode: SetLevelFailureCode,
                ErrorDetail: "Injected ramp-up failure.",
                IsSupported: true,
                StateChanged: false,
                AppliedLevel: before);
        }

        if (FailNextSetLevel)
        {
            FailNextSetLevel = false;
            LastErrorCode = SetLevelFailureCode;
            _calls.Add(new BrightnessControllerCall(
                Operation: nameof(SetLevel),
                TimestampUtc: Clock.UtcNow,
                RequestedLevel: level,
                LevelBefore: before,
                LevelAfter: before,
                SupportedAfter: Supported,
                Succeeded: false,
                ErrorCode: SetLevelFailureCode,
                StateChanged: false));
            return new BrightnessControllerSetResult(
                Succeeded: false,
                ErrorCode: SetLevelFailureCode,
                ErrorDetail: "Injected set-level failure.",
                IsSupported: true,
                StateChanged: false,
                AppliedLevel: CurrentLevel);
        }

        bool changed = before != clamped;
        CurrentLevel = clamped;
        if (changed)
        {
            LevelChangeCount++;
        }
        LastErrorCode = string.Empty;
        _calls.Add(new BrightnessControllerCall(
            Operation: nameof(SetLevel),
            TimestampUtc: Clock.UtcNow,
            RequestedLevel: level,
            LevelBefore: before,
            LevelAfter: CurrentLevel,
            SupportedAfter: Supported,
            Succeeded: true,
            ErrorCode: string.Empty,
            StateChanged: changed));
        return new BrightnessControllerSetResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            IsSupported: true,
            StateChanged: changed,
            AppliedLevel: CurrentLevel);
    }
}
