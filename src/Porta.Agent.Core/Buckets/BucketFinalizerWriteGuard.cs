using System.Threading;

namespace Porta.Agent.Core.Buckets;

/// <summary>
/// Runtime guard to block direct snapshot DB writes outside finalizer flow.
/// </summary>
internal static class BucketFinalizerWriteGuard
{
    private static readonly AsyncLocal<int> Depth = new();

    internal static bool IsActive => Depth.Value > 0;

    internal static IDisposable Enter()
    {
        Depth.Value = Depth.Value + 1;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            int next = Depth.Value - 1;
            Depth.Value = next < 0 ? 0 : next;
            _disposed = true;
        }
    }
}
