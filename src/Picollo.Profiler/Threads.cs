using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Picollo.Profiler;

internal class ThreadInfo
{
    public nuint ManagedThreadId { get; set; }
    public uint OsThreadId { get; set; }

    /// <summary>
    /// See <see cref="Profiler.Id"/>.
    /// </summary>
    public long SumbittedTo { get; set; }

    public string? Name { get; set; }

    public bool IsComplete => OsThreadId > 0;

    public override string ToString() =>
        $"{nameof(ManagedThreadId)}: {ManagedThreadId}, {nameof(OsThreadId)}: {OsThreadId}, {nameof(Name)}: {Name}, {nameof(SumbittedTo)}: {SumbittedTo}";
}

internal class ThreadsLookup
{
    public static readonly ThreadsLookup Instance = new();

    private static readonly ILogger Log = Logger.ForType<ThreadsLookup>();

    private volatile uint[]? _osThreadsFilter;
    private volatile string[]? _threadNameFilter;

    private readonly List<ThreadInfo> _threads = new();

    private ThreadsLookup()
    {
    }

    public void SetFilters(uint[]? osThreadsFilter = null, string[]? threadNameFilter = null)
    {
        _threadNameFilter = threadNameFilter is {Length: > 0} ? threadNameFilter : null;
        _osThreadsFilter = osThreadsFilter is {Length: > 0} ? osThreadsFilter : null;
    }
    
    public void Reset()
    {
        lock (_threads)
        {
            // Do not clear filetrs
            _threads.Clear();
        }
    }

    public ThreadInfo UpdateOrCreate(nuint managedThreadId, uint osThreadId)
    {
        if (managedThreadId == 0)
            throw new InvalidOperationException("Tried to add zero managedThreadId");


        lock (_threads)
        {
            var existing = GetOrNull(managedThreadId, osThreadId);

            if (existing is not null)
            {
                if (osThreadId != 0)
                {
                    if (existing.OsThreadId != 0 && existing.OsThreadId != osThreadId)
                        throw new InvalidOperationException("Managed to OS threads should be mapped 1-to-1");

                    existing.OsThreadId = osThreadId;
                }

                if (string.IsNullOrWhiteSpace(existing.Name))
                    existing.Name = GetTheadName(osThreadId);

                return existing;
            }

            var threadInfo = new ThreadInfo
            {
                ManagedThreadId = managedThreadId,
                OsThreadId = osThreadId,
                Name = osThreadId != 0 ? GetTheadName(osThreadId) : null
            };
            _threads.Add(threadInfo);
            Log.LogDebug($"Added thread: {threadInfo}");
            return threadInfo;
        }
    }

    public bool IsExcluded(ThreadInfo threadInfo)
    {
        bool hasFilters = _osThreadsFilter is not null || _threadNameFilter is not null;
        bool matches = _osThreadsFilter?.Contains(threadInfo.OsThreadId) == true ||
                       _threadNameFilter?.Any(x => threadInfo.Name?.Contains(x, StringComparison.OrdinalIgnoreCase) == true) == true;
        return hasFilters && !matches;
    }

    public bool TryRemove(nuint managedThreadId, uint osThreadId, [NotNullWhen(true)] out ThreadInfo? threadInfo)
    {
        lock (_threads)
        {
            threadInfo = GetOrNull(managedThreadId, osThreadId);

            if (threadInfo is null)
            {
                Log.LogDebug($"Tried to remove unknown thread with ids: {managedThreadId}/{osThreadId}");
                return false;
            }

            _threads.Remove(threadInfo);
            Log.LogDebug($"Removed thread: {threadInfo}");
            return true;
        }
    }

    public ThreadInfo? GetOrNull(nuint managedThreadId, uint osThreadId = 0)
    {
        lock (_threads)
        {
            foreach (var threadInfo in _threads)
            {
                if (threadInfo.ManagedThreadId == managedThreadId || (osThreadId != 0 && threadInfo.OsThreadId == osThreadId))
                    return threadInfo;
            }

            return null;
        }
    }
    
    public ThreadInfo GetOrCreate(nuint managedThreadId, uint osThreadId = 0)
    {
        lock (_threads)
        {
            foreach (var threadInfo in _threads)
            {
                if (threadInfo.ManagedThreadId == managedThreadId || (osThreadId != 0 && threadInfo.OsThreadId == osThreadId))
                {
                    threadInfo.OsThreadId = osThreadId;
                    return threadInfo;
                }
            }

            var newThreadInfo = new ThreadInfo
            {
                ManagedThreadId = managedThreadId,
                OsThreadId = osThreadId
            };
            _threads.Add(newThreadInfo);
            return newThreadInfo;
        }
    }

    public void CreateByManagedId(nuint managedThreadId)
    {
        ThreadInfo? threadInfo;
        lock (_threads)
        {
            threadInfo = GetOrNull(managedThreadId);
            if (threadInfo is null)
                _threads.Add(threadInfo = new ThreadInfo {ManagedThreadId = managedThreadId});
        }

        Log.LogDebug($"Crated managed thread: {threadInfo}");
    }

    public ThreadInfo UpdateNameByManagedId(nuint managedThreadId, string name)
    {
        ThreadInfo? threadInfo;

        lock (_threads)
        {
            threadInfo = GetOrNull(managedThreadId);
            if (threadInfo is null)
                _threads.Add(threadInfo = new ThreadInfo {ManagedThreadId = managedThreadId, Name = name});
            else
                threadInfo.Name = name;
        }
        
        Log.LogDebug($"Updated managed thread name: {threadInfo}");

        return threadInfo;
    }

    private string GetTheadName(uint osThreadId)
    {
        if (osThreadId <= 0)
            return string.Empty;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetWindowsThreadName(osThreadId);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetLinuxThreadName((int)osThreadId);

        return string.Empty;
    }

    private static string GetLinuxThreadName(int tid)
    {
        try
        {
            // Linux TIDs are directly addressable under /proc/<tid>.
            var path = $"/proc/{tid}/comm";

            if (!File.Exists(path))
                return string.Empty;

            return File.ReadAllText(path).TrimEnd('\n', '\r');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowsThreadName(uint tid)
    {
        IntPtr hThread = OpenThread(
            THREAD_QUERY_LIMITED_INFORMATION,
            false,
            tid);

        if (hThread == IntPtr.Zero)
            return string.Empty;

        try
        {
            int hr = GetThreadDescription(hThread, out IntPtr namePtr);

            if (hr < 0 || namePtr == IntPtr.Zero)
                return string.Empty;

            try
            {
                return Marshal.PtrToStringUni(namePtr) ?? string.Empty;
            }
            finally
            {
                LocalFree(namePtr);
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            CloseHandle(hThread);
        }
    }

    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwThreadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetThreadDescription(
        IntPtr hThread,
        out IntPtr ppszThreadDescription);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);


    
}
