using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Picollo.Profiling.Messages;

internal interface IMessage
{
}

internal interface IMessage<T> : IMessage where T : IMessage<T>
{
    private static JsonTypeInfo<T> JsonTypeInfo =>
        (JsonTypeInfo<T>)(MessagesJsonContext.Default.GetTypeInfo(typeof(T)) ??
                          throw new InvalidOperationException($"No JSON metadata generated for {typeof(T)}."));

    public static virtual void Write(IBufferWriter<byte> writer, in T message)
    {
        using var jsonWriter = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(jsonWriter, message, JsonTypeInfo);
    }

    static virtual T Read(in ReadOnlySequence<byte> payload)
    {
        var reader = new Utf8JsonReader(payload);

        return JsonSerializer.Deserialize(ref reader, JsonTypeInfo)
               ?? throw new JsonException($"Cannot deserialize {typeof(T)}.");
    }
}