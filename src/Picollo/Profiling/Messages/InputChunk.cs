using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
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

    /// <summary>
    /// An index in <see cref="FrameTypes"/>. Missing or out of range valeus are treated as zero/unknown. 
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Type { get; set; }

    [JsonPropertyName("moduleMvid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid? ModuleMvid { get; set; }

    [JsonPropertyName("methodToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? MethodToken { get; set; }

    [JsonPropertyName("methodMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MethodMetadata? MethodMetadata { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Line { get; set; }

    [JsonPropertyName("col")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Column { get; set; }
}

public enum MetadataFormatting
{
    Full = 0,
    Abbreviated = 1,
    Short = 2,
    Minimal = 3
}

public sealed class TypeMetadata
{
    [JsonPropertyName("namespace")]
    public string Namespace { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonIgnore]
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";

    public string Format(MetadataFormatting formatting)
    {
        switch (formatting)
        {
            case MetadataFormatting.Full:
                return FullName;

            case MetadataFormatting.Short:
            case MetadataFormatting.Minimal:
                return Name;

            case MetadataFormatting.Abbreviated:
                if (string.IsNullOrEmpty(Namespace))
                    return Name;

                var builder = new StringBuilder();
                builder.Append(Namespace[0]);
                for (var i = 1; i < Namespace.Length; i++)
                {
                    if (Namespace[i - 1] == '.')
                        builder.Append(Namespace[i]);
                }

                builder.Append('.');
                builder.Append(Name);
                return builder.ToString();

            default:
                throw new ArgumentOutOfRangeException(nameof(formatting));
        }
    }

    [JsonConstructor]
    public TypeMetadata(string @namespace, string name)
    {
        Namespace = @namespace;
        Name = name;
    }
}

public enum ParameterModifier
{
    None,
    Ref,
    Out,
    In
}

public sealed class ParameterMetadata
{
    [JsonPropertyName("type")]
    public TypeMetadata Type { get; }

    [JsonPropertyName("modifier")]
    public ParameterModifier Modifier { get; }

    public string Format(MetadataFormatting formatting)
    {
        var prefix = Modifier switch
        {
            ParameterModifier.Ref => "ref ",
            ParameterModifier.Out => "out ",
            ParameterModifier.In => "in ",
            _ => ""
        };

        return prefix + Type.Format(formatting);
    }

    [JsonConstructor]
    public ParameterMetadata(TypeMetadata type, ParameterModifier modifier)
    {
        Type = type;
        Modifier = modifier;
    }
}

public sealed class MethodMetadata
{
    [JsonPropertyName("declaringType")]
    public TypeMetadata DeclaringType { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("returnType")]
    public TypeMetadata? ReturnType { get; }

    [JsonPropertyName("parameters")]
    public ImmutableArray<ParameterMetadata> Parameters { get; }

    public string Format(MetadataFormatting formatting)
    {
        var builder = new StringBuilder();

        if (formatting != MetadataFormatting.Minimal)
        {
            builder.Append(DeclaringType.Format(formatting));
            builder.Append('.');
        }

        builder.Append(Name);
        builder.Append('(');

        for (var i = 0; i < Parameters.Length; i++)
        {
            if (i != 0)
                builder.Append(", ");

            builder.Append(Parameters[i].Format(formatting));
        }

        builder.Append(')');

        if (formatting == MetadataFormatting.Full && ReturnType is not null)
        {
            builder.Append(" : ");
            builder.Append(ReturnType.Format(formatting));
        }

        return builder.ToString();
    }

    [JsonConstructor]
    public MethodMetadata(
        TypeMetadata declaringType,
        string name,
        TypeMetadata? returnType,
        ImmutableArray<ParameterMetadata> parameters)
    {
        DeclaringType = declaringType;
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
    }
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
