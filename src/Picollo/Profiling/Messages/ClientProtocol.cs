using System;
using System.IO;
using System.Text.Json.Serialization;

namespace Picollo.Profiling.Messages;

internal class SessionConfiguration : IClientMessage<SessionConfiguration>
{
    public static ClientMessageType MessageType => ClientMessageType.SessionConfiguration;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = null!;

    [JsonPropertyName("attached_at")]
    public DateTimeOffset AttachedAt { get; set; }

    [JsonPropertyName("profiler_configuration")]
    public required ProfilerConfiguration ProfilerConfiguration { get; set; }

    public string GetSessionOutputDir() => Path.Combine(ProfilerConfiguration.BaseOutputDir!, $"{AttachedAt:yyMMdd-HHmmss}-{SessionId.Substring(0, 7)}");
}

internal sealed class StartMessage : IClientMessage<StartMessage>
{
    public static ClientMessageType MessageType => ClientMessageType.Start;

    // TODO When receiving this while running, publish everything and start a new chunk
    [JsonPropertyName("segment_name")]
    public string? SegmentName { get; set; }
}

internal sealed class StopMessage : IClientMessage<StopMessage>
{
    public static ClientMessageType MessageType => ClientMessageType.Stop;

    /// <summary>
    /// When set to true, <see cref="ProfilerState"/> is moved to dry-run, making this command effectively a pause with a fast next start. 
    /// </summary>
    [JsonPropertyName("dryrun")]
    public bool DryRun { get; set; }
}

/// <summary>
/// AOTed .NET library cannot be unloaded, so detach turns a profile into an idle component.
/// Mostly threads will be reported. CorProfiler is used for managed symbols resolution.
/// </summary>
internal sealed class DetachMessage : IClientMessage<DetachMessage>
{
    public static ClientMessageType MessageType => ClientMessageType.Detach;
    
    public int ExpectedCompletionMilliseconds { get; set; } = 1000;
}

internal sealed class OnDetachedMessage : IClientMessage<OnDetachedMessage>
{
    public static ClientMessageType MessageType => ClientMessageType.OnDetached;
}

internal sealed class OnAttachedMessage : IClientMessage<OnAttachedMessage>
{
    public static ClientMessageType MessageType => ClientMessageType.OnAttached;

    public string SessionId { get; set; } = null!;
}

internal sealed class CallCountersMessage : CallCounters, IClientMessage<CallCountersMessage>
{
    public static ClientMessageType MessageType => ClientMessageType.CallCounters;
}
