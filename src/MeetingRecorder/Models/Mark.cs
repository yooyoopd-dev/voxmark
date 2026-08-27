using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// One speaker turn: <c>[Start, End)</c> in seconds measured against the
/// audio file's sample count, not the wall clock — design guide section 11,
/// so a paused meeting can never desynchronise the marks from the MP3.
/// </summary>
public sealed class Mark
{
    /// <summary>Stable within a session; the journal and the undo stack address marks by this.</summary>
    public long Id { get; set; }

    public int SpeakerSlot { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }

    /// <summary>
    /// The operator's actual key press, before the −0.8 s offset was applied.
    /// Section 07: "the raw press time is kept in the log so the offset can
    /// be re-tuned later."
    /// </summary>
    public double RawPressSeconds { get; set; }

    /// <summary>Set when this mark was still open at Stop and closed automatically.</summary>
    public bool AutoClosed { get; set; }

    [JsonIgnore]
    public double DurationSeconds => EndSeconds - StartSeconds;

    public Mark Clone() => new()
    {
        Id = Id, SpeakerSlot = SpeakerSlot, StartSeconds = StartSeconds, EndSeconds = EndSeconds,
        RawPressSeconds = RawPressSeconds, AutoClosed = AutoClosed,
    };
}
