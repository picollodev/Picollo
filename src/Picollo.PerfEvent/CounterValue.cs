using System.Runtime.InteropServices;

namespace Picollo;

[StructLayout(LayoutKind.Sequential)]
public struct CounterValue
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

    public static CounterValue operator +(CounterValue left, CounterValue right)
        => new CounterValue
        {
            Value = left.Value + right.Value,
            TimeEnabled = left.TimeEnabled + right.TimeEnabled,
            TimeRunning = left.TimeRunning + right.TimeRunning
        };

    public static CounterValue operator -(CounterValue left, CounterValue right)
    {
        return new CounterValue
        {
            Value = left.Value - right.Value,
            TimeEnabled = left.TimeEnabled - right.TimeEnabled,
            TimeRunning = left.TimeRunning - right.TimeRunning
        };
    }
}