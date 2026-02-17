using System;
using System.Runtime.InteropServices;
using Porta.Agent.Runtime;

class Program
{
    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

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
        Console.WriteLine("Scroll using mouse or trackpad. Press ESC to exit.");

        MSG msg;
        while (true)
        {
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                // exit on ESC key
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    goto exit;
            }
        }

    exit:
        host.Stop();
        Console.WriteLine("AgentHost stopped.");
    }
}
