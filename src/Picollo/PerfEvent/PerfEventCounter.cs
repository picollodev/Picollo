using System;
using System.Collections.Generic;

namespace Picollo.PerfEvent;

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

    /// <summary>
    /// Estimated overhead between two consecutive calls to <see cref="PerfEventCounterSession.Read"/>.
    /// This value is already taken into account for <see cref="Delta"/>, where the counter value is reduced by this overhead. 
    /// </summary>
    /// <seealso cref="Delta"/>
    /// <seealso cref="RawDelta"/>
    public ulong PairReadOverhead { get; internal set; }

    /// <summary>
    /// The counter value after the last call to <see cref="PerfEventCounterSession.Read"/>.
    /// </summary>
    /// <seealso cref="Delta"/>
    /// <seealso cref="RawDelta"/>
    /// <seealso cref="PairReadOverhead"/>
    public unsafe PerfEventCounterValue Current => Session.CounterValuesPtr[Index];

    /// <summary>
    /// The counter difference between last two calls to <see cref="PerfEventCounterSession.Read"/> without any adjustments.
    /// </summary>
    /// <seealso cref="Delta"/>
    /// <seealso cref="PairReadOverhead"/>
    /// <seealso cref="Current"/>
    public unsafe PerfEventCounterValue RawDelta => Session.CounterValuesPtr[Index] - Session.PreviousCounterValuesPtr[Index];

    /// <summary>
    /// The counter difference between last two calls to <see cref="PerfEventCounterSession.Read"/>, adjusted for calibrated overhead <see cref="PairReadOverhead"/>.
    /// </summary>
    /// <seealso cref="RawDelta"/>
    /// <seealso cref="PairReadOverhead"/>
    /// <seealso cref="Current"/>
    public PerfEventCounterValue Delta
    {
        get
        {
            var rawDelta = RawDelta;
            return new PerfEventCounterValue
            {
                Value = rawDelta.Value < PairReadOverhead ? 0 : rawDelta.Value - PairReadOverhead,
                TimeEnabled = rawDelta.TimeEnabled,
                TimeRunning = rawDelta.TimeRunning
            };
        }
    }

    /// <summary>
    /// The counter name.
    /// </summary>
    public string Name => field ??= GetName(Type, Config);

    internal static string GetName(PerfTypeId type, ulong config)
    {
        string subName = "";
        switch (type)
        {
            case PerfTypeId.Hardware:
                subName = $"{((PerfHardwareCounterId)config):G}";
                break;
            case PerfTypeId.Software:
                subName = $"{((PerfSoftwareCounterId)config):G}";
                break;
            // case PerfTypeId.Tracepoint:
            //     break;
            case PerfTypeId.HardwareCache:
                subName = $"{((PerfCacheCounterId)config):G}";
                break;
            case PerfTypeId.Raw:
                subName = $"{config}";
                break;
            // case PerfTypeId.Breakpoint:
            //     break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var name = $"{type:G}:{subName}";
        return name;
    }

    public override string ToString() =>
        $"{Name}: Current={Current.Value:N0}, Delta={Delta.Value:N0} (MUX={Current.TimeRunning != Current.TimeEnabled})";
}