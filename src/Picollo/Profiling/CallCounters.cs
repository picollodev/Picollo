using System.Collections.Generic;
using System.Text.Json.Serialization;
using Picollo.Profiling.Messages;

namespace Picollo.Profiling;

public class CallCounters
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Name { get; set; }
    
    [JsonPropertyName("metadata")]
    public Metadata Metadata { get; set; } = new();
    
    [JsonPropertyName("threadCounters")]
    public List<ThreadCounters> ThreadMethodCounters { get; set; } = [];
}

public sealed class ThreadCounters
{
    // Name/UniqueId are the same as on InputChunk
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; set; } = "";
    
    [JsonPropertyName("frameCounters")]
    public List<FrameCounters> FrameCounters { get; set; } = [];
}

public struct FrameCounters
{
    [JsonPropertyName("frameIndex")]
    public int FrameIndex { get; set; }

    [JsonPropertyName("total")]
    public double Total { get; set; }

    [JsonPropertyName("own")]
    public double Own { get; set; }

    [JsonPropertyName("ownPlus")]
    public double OwnPlus { get; set; }
}
