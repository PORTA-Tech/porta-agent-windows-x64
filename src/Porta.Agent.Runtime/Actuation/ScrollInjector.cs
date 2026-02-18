using System;
using Porta.Agent.Runtime.Win32;

namespace Porta.Agent.Runtime.Actuation;

internal sealed class ScrollInjector
{
    public void InjectVerticalWheel(int delta)
    {
        NativeMethods.mouse_event(
            Constants.MouseEventfWheel,
            dx: 0,
            dy: 0,
            dwData: delta,
            dwExtraInfo: (UIntPtr)Constants.InjectedScrollSignature);
    }
}
