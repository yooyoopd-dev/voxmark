using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// A meeting set up ahead of time and saved: title, when it is scheduled,
/// the room, the roster and the recording options.
///
/// This is deliberately more than a <see cref="Preset"/>. A preset is a
/// reusable roster ("the product team"); a plan is one specific meeting the
/// operator prepared earlier and wants to walk into and start.
/// </summary>
public sealed class MeetingPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";
    public string Room { get; set; } = "";

    /// <summary>When the meeting is due to start. Editable on the setup screen.</summary>
    public DateTimeOffset ScheduledAt { get; set; }

    public List<Speaker> Speakers { get; set; } = new();
    public SessionOptions Options { get; set; } = new();

    public DateTimeOffset SavedAt { get; set; }

    [JsonIgnore]
    public string WhenLabel => ScheduledAt.ToString("yyyy-MM-dd HH:mm");

    public MeetingPlan Clone() => new()
    {
        Id = Id,
        Title = Title,
        Room = Room,
        ScheduledAt = ScheduledAt,
        SavedAt = SavedAt,
        Speakers = Speakers.Select(s => s.Clone()).ToList(),
        Options = new SessionOptions
        {
            AllowOverlappingMarks = Options.AllowOverlappingMarks,
            MarkStartOffsetSeconds = Options.MarkStartOffsetSeconds,
            Mp3BitrateKbps = Options.Mp3BitrateKbps,
            SplitMinutes = Options.SplitMinutes,
        },
    };
}
