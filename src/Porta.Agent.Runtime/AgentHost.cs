using System;
using System.Threading.Tasks;
using Porta.Agent.Runtime.Input;

namespace Porta.Agent.Runtime
{
    public class AgentHost : IDisposable
    {
        private readonly MouseHook _mouseHook;
        private bool _running;

        public AgentHost()
        {
            _mouseHook = new MouseHook();
            _mouseHook.ScrollCaptured += delta =>
            {
                Console.WriteLine($"Scroll delta: {delta}");
            };
        }

        public void Start()
        {
            if (_running) return;
            Console.WriteLine("AgentHost starting...");
            _mouseHook.Install();
            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;
            Console.WriteLine("AgentHost stopping...");
            _mouseHook.Uninstall();
            _running = false;
        }

        public Task StartAsync() => Task.Run(Start);
        public Task StopAsync() => Task.Run(Stop);

        public void Dispose() => Stop();
    }
}
