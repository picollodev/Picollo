using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Picollo.Profiling.Messages;

// ReSharper disable AccessToDisposedClosure

namespace Picollo.Profiling;

public static class ProfilerClient
{
    internal const string GuidStr = "51C01100-2507-1008-1002-BEBE6E0B6E25";

    private static readonly bool ConsoleLoggingEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    private static void Log(string message)
    {
        if (ConsoleLoggingEnabled)
            Console.WriteLine($"CLIENT: {message}");
    }

    public static ProfilerSession AttachProfiler(int processId = 0,
        ProfilerState onAttachState = ProfilerState.Idle,
        int samplingFrequency = 1000,
        string? baseOutputDir = null,
        string? sessionName = null,
        uint[]? osThreadIdFilter = null,
        string[]? threadNameFilter = null,
        ProfilingFlags profilingFlags = ProfilingFlags.Default,
        OutputFlags outputFlags = OutputFlags.Default,
        DiagnosticsFlags diagnosticsFlags = DiagnosticsFlags.Default,
        CancellationToken cancellationToken = default
    )
    {
        var profilerConfiguration = new ProfilerConfiguration
        {
            OnAttachState = onAttachState,
            SamplingFrequency = Math.Clamp(samplingFrequency, 1, 10000),
            SessionName = sessionName,
            BaseOutputDir = baseOutputDir,
            ProfilingFlags = profilingFlags,
            OutputFlags = outputFlags,
            DiagnosticsFlags = diagnosticsFlags,
            OsThreadIdFilter = osThreadIdFilter,
            ThreadNameFilter = threadNameFilter,
        };

        return AttachProfiler(profilerConfiguration, processId, cancellationToken);
    }

    public static ProfilerSession AttachProfiler(ProfilerConfiguration profilerConfiguration,
        int processId = 0,
        CancellationToken cancellationToken = default
    )
    {
        if (profilerConfiguration.OnAttachState is not (ProfilerState.Idle or ProfilerState.DryRun or ProfilerState.Running))
            throw new ArgumentException($"Bad value for OnAttachState: {profilerConfiguration.OnAttachState:G}");

        if (processId <= 0)
            processId = Environment.ProcessId;

        string? processName = GetProcessName(processId);
        if (string.IsNullOrWhiteSpace(processName))
            throw new ArgumentException($"Cannot find a process by processId={processId}");

        if (string.IsNullOrWhiteSpace(profilerConfiguration.SessionName))
            profilerConfiguration.SessionName =
                processName; // TODO (low) if session name is given, we lose processName, it does not go inside the output, only the file name.

        Log($"Attach requested for process {processId} with initial state {profilerConfiguration.OnAttachState:G}");

        var profilerFilePath = GetProfilerFilePath();
        if (profilerFilePath is null || !File.Exists(profilerFilePath))
            throw new FileNotFoundException($"Cannot find Picollo native profiler {GetProfilerFileName()} in expected locations");

        Log($"Using profiler at {profilerFilePath}");

        var sessionId = Guid.NewGuid().ToString("N");

        var socketPath = PicolloConstants.GetSessionSocketPath(processId);
        Log($"Connecting session {sessionId} through {socketPath}");

        var attachedAtUts = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(profilerConfiguration.BaseOutputDir))
            profilerConfiguration.BaseOutputDir = Path.Combine(PicolloConstants.PicolloHome, "profiler", "sessions");

        Directory.CreateDirectory(profilerConfiguration.BaseOutputDir);

        var configuration = new SessionConfiguration
        {
            SessionId = sessionId,
            AttachedAt = attachedAtUts,
            ProfilerConfiguration = profilerConfiguration
        };

        var sessionDirectoryPath = configuration.GetSessionOutputDir();
        Directory.CreateDirectory(sessionDirectoryPath);

        ProfilerSession? profilerSession = null;

        var outputFilePath = Path.Combine(sessionDirectoryPath, $"sampling-{processName}{PicolloConstants.ProfileOutputExtension}");
        GZipStream? compressor = null;
        var hasOutput = false;

        const int retryLimit = 10;
        var retryCount = 0;
        var attachedDiagnosticClient = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(socketPath))
            {
                try
                {
                    profilerSession = ProfilerSession.Connect(socketPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Log($"Connection attempt failed: {ex.Message}\n{ex.StackTrace}");
                }
            }

            if (profilerSession is not null)
            {
                Log("Connected to profiler session transport");
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!attachedDiagnosticClient)
            {
                Log($"Attaching native profiler to process {processId}");
                var client = new DiagnosticsClient(processId);
                client.AttachProfiler(
                    attachTimeout: TimeSpan.FromSeconds(10),
                    profilerGuid: new(GuidStr),
                    profilerPath: Path.GetFullPath(profilerFilePath));
                attachedDiagnosticClient = true;
                Log("Native profiler attach completed");
            }

            retryCount++;

            cancellationToken.ThrowIfCancellationRequested();

            if (retryCount > retryLimit)
                throw new Exception("Cannot attach profiler");

            Thread.Sleep(100 * retryCount);
        }

        var attached = new TaskCompletionSource();
        Exception? attachError = null;

        void OnAttached(OnAttachedMessage message)
        {
            Log($"OnAttached callback received for session {message.SessionId}");
            if (!string.Equals(message.SessionId, sessionId, StringComparison.Ordinal))
                attachError = new InvalidOperationException("Unexpected profiler handshake");
            attached.SetResult();
        }

        profilerSession.OnAttachedReceived += OnAttached;

        profilerSession.SessionDirectoryPath = sessionDirectoryPath;

        profilerSession.OnInputChunkPayloadReceived += payload =>
        {
            Log($"InputChunk callback received with {payload.Length} payload bytes");
            if (compressor is null)
            {
                compressor = new GZipStream(File.Open(outputFilePath, FileMode.Create), CompressionMode.Compress);
                Log($"Profile output opened at {outputFilePath}");
            }

            foreach (ReadOnlyMemory<byte> block in payload)
                compressor.Write(block.Span);
            compressor.WriteByte((byte)'\n');
            compressor.Flush();

            hasOutput = true;
            profilerSession.OutputFilePath = outputFilePath;
        };

        var callCountersFilePath = Path.Combine(sessionDirectoryPath, "callcounters.ndjson");
        profilerSession.OnHotMethodsPayloadReceived += payload =>
        {
            Log($"CallCounters callback received with {payload.Length} payload bytes");
            File.AppendAllBytes(callCountersFilePath, payload.ToArray());
            File.AppendAllBytes(callCountersFilePath, "\n"u8);
        };

        profilerSession.OnDetachedReceived += _ =>
        {
            Log("OnDetached callback received");
            compressor?.Dispose();

            if (!hasOutput)
            {
                try
                {
                    File.Delete(outputFilePath);
                }
                catch
                {
                    //
                }

                profilerSession.OutputFilePath = null;
            }

            Log("Profile output completed");
        };

        var attachSucceeded = false;

        try
        {
            Log("Starting session processing");
            _ = profilerSession.ProcessSessionAsync();

            Log("Sending session configuration");
            profilerSession.SendConfiguration(configuration);

            Log("Waiting for OnAttached");
            if (!attached.Task.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                throw new TimeoutException("The profiler did not send OnAttached in 10 seconds.");

            if (attachError is not null)
                throw attachError;

            Log($"Session {sessionId} attached");
            attachSucceeded = true;
            return profilerSession;
        }
        finally
        {
            profilerSession.OnAttachedReceived -= OnAttached;

            if (!attachSucceeded)
                profilerSession.Dispose();
        }
    }

    private static string GetProfilerFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Picollo.Profiler.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "Picollo.Profiler.dylib";

        return "Picollo.Profiler.so";
    }

    private static string? GetProfilerFilePath()
    {
        var profilerFileName = GetProfilerFileName();
        var assemblyDirPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, profilerFileName));
        if (File.Exists(assemblyDirPath))
            return assemblyDirPath;

        var currentDirPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, profilerFileName));
        if (!currentDirPath.Equals(assemblyDirPath, StringComparison.Ordinal) && File.Exists(currentDirPath))
            return currentDirPath;

        var binPath = Path.GetFullPath(Path.Combine(PicolloConstants.PicolloHome, "bin", profilerFileName));
        if (File.Exists(binPath))
            return binPath;

        var libPath = Path.GetFullPath(Path.Combine(PicolloConstants.PicolloHome, "lib", profilerFileName));
        if (File.Exists(libPath))
            return libPath;

        return Directory.Exists(PicolloConstants.PicolloHome)
            ? Directory.EnumerateFiles(PicolloConstants.PicolloHome, profilerFileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static string? GetProcessName(int pid)
    {
        try
        {
            string name;
            if (pid == Environment.ProcessId)
                name = Process.GetCurrentProcess().ProcessName;
            else
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
            }

            return SanitizeFileNameFragment(name);
        }
        catch (ArgumentException)
        {
            return null; // process does not exist
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string SanitizeFileNameFragment(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
    }
}
