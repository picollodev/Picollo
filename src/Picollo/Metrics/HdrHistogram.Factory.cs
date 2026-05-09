using System;

namespace Picollo.Metrics;

public abstract partial class HdrHistogram
{
    private const double DefaultRelativeError = 0.001;
    private static readonly TimeSpan DefaultThreadLocalAccumulateInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Creates a new thread-unsafe HdrHistogram backed by uint64 storage counters, the relative precision of 0.001 (3 significant digits) and maxTrackableValue = ulong.MaxValue.
    /// This is a safe default for single-threaded usage, but if you need higher precision or less memory usage, use <see cref="Factory"/> to change the defaults. 
    /// </summary>
    public static HdrHistogram Create() => new UInt64HdrHistogram(DefaultRelativeError, 0, ulong.MaxValue);

    /// <summary>
    /// Gets a factory for configuring and creating histogram instances.
    /// </summary>
    public static HdrHistogramFactory Factory => new();

    public readonly ref struct HdrHistogramFactory
    {
        private readonly double? _relativeError;
        private readonly ulong? _minTrackableValue;
        private readonly ulong? _maxTrackableValue;
        private readonly bool? _useInterlocked;
        private readonly bool? _useThreadLocal;
        private readonly bool? _useUInt32;
        private readonly TimeSpan? _threadLocalAccumulateInterval;

        private HdrHistogramFactory(
            double? relativeError = null,
            ulong? minTrackableValue = null,
            ulong? maxTrackableValue = null,
            bool? useInterlocked = null,
            bool? useThreadLocal = null,
            bool? useUInt32 = null,
            TimeSpan? threadLocalAccumulateInterval = null)
        {
            _relativeError = relativeError;
            _minTrackableValue = minTrackableValue;
            _maxTrackableValue = maxTrackableValue;
            _useInterlocked = useInterlocked;
            _useThreadLocal = useThreadLocal;
            _useUInt32 = useUInt32;
            _threadLocalAccumulateInterval = threadLocalAccumulateInterval;
        }

        /// <summary>
        /// Configures whether the histogram uses interlocked counter updates.
        /// </summary>
        public HdrHistogramFactory WithInterlocked(bool enabled = true) =>
            new(_relativeError, _minTrackableValue, _maxTrackableValue, enabled, _useThreadLocal, _useUInt32,
                _threadLocalAccumulateInterval);

        /// <summary>
        /// Configures whether the histogram uses thread-local writer storage.
        /// </summary>
        public HdrHistogramFactory WithThreadLocal(bool enabled = true, TimeSpan? accumulateInterval = null) =>
            new(
                _relativeError,
                _minTrackableValue,
                _maxTrackableValue,
                _useInterlocked,
                enabled,
                _useUInt32,
                accumulateInterval ?? _threadLocalAccumulateInterval);

        /// <summary>
        /// Configures whether the histogram uses 32-bit counter storage.
        /// </summary>
        public HdrHistogramFactory WithUInt32Storage(bool enabled = true) =>
            new(_relativeError, _minTrackableValue, _maxTrackableValue, _useInterlocked, _useThreadLocal, enabled,
                _threadLocalAccumulateInterval);

        /// <summary>
        /// Configures the histogram to use 64-bit counter storage.
        /// </summary>
        public HdrHistogramFactory WithUInt64Storage() =>
            new(_relativeError, _minTrackableValue, _maxTrackableValue, _useInterlocked, _useThreadLocal, false,
                _threadLocalAccumulateInterval);

        /// <summary>
        /// Configures the target relative error.
        /// </summary>
        public HdrHistogramFactory WithRelativeError(double value) =>
            new(value, _minTrackableValue, _maxTrackableValue, _useInterlocked, _useThreadLocal, _useUInt32,
                _threadLocalAccumulateInterval);

        /// <summary>
        /// Configures the minimum trackable value.
        /// </summary>
        public HdrHistogramFactory WithMinTrackableValue(ulong value) =>
            new(_relativeError, value, _maxTrackableValue, _useInterlocked, _useThreadLocal, _useUInt32, _threadLocalAccumulateInterval);

        /// <summary>
        /// Configures the maximum trackable value.
        /// </summary>
        public HdrHistogramFactory WithMaxTrackableValue(ulong value) =>
            new(_relativeError, _minTrackableValue, value, _useInterlocked, _useThreadLocal, _useUInt32, _threadLocalAccumulateInterval);

        // ReSharper disable once MemberHidesStaticFromOuterClass

        /// <summary>
        /// Creates a configured <see cref="HdrHistogram"/>.
        /// </summary>
        public HdrHistogram Create()
        {
            var relativeError = _relativeError ?? DefaultRelativeError;
            var minTrackableValue = _minTrackableValue ?? 0;
            var maxTrackableValue = _maxTrackableValue ?? ulong.MaxValue;
            var useInterlocked = _useInterlocked ?? false;
            var useThreadLocal = _useThreadLocal ?? false;
            var useUInt32 = _useUInt32 ?? false;

            if (useInterlocked && useThreadLocal)
                throw new InvalidOperationException("Use only one of interlocked/thread-local configuration.");

            if (useThreadLocal)
            {
                var accumulateInterval = _threadLocalAccumulateInterval ?? DefaultThreadLocalAccumulateInterval;
                return useUInt32
                    ? new ThreadLocalHdrHistogram<uint>(relativeError, minTrackableValue, maxTrackableValue, accumulateInterval)
                    : new ThreadLocalHdrHistogram<ulong>(relativeError, minTrackableValue, maxTrackableValue, accumulateInterval);
            }

            if (useInterlocked)
            {
                return useUInt32
                    ? new InterlockedUInt32HdrHistogram(relativeError, minTrackableValue, maxTrackableValue)
                    : new InterlockedUInt64HdrHistogram(relativeError, minTrackableValue, maxTrackableValue);
            }

            return useUInt32
                ? new UInt32HdrHistogram(relativeError, minTrackableValue, maxTrackableValue)
                : new UInt64HdrHistogram(relativeError, minTrackableValue, maxTrackableValue);
        }
    }
}