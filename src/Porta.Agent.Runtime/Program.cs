using System;
using System.Runtime.InteropServices;
using Porta.Agent.Runtime;

class Program
{
    // Win32 imports
    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    static void Main()
    {
        using var host = new AgentHost();
        host.Start();

        Console.WriteLine("Scroll anywhere. Press ESC to exit.");
        var canReadConsole = Environment.UserInteractive && !Console.IsInputRedirected;

        MSG msg;

        // Proper Windows message loop
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);

            // Exit when ESC is pressed
            if (canReadConsole && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                break;
        }

        host.Stop();
        Console.WriteLine("AgentHost stopped.");
    }
}
