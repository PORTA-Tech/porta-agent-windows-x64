using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Porta.Agent.Runtime.Input;

namespace Porta.Agent.Runtime
{
    public class ScrollEvent
    {
        public int Delta { get; }
        public long Timestamp { get; }

        public ScrollEvent(int delta, long timestamp)
        {
            Delta = delta;
            Timestamp = timestamp;
        }
    }

    public class ScrollProcessor
    {
        private ScrollEvent? _lastEvent;

        public void Process(ScrollEvent evt)
        {
            double velocity = evt.Delta;

            if (_lastEvent != null)
            {
                double deltaTime = (evt.Timestamp - _lastEvent.Timestamp) / (double)Stopwatch.Frequency;
                if (deltaTime > 0)
                    velocity = evt.Delta / deltaTime;
            }

            Console.WriteLine($"Scroll delta: {evt.Delta}, velocity: {velocity:F2}, timestamp: {evt.Timestamp}");

            _lastEvent = evt;

            // TODO: send to PORTA processing modules
            // Example: EmotionalEngine.ProcessScroll(evt.Delta, velocity);
        }
    }

    public class AgentHost : IDisposable
    {
        private readonly MouseHook _mouseHook;
        private readonly ScrollProcessor _processor = new();
        private readonly ConcurrentQueue<ScrollEvent> _queue = new();
        private readonly Thread _worker;
        private bool _running;

        public AgentHost()
        {
            _mouseHook = new MouseHook();
            _mouseHook.ScrollDeltaChanged += OnScrollDelta;

            _worker = new Thread(ProcessQueue)
            {
                IsBackground = true
            };
        }

        private void OnScrollDelta(int delta)
        {
            _queue.Enqueue(new ScrollEvent(delta, Stopwatch.GetTimestamp()));
        }

        private void ProcessQueue()
        {
            while (_running)
            {
                while (_queue.TryDequeue(out var evt))
                {
                    _processor.Process(evt);
                }
                Thread.Sleep(1); // prevent CPU spinning
            }
        }

        public void Start()
        {
            Console.WriteLine("AgentHost starting...");
            _running = true;
            _mouseHook.Install();
            _worker.Start();
        }

        public void Stop()
        {
            Console.WriteLine("AgentHost stopping...");
            _running = false;
            _worker.Join();
            _mouseHook.Uninstall();
            Console.WriteLine("AgentHost stopped.");
        }

        public void Dispose() => Stop();
    }
}
