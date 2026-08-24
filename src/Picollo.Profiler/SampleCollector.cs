using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Picollo.Profiler.IpResolution;
using Picollo.Profiling;
using Picollo.Profiling.Messages;
using ThreadMethodCounters = Picollo.Profiler.IpResolution.ThreadMethodCounters;

namespace Picollo.Profiler;

internal sealed class SampleCollector
{
    private readonly IpRangeCache<IResolvedMethod> _methodCache = new();
    private (int Tid, ThreadSampleCollector? ThreadCache) _lastThreadCache;
    private readonly Dictionary<int, ThreadSampleCollector> _threadCaches = new();
    private readonly Dictionary<uint, ThreadInfo> _submittedThreads = new();
    private readonly Func<ulong, bool, IResolvedMethod> _resolve;
    private Action<InputChunk>? _chunkPublisher;
    private readonly Action<CallCountersMessage>? _callCountersPublisher;

    private int _publishedFrameCount;
    private readonly InputChunk _chunk;
    private CallCountersMessage? _callCountersMessage;
    private string? _segmentName;

    private long _publishedTotalCount;
    public long TotalCount { get; private set; }

    public SampleCollector(Func<ulong, bool, IResolvedMethod> resolve, Action<InputChunk>? chunkPublisher,
        Action<CallCountersMessage>? callCountersPublisher)
    {
        _resolve = resolve;
        _chunkPublisher = chunkPublisher;
        _callCountersPublisher = callCountersPublisher;

        _chunk = new InputChunk
        {
            Metadata = new Metadata
            {
                FrameTypes = ResolvedMethod.FrameTypes,
                Frames = []
            },
            Profiles = []
        };
    }

    public void OnThreadSubmitted(ThreadInfo threadInfo)
    {
        _submittedThreads[threadInfo.OsThreadId] = threadInfo;
    }

    public void SetSegmentName(string? name)
    {
        if (name is null || name.Equals(_segmentName))
            return;

        if (_segmentName is not null)
        {
            PublishChunk();
            PublishCallCounters();
        }

        _segmentName = name;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ThreadSampleCollector GetOrCreateThreadSampleCollector(int tid)
    {
        if (_lastThreadCache.Tid == tid && _lastThreadCache.ThreadCache is { } threadCache)
            return threadCache;

        if (!_threadCaches.TryGetValue(tid, out threadCache))
        {
            threadCache = new ThreadSampleCollector(tid, _methodCache, _resolve);
            _threadCaches.Add(tid, threadCache);
        }

        _lastThreadCache = (tid, threadCache);

        return threadCache;
    }

    public void OnSample(in IpSampleHeader ipSampleHeader, ReadOnlySpan<ulong> ips)
    {
        TotalCount++;
        GetOrCreateThreadSampleCollector(ipSampleHeader.Tid).OnSample(in ipSampleHeader, ips);
    }

    public int Count => _threadCaches.Count;

    public void PrettyPrint(int minThreadSamples = 0)
    {
        foreach (var (_, value) in _threadCaches.OrderBy(x => x.Key))
        {
            if (value.ThreadTotalCount > 0)
                value.PrettyPrint(minThreadSamples);
        }
    }

    private class ThreadSampleCollector
    {
        public SampledProfile SampledProfile { get; set; } = new()
        {
            Unit = "milliseconds",
            StartValue = DateTime.UtcNow.TimeOfDay.TotalMilliseconds
        };

        private readonly IpRangeCache<IResolvedMethod> _methodCache;
        private readonly Func<ulong, bool, IResolvedMethod> _resolve;
        public int Tid { get; }
        public long ThreadTotalCount { get; set; }

        internal readonly Dictionary<IResolvedMethod, ThreadMethodCounters> SeenMethods = new(ReferenceEqualityComparer.Instance);
        internal readonly ThreadMethodCounters KernelPlaceholder;

        private readonly List<int> _frameScratch = new(64);

        public ThreadSampleCollector(int tid, IpRangeCache<IResolvedMethod> methodCache, Func<ulong, bool, IResolvedMethod> resolve)
        {
            Tid = tid;
            _methodCache = methodCache;
            _resolve = resolve;

            KernelPlaceholder = new ThreadMethodCounters(KernelModule.MethodPlaceholder);

            SampledProfile.Name = $"TID: {tid}";
            SampledProfile.Tid = $"{tid}";
            SampledProfile.FlatSamples = new FlatSamples();
        }

        public void OnSample(in IpSampleHeader ipSampleHeader, ReadOnlySpan<ulong> ips)
        {
            ThreadTotalCount++;
            _frameScratch.Clear();

            ThreadMethodCounters leafMethodCounter = TryGetOrCreateMethod(ips[0], ipSampleHeader.IsKernel);

            var leafFrameIdx = leafMethodCounter.Method.Index;
            _frameScratch.Add(leafFrameIdx);
            int previousFrameId = leafFrameIdx;

            leafMethodCounter.IncrementOwn();
            leafMethodCounter.IncrementTotal();

            var incrementOwnPlusForFirstKnownManaged = !leafMethodCounter.Method.Module.IsKnownManaged;

            for (int i = 1; i < ips.Length; i++)
            {
                ulong callChainIp = ips[i];

                if (callChainIp == 0) // Old code used zero termination, should be safe to delete
                    break;

                // ETW skips kernel frames, perf_event is configured not to receive them 
                var callChainMethodCounter = TryGetOrCreateMethod(callChainIp, false);

                int frameIdx = callChainMethodCounter.Method.Index;
                if (frameIdx != previousFrameId) // Mutually recursive calls do pass, only adjacent recursive frames are excluded
                {
                    if (!_frameScratch.Contains(frameIdx)) // But for inclusive count, any previous occurence is already accounted for, avoid double counting
                        callChainMethodCounter.IncrementTotal();

                    _frameScratch.Add(frameIdx);
                    previousFrameId = frameIdx;
                }

                // If the leaf IP was not managed, add it to the first managed frame.
                if (incrementOwnPlusForFirstKnownManaged && callChainMethodCounter.Method.Module.IsKnownManaged)
                {
                    callChainMethodCounter.IncrementOwnPlus();
                    incrementOwnPlusForFirstKnownManaged = false;
                }
            }

            var framesSpan = CollectionsMarshal.AsSpan(_frameScratch);

            framesSpan.Reverse(); // Samplers have leaf->root, but Picolloscope needs root->leaf

            SampledProfile.FlatSamples!.Add(framesSpan);
        }

        private ThreadMethodCounters TryGetOrCreateMethod(ulong ip, bool isKernel)
        {
            if (isKernel)
                return KernelPlaceholder;

            if (!_methodCache.TryFind(ip, out IResolvedMethod? method))
            {
                method = _resolve.Invoke(ip, false);
                _methodCache.AddOrReplaceRanges(method);
            }

            method.HitCount++;

            if (!SeenMethods.TryGetValue(method, out var counter))
            {
                SeenMethods[method] = counter = new ThreadMethodCounters(method);
            }

            return counter;
        }

        public void PrettyPrint(int minSamples)
        {
            if (ThreadTotalCount < minSamples)
                return;

            // TODO Should keep a flat list of method instances
            var methods =
                SeenMethods.Select(x => x.Value)
                    .Concat([KernelPlaceholder])
                    .OrderByDescending(x => x.OwnCount)
                    .ToList();

            if (methods.Count == 0)
                return;

            var observedOwnShare = 0.0;
            var observedOwnPlusShare = 0.0;

            Console.WriteLine($"TID: {Tid}");

            foreach (var methodCounter in methods)
            {
                double totalShare = methodCounter.TotalCount * 100.0 / ThreadTotalCount;
                double ownShare = methodCounter.OwnCount * 100.0 / ThreadTotalCount;
                double ownPlusShare = methodCounter.OwnPlusCount * 100.0 / ThreadTotalCount;

                observedOwnShare += ownShare;
                if (methodCounter.Method.Module.IsManaged)
                {
                    observedOwnPlusShare += ownPlusShare;
                }

                if (ownPlusShare >= 1)
                {
                    // ManagedResolvedMethod? rmm = methodCounter as ManagedResolvedMethod;
                    Console.WriteLine(
                        $"\t Own+:{ownPlusShare:N2}%, Own:{ownShare:N2}%, Total {totalShare:N2}% |" +
                        $"  {methodCounter.Method} ({methodCounter.TotalCount}/{methodCounter.OwnCount}/{methodCounter.OwnPlusCount}/{ThreadTotalCount}), range count: {methodCounter.Method.IpRangeSet.Count} (implied {methodCounter.Method.IpRangeSet.ImpliedCount})"); // , IsDllImportAttribute={rmm?.IsDllImportAttribute}, IsLibraryImport={rmm?.IsLibraryImport}, IsDllImportPinvoke={rmm?.IsDllImportPinvoke}, IsInternalCall={rmm?.IsInternalCall}
                }
            }

            Console.WriteLine($"\t Observed Own: {observedOwnShare:N2}%, Own+: {observedOwnPlusShare:N2}%");
        }
    }

    public void DropSamples()
    {
        var published = Interlocked.Exchange(ref _chunkPublisher, null);
        try
        {
            PublishChunk();
        }
        finally
        {
            _chunkPublisher = published;
        }
    }


    public bool PublishChunk()
    {
        if (TotalCount == _publishedTotalCount)
            return false; // Nothing to do

        var publisher = _chunkPublisher;

        if (publisher is not null)
        {
            _chunk.Name = _segmentName;

            var knownMethods = ResolvedMethod.KnownMethods;
            _chunk.Metadata.Frames.Clear();
            const bool useMetadataName = false;

            for (int i = _publishedFrameCount; i < knownMethods.Count; i++)
            {
                var resolvedMethod = knownMethods[i];
                var managedMethod = resolvedMethod as ManagedResolvedMethod;
                var frameInfo = new FrameInfo
                {
                    Name = useMetadataName && managedMethod is not null
                        ? managedMethod.Metadata.Format(MetadataFormatting.Full)
                        : resolvedMethod.GetName(),
                    File = resolvedMethod.Module.ModuleName,
                    Type = resolvedMethod.GetFrameType(),
                    ModuleMvid = managedMethod?.ModuleMvid,
                    MethodToken = managedMethod?.MethodToken,
                    MethodMetadata = managedMethod?.Metadata,
                };
                _chunk.Metadata.Frames.Add(frameInfo);
            }

            _publishedFrameCount = knownMethods.Count;

            _chunk.Profiles.Clear();

            foreach (var threadCollector in _threadCaches.Values)
            {
                var tid = threadCollector.Tid;
                if (!_submittedThreads.TryGetValue((uint)tid, out var threadInfo))
                    continue;

                if (!string.IsNullOrWhiteSpace(threadInfo.Name))
                    threadCollector.SampledProfile.Name = $"{threadInfo.Name}";

                var profile = threadCollector.SampledProfile as Profile;
                profile.EndValue = DateTime.UtcNow.TimeOfDay.TotalMilliseconds;
                _chunk.Profiles.Add(profile);
            }

            publisher.Invoke(_chunk);
        }

        foreach (var threadCollector in _threadCaches.Values)
        {
            threadCollector.SampledProfile.FlatSamples!.Clear();
            threadCollector.SampledProfile.StartValue = DateTime.UtcNow.TimeOfDay.TotalMilliseconds;
        }

        _publishedTotalCount = TotalCount;
        return true;
    }

    public void PublishCallCounters()
    {
        var publisher = _callCountersPublisher;

        if (publisher is not null)
        {
            _callCountersMessage ??= new CallCountersMessage
            {
                Metadata = new Metadata
                {
                    FrameTypes = ResolvedMethod.FrameTypes
                },
                ThreadMethodCounters =
                [
                    new ThreadCounters
                    {
                        Name = "Total",
                        UniqueId = ""
                    }
                ]
            };

            var message = _callCountersMessage;
            message.Name = _segmentName;
            message.Metadata.Frames.Clear();

            var totalCounters = message.ThreadMethodCounters[0];
            message.ThreadMethodCounters.Clear();
            message.ThreadMethodCounters.Add(totalCounters);
            totalCounters.FrameCounters.Clear();

            var knownMethods = ResolvedMethod.KnownMethods;
            const bool useMetadataName = false;

            foreach (var method in knownMethods)
            {
                var managedMethod = method as ManagedResolvedMethod;
                message.Metadata.Frames.Add(new FrameInfo
                {
                    Name = useMetadataName && managedMethod is not null
                        ? managedMethod.Metadata.Format(MetadataFormatting.Full)
                        : method.GetName(),
                    File = method.Module.ModuleName,
                    Type = method.GetFrameType(),
                    ModuleMvid = managedMethod?.ModuleMvid,
                    MethodToken = managedMethod?.MethodToken,
                    MethodMetadata = managedMethod?.Metadata,
                });

                totalCounters.FrameCounters.Add(new FrameCounters
                {
                    FrameIndex = method.Index
                });
            }

            var totalCountersSpan = CollectionsMarshal.AsSpan(totalCounters.FrameCounters);

            foreach (var threadCollector in _threadCaches.Values)
            {
                var tid = threadCollector.Tid;
                if (!_submittedThreads.TryGetValue((uint)tid, out var threadInfo) || ThreadsLookup.Instance.IsExcluded(threadInfo))
                    continue;

                var threadCounters = new ThreadCounters
                {
                    Name = $"TID: {tid}",
                    UniqueId = $"{tid}"
                };

                if (!string.IsNullOrWhiteSpace(threadInfo.Name))
                    threadCounters.Name = $"{threadInfo.Name} (TID: {tid})";

                foreach (var counter in threadCollector.SeenMethods.Values)
                    AddFrameCounters(threadCounters.FrameCounters, totalCountersSpan, counter);

                AddFrameCounters(threadCounters.FrameCounters, totalCountersSpan, threadCollector.KernelPlaceholder);
                threadCounters.FrameCounters.Sort(CompareFrameCounters);
                message.ThreadMethodCounters.Add(threadCounters);
            }

            totalCounters.FrameCounters.Sort(CompareFrameCounters);

            var totalCount = totalCounters.FrameCounters.Count;
            while (totalCount > 0)
            {
                var counter = totalCounters.FrameCounters[totalCount - 1];
                if (counter.OwnPlus != 0 || counter.Own != 0 || counter.Total != 0)
                    break;
                totalCount--;
            }

            if (totalCount != totalCounters.FrameCounters.Count)
                totalCounters.FrameCounters.RemoveRange(totalCount, totalCounters.FrameCounters.Count - totalCount);

            publisher(message);
        }

        ResetCallCounters();

        static void AddFrameCounters(List<FrameCounters> destination, Span<FrameCounters> totalCounters,
            ThreadMethodCounters counter)
        {
            destination.Add(new FrameCounters
            {
                FrameIndex = counter.Method.Index,
                Total = counter.TotalCount,
                Own = counter.OwnCount,
                OwnPlus = counter.OwnPlusCount
            });

            ref var totalCounter = ref totalCounters[counter.Method.Index];
            totalCounter.Total += counter.TotalCount;
            totalCounter.Own += counter.OwnCount;
            totalCounter.OwnPlus += counter.OwnPlusCount;
        }

        static int CompareFrameCounters(FrameCounters x, FrameCounters y)
        {
            var result = y.OwnPlus.CompareTo(x.OwnPlus);
            if (result != 0)
                return result;

            result = y.Own.CompareTo(x.Own);
            return result != 0 ? result : y.Total.CompareTo(x.Total);
        }

    }

    internal void ResetCallCounters()
    {
        foreach (var method in ResolvedMethod.KnownMethods)
            method.ResetCounters();

        foreach (var threadCollector in _threadCaches.Values)
        {
            foreach (var counter in threadCollector.SeenMethods.Values)
                counter.ResetCounters();

            threadCollector.KernelPlaceholder.ResetCounters();
        }
    }
}
