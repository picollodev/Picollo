using System.Text.Json.Serialization;
using Picollo.Metrics;
using Picollo.Profiling;
using Picollo.Profiling.Messages;

namespace Picollo;

[JsonSerializable(typeof(HdrHistogramSummary))]
[JsonSerializable(typeof(Percentile))]
[JsonSerializable(typeof(Bucket))]
[JsonSerializable(typeof(CallCounters))]
[JsonSerializable(typeof(ThreadCounters))]
[JsonSerializable(typeof(FrameCounters))]
[JsonSerializable(typeof(Metadata))]
[JsonSerializable(typeof(FrameInfo))]
public partial class PicolloJsonContext : JsonSerializerContext
{
}
