using System;
using System.Threading.Tasks;

namespace Avalonia.Controls;

internal sealed class DeferralManager
{
    private readonly object _mutex = new();
    private TaskCompletionSource<object?>? _tcs;
    private long _count;

    private void IncrementCount()
    {
        lock (_mutex)
        {
            _tcs ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            _count++;
        }
    }

    private void DecrementCount()
    {
        lock (_mutex)
        {
            if (_tcs == null)
                throw new InvalidOperationException("You must call IncrementCount before calling DecrementCount.");
            _count--;
            if (_count == 0)
            {
                var oldTcs = _tcs;
                _tcs = null;
                oldTcs.SetResult(null);
            }
            else if (_count < 0)
                throw new InvalidOperationException();
        }
    }

    public Task WaitForDeferralsAsync()
    {
        lock (_mutex)
        {
            if (_tcs == null)
                return Task.CompletedTask;
            return _tcs.Task;
        }
    }

    public Deferral GetDeferral() => new(this);

    public sealed class Deferral : IDisposable
    {
        private readonly DeferralManager _manager;

        public Deferral(DeferralManager manager)
        {
            _manager = manager;
            _manager.IncrementCount();
        }

        public void Dispose() => _manager.DecrementCount();
    }
}
