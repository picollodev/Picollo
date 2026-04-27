using System;
using System.Runtime.InteropServices;

namespace Picollo.PerfEvent;

internal static class NativeMethods
{
    [DllImport("picollo_native", EntryPoint = "is_supported")]
    private static extern int IsSupportedNative();

    public static bool IsSupported()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return false;
        }

        try
        {
            return IsSupportedNative() == 1;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public static void ThrowIfNotSupported()
    {
        if (!IsSupported())
        {
            throw new PlatformNotSupportedException("PerfEvent is only supported on Linux x64.");
        }
    }
}