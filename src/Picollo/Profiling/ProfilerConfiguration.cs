using System.Text.Json.Serialization;

namespace Picollo.Profiling;

public enum ProfilerState
{
    /// <summary>
    /// Profiler is not yet attached or already detached. 
    /// </summary>
    Detached = 0,
    
    /// <summary>
    /// Profiler is ready but does nothing until an explicit start command or a new configuration with different <see cref="ProfilerConfiguration.OnAttachState"/> state. 
    /// </summary>
    Idle = 1,

    /// <summary>
    /// Profiler does sampling and symbol resolution but does not publish results until explicit start request.
    /// </summary>
    DryRun = 2,

    /// <summary>
    /// Profiler does sampling, symbol resolution and publishes results.
    /// </summary>
    Running = 3,
}

public class ProfilerConfiguration
{
    [JsonPropertyName("start_on_attach")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ProfilerState OnAttachState { get; set; } = ProfilerState.Idle;

    [JsonPropertyName("frequency")]
    public int SamplingFrequency { get; set; } = 1000;

    [JsonPropertyName("session_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SessionName { get; set; }
    
    /// <summary>
    /// Optional override of the output destination. The default value is PicolloHome\profiler\sessions.
    /// </summary>
    [JsonPropertyName("base_output_dir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? BaseOutputDir { get; set; }

    [JsonPropertyName("profiling_flags")]
    public ProfilingFlags ProfilingFlags { get; set; } = ProfilingFlags.Default;

    [JsonPropertyName("output_flags")]
    public OutputFlags OutputFlags { get; set; } = OutputFlags.Default;

    [JsonPropertyName("diagnostics_flags")]
    public DiagnosticsFlags DiagnosticsFlags { get; set; } = DiagnosticsFlags.Default;
    
    [JsonPropertyName("os_thread_id_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint[]? OsThreadIdFilter { get; set; }

    [JsonPropertyName("thread_name_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string[]? ThreadNameFilter { get; set; }
}
