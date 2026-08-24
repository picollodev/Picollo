using System.Text.Json.Serialization;

namespace Picollo.Profiling.Messages;

[JsonSerializable(typeof(InputChunk))]
[JsonSerializable(typeof(Metadata))]
[JsonSerializable(typeof(FrameInfo))]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(SampledProfile))]
[JsonSerializable(typeof(EventedProfile))]
[JsonSerializable(typeof(FlatSamples))]
[JsonSerializable(typeof(ProfileEvent))]
[JsonSerializable(typeof(OpenFrameEvent))]
[JsonSerializable(typeof(CloseFrameEvent))]
[JsonSerializable(typeof(SessionConfiguration))]
[JsonSerializable(typeof(ProfilerConfiguration))]
[JsonSerializable(typeof(StartMessage))]
[JsonSerializable(typeof(StopMessage))]
[JsonSerializable(typeof(DetachMessage))]
[JsonSerializable(typeof(OnDetachedMessage))]
[JsonSerializable(typeof(OnAttachedMessage))]
[JsonSerializable(typeof(CallCountersMessage))]
[JsonSerializable(typeof(CallCounters))]
[JsonSerializable(typeof(ThreadCounters))]
[JsonSerializable(typeof(FrameCounters))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
internal partial class MessagesJsonContext : JsonSerializerContext
{
}
