using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines
{
    internal interface IPipeSynchronizer
    {
        bool Wait(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default);

        ValueTask<bool> WaitAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default);

        void Pulse();
    }

    internal sealed class BusySpinPipeSynchronizer : IPipeSynchronizer
    {
        private readonly bool _shouldYield;
        private long _version;
        private long _observedVersion;

        public BusySpinPipeSynchronizer(bool shouldYield)
        {
            _shouldYield = shouldYield;
        }

        public bool Wait(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
        {
            if (timeoutMilliseconds < Timeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            }

            cancellationToken.ThrowIfCancellationRequested();

            long version = Volatile.Read(ref _version);
            if (_observedVersion != version)
            {
                _observedVersion = version;
                return true;
            }

            bool hasTimeout = timeoutMilliseconds != Timeout.Infinite;
            Stopwatch? stopwatch = hasTimeout ? Stopwatch.StartNew() : null;
            SpinWait spinner = default;

            while ((version = Volatile.Read(ref _version)) == _observedVersion)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (hasTimeout && stopwatch!.ElapsedMilliseconds >= timeoutMilliseconds)
                {
                    return false;
                }

                if (spinner.NextSpinWillYield)
                {
                    spinner.Reset();
                    if (_shouldYield)
                    {
                        Thread.Yield();
                    }
                }
                else
                {
                    spinner.SpinOnce();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _observedVersion = version;
            return true;
        }

        public void Pulse()
        {
            Interlocked.Increment(ref _version);
        }

        public ValueTask<bool> WaitAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Asynchronous waiting is not supported by the busy-spin synchronizer.");
    }

    internal sealed class SemaphoreSlimPipeSynchronizer : IPipeSynchronizer, IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(0, 1);

        private int _waitActive;

        private void BeginWait()
        {
            if (Interlocked.CompareExchange(ref _waitActive, 1, 0) != 0)
                throw new InvalidOperationException("Another wait operation is already active.");
        }

        private void EndWait()
        {
            Interlocked.Exchange(ref _waitActive, 0);
        }

        public bool Wait(int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            BeginWait();
            try
            {
                return _semaphore.Wait(timeoutMilliseconds, cancellationToken);
            }
            finally
            {
                EndWait();
            }
        }

        public async ValueTask<bool> WaitAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            BeginWait();
            try
            {
                return await _semaphore
                    .WaitAsync(timeoutMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                EndWait();
            }
        }

        public void Pulse()
        {
            if (_semaphore.CurrentCount == 0)
                _semaphore.Release();
        }

        public void Dispose() => _semaphore.Dispose();
    }
}