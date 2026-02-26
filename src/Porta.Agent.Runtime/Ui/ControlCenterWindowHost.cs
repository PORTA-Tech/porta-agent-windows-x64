using System.Threading;
using System.Windows.Forms;

namespace Porta.Agent.Runtime.Ui;

internal static class ControlCenterWindowHost
{
    public static void ShowBlocking(bool alreadyRunning)
    {
        Application.Run(new ControlCenterForm(alreadyRunning));
    }

    public static void ShowNonBlocking(bool alreadyRunning)
    {
        var thread = new Thread(() => ShowBlocking(alreadyRunning))
        {
            Name = "PortaControlCenterUiThread",
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
