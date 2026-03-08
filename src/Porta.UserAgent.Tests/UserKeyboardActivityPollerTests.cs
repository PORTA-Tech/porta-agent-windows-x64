using Porta.UserAgent.Input;

namespace Porta.UserAgent.Tests;

public sealed class UserKeyboardActivityPollerTests
{
    [Fact]
    public void PollOnce_EmitsKeyDownTransition_ForRegularKey()
    {
        var reader = new FakeKeyboardStateReader();
        var poller = new UserKeyboardActivityPoller(reader, TimeSpan.FromHours(1));
        var events = new List<(bool IsDown, bool IsBackspace, bool IsInjected)>();
        poller.KeyChanged += (isDown, isBackspace, isInjected) => events.Add((isDown, isBackspace, isInjected));

        poller.PrimeSnapshot();
        reader.SetKeyDown(0x41, true);

        poller.PollOnce();

        Assert.Single(events);
        Assert.Equal((true, false, false), events[0]);
    }

    [Fact]
    public void PollOnce_EmitsBackspaceTransition_AsBackspace()
    {
        var reader = new FakeKeyboardStateReader();
        var poller = new UserKeyboardActivityPoller(reader, TimeSpan.FromHours(1));
        var events = new List<(bool IsDown, bool IsBackspace, bool IsInjected)>();
        poller.KeyChanged += (isDown, isBackspace, isInjected) => events.Add((isDown, isBackspace, isInjected));

        poller.PrimeSnapshot();
        reader.SetKeyDown(0x08, true);

        poller.PollOnce();

        Assert.Single(events);
        Assert.Equal((true, true, false), events[0]);
    }

    [Fact]
    public void PollOnce_DoesNotEmit_WhenStateDoesNotChange()
    {
        var reader = new FakeKeyboardStateReader();
        var poller = new UserKeyboardActivityPoller(reader, TimeSpan.FromHours(1));
        int eventCount = 0;
        poller.KeyChanged += (_, _, _) => eventCount++;

        poller.PrimeSnapshot();
        poller.PollOnce();

        Assert.Equal(0, eventCount);
    }

    private sealed class FakeKeyboardStateReader : IKeyboardStateReader
    {
        private readonly bool[] _down = new bool[256];

        public short GetAsyncKeyState(int virtualKey)
        {
            return _down[virtualKey] ? unchecked((short)0x8000) : (short)0;
        }

        public void SetKeyDown(int virtualKey, bool down)
        {
            _down[virtualKey] = down;
        }
    }
}
