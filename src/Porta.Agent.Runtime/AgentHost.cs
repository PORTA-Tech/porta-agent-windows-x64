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
        private readonly Thread _workerThread;
        private readonly Thread _messageLoopThread;
        private bool _running;

        public AgentHost()
        {
            _mouseHook = new MouseHook();
            _mouseHook.ScrollDeltaChanged += OnScrollDelta;

            _messageLoopThread = new Thread(RunMessageLoop)
            {
                IsBackground = false,  // Keep app alive while this runs
                Name = "MessageLoop"
            };

            _workerThread = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Name = "ScrollProcessor"
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

        // Message loop thread - keeps windows messages pumping
        private void RunMessageLoop()
        {
            while (_running)
            {
                // Sleep briefly to avoid 100% CPU
                Thread.Sleep(10);
            }
        }

        public void Start()
        {
            Console.WriteLine("AgentHost starting...");
            _running = true;
            
            // Start message loop thread first
            _messageLoopThread.Start();
            System.Threading.Thread.Sleep(100);  // Give it time to start
            
            _mouseHook.Install();
            _workerThread.Start();
        }

        public void Stop()
        {
            Console.WriteLine("AgentHost stopping...");
            _running = false;
            _workerThread.Join(2000);
            _messageLoopThread.Join(2000);
            _mouseHook.Uninstall();
            Console.WriteLine("AgentHost stopped.");
        }

        public void Dispose() => Stop();
    }
}
