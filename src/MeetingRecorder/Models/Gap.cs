using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>A range with no speaker marked — the "## Gaps" table in section 10.</summary>
public sealed class Gap
{
    public double Start { get; set; }
    public double End { get; set; }
    [JsonIgnore]
    public double Duration => End - Start;
}
