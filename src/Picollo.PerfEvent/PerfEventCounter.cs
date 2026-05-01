using System;
using System.Collections.Generic;

using Picollo.PerfEvent;

namespace Picollo;

public class PerfEventCounter
{
    public PerfEventCounterSession Session { get; }
    public PerfTypeId Type { get; }
    public ulong Config { get; }
    internal int Fd = -1;
    internal nint MmapPage;
    internal ulong Id;
    internal int Index;

    public unsafe bool HasUserTime => MmapPage > 0 &&
                                      (((PerfEventMMapPage*)MmapPage)->Capabilities & PerfEventMmapCapabilities.CapUserTime) != 0;

    public unsafe bool HasUserRdpmc => MmapPage > 0 &&
                                       (((PerfEventMMapPage*)MmapPage)->Capabilities & PerfEventMmapCapabilities.CapUserRdpmc) != 0;

    internal PerfEventCounter(PerfEventCounterSession session, PerfTypeId type, ulong config)
    {
        Session = session;
        Type = type;
        Config = config;
    }

    internal List<ulong> PairReadOverheadList = new List<ulong>(PerfEventCounterSession.OverheadCalibrationIterations);
    internal CounterValue PairReadOverhead;

    public unsafe CounterValue Current => Session.CounterValuesPtr[Index];

    internal unsafe CounterValue RawDelta => Session.CounterValuesPtr[Index] - Session.PreviousCounterValuesPtr[Index];

    public CounterValue Delta
    {
        get
        {
            var rawDelta = RawDelta;
            if (rawDelta.Value < PairReadOverhead.Value)
                return new CounterValue { Value = 0, TimeEnabled = rawDelta.TimeEnabled, TimeRunning = rawDelta.TimeRunning };

            return rawDelta - PairReadOverhead;
        }
    }

    public string Name => field ??= GetName(Type, Config);

    internal static string GetName(PerfTypeId type, ulong config)
    {
        string subName = "";
        switch (type)
        {
            case PerfTypeId.Hardware:
                subName = $"{((PerfHwId)config):G}";
                break;
            case PerfTypeId.Software:
                subName = $"{((PerfSwIds)config):G}";
                break;
            case PerfTypeId.Tracepoint:
                break;
            case PerfTypeId.HardwareCache:
                subName = $"{((PerfCacheId)config):G}";
                break;
            case PerfTypeId.Raw:
                break;
            case PerfTypeId.Breakpoint:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var name = $"{type:G}:{subName}";
        return name;
    }

    public override string ToString()
    {
        return $"{Name}: {Delta.Value:N0} ({Current.Value:N0}, mpx={Delta.TimeRunning != Delta.TimeEnabled})";
    }
}