using System.Text.Json.Serialization;
using Picollo.Metrics;

namespace Picollo;

[JsonSerializable(typeof(HdrHistogramSummary))]
[JsonSerializable(typeof(Percentile))]
[JsonSerializable(typeof(Bucket))]
public partial class PicolloJsonContext : JsonSerializerContext
{
}
