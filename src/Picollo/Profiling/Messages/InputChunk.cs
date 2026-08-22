using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Picollo.Profiling.Messages;

internal sealed class InputChunk : IClientMessage<InputChunk>
{
    public static ClientMessageType MessageType => ClientMessageType.InputChunk;

    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://picollo.dev/picolloscope/input-format-schema.json";

    [JsonPropertyName("metadata")]
    public Metadata Metadata { get; set; } = new();

    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = [];

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Name { get; set; }

    [JsonPropertyName("activeProfileIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? ActiveProfileIndex { get; set; }
}

public sealed class Metadata
{
    [JsonPropertyName("frames")]
    public List<FrameInfo> Frames { get; set; } = [];

    [JsonPropertyName("frameTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string>? FrameTypes { get; set; }
}

public struct FrameInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? File { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Type { get; set; }

    [JsonPropertyName("moduleMvid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid? ModuleMvid { get; set; }

    [JsonPropertyName("methodToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? MethodToken { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Line { get; set; }

    [JsonPropertyName("col")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Column { get; set; }
}

internal static class ProfileTypes
{
    public const string Sampled = "sampled";
    public const string Evented = "evented";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EventedProfile), ProfileTypes.Evented)]
[JsonDerivedType(typeof(SampledProfile), ProfileTypes.Sampled)]
internal abstract class Profile
{
    /// <summary>
    /// Name of the profile. Typically, a thread name. Use for matching a base profile if the later is present.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// OS Thread ID, used to identify a profile with an import file and across its chunks.
    /// </summary>
    [JsonPropertyName("tid")]
    public string Tid { get; set; } = "";

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Unit { get; set; }

    [JsonPropertyName("startValue")]
    public double StartValue { get; set; }

    [JsonPropertyName("endValue")]
    public double EndValue { get; set; }
}

internal sealed class EventedProfile : Profile
{
    [JsonPropertyName("events")]
    public List<ProfileEvent> Events { get; set; } = [];
}

internal sealed class FlatSamples
{
    [JsonPropertyName("stacks")]
    public List<int> Stacks { get; set; } = [];

    [JsonPropertyName("ends")]
    public List<int> Ends { get; set; } = [];
    
    public void Add(params ReadOnlySpan<int> stack)
    {
        Stacks.AddRange(stack);
        Ends.Add(Stacks.Count);
    }

    public void Clear()
    {
        Stacks.Clear();
        Ends.Clear();
    }
}

internal sealed class SampledProfile : Profile
{
    [JsonPropertyName("samples")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<List<int>>? Samples { get; set; }

    [JsonPropertyName("flatSamples")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlatSamples? FlatSamples { get; set; }

    [JsonPropertyName("weights")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<double>? Weights { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenFrameEvent), "O")]
[JsonDerivedType(typeof(CloseFrameEvent), "C")]
internal abstract class ProfileEvent
{
    [JsonPropertyName("at")]
    public double At { get; set; }

    [JsonPropertyName("frame")]
    public int Frame { get; set; }
}

internal sealed class OpenFrameEvent : ProfileEvent
{
}

internal sealed class CloseFrameEvent : ProfileEvent
{
}
