using System.Runtime.InteropServices;

namespace Picollo.PerfEvent;

[StructLayout(LayoutKind.Sequential)]
public struct PerfEventCounterValue
{
    public ulong Value;
    public ulong TimeEnabled;
    public ulong TimeRunning;

    public ulong ScaledValue => ScaleToUInt64(Value, TimeEnabled, TimeRunning);

    private static ulong ScaleToUInt64(ulong value, ulong timeEnabled, ulong timeRunning)
    {
        if (timeEnabled == timeRunning)
            return value;

        if (timeRunning == 0)
            return 0;

        return (ulong)((double)value * timeEnabled / timeRunning);
    }

    public static PerfEventCounterValue operator +(PerfEventCounterValue left, PerfEventCounterValue right)
        => new PerfEventCounterValue
        {
            Value = left.Value + right.Value,
            TimeEnabled = left.TimeEnabled + right.TimeEnabled,
            TimeRunning = left.TimeRunning + right.TimeRunning
        };

    public static PerfEventCounterValue operator -(PerfEventCounterValue left, PerfEventCounterValue right)
    {
        return new PerfEventCounterValue
        {
            Value = left.Value - right.Value,
            TimeEnabled = left.TimeEnabled - right.TimeEnabled,
            TimeRunning = left.TimeRunning - right.TimeRunning
        };
    }
}