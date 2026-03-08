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

    [Fact]
    public void PollOnce_EmitsKeyDown_ForShortTapBetweenPolls()
    {
        var reader = new FakeKeyboardStateReader();
        var poller = new UserKeyboardActivityPoller(reader, TimeSpan.FromHours(1));
        var events = new List<(bool IsDown, bool IsBackspace, bool IsInjected)>();
        poller.KeyChanged += (isDown, isBackspace, isInjected) => events.Add((isDown, isBackspace, isInjected));

        poller.PrimeSnapshot();
        reader.SetPressedSinceLastPoll(0x41, true);
        reader.SetKeyDown(0x41, false);

        poller.PollOnce();

        Assert.Single(events);
        Assert.Equal((true, false, false), events[0]);
    }

    private sealed class FakeKeyboardStateReader : IKeyboardStateReader
    {
        private readonly bool[] _down = new bool[256];
        private readonly bool[] _pressedSinceLastPoll = new bool[256];

        public short GetAsyncKeyState(int virtualKey)
        {
            short state = _down[virtualKey] ? unchecked((short)0x8000) : (short)0;
            if (_pressedSinceLastPoll[virtualKey])
            {
                state |= 0x0001;
                _pressedSinceLastPoll[virtualKey] = false;
            }

            return state;
        }

        public void SetKeyDown(int virtualKey, bool down)
        {
            _down[virtualKey] = down;
        }

        public void SetPressedSinceLastPoll(int virtualKey, bool pressed)
        {
            _pressedSinceLastPoll[virtualKey] = pressed;
        }
    }
}
