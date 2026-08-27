namespace MeetingRecorder.Models;

/// <summary>
/// One closed speaker turn: [Start, End) in seconds, measured against the
/// audio file (sample count), not the wall clock — see design guide
/// section 11, "Non-negotiable behaviour".
/// </summary>
public sealed class Mark
{
    public required int SpeakerSlot { get; init; }
    public required double StartSeconds { get; set; }
    public required double EndSeconds { get; set; }

    /// <summary>Set when this mark was still open at Stop and closed automatically.</summary>
    public bool AutoClosed { get; set; }

    public double DurationSeconds => EndSeconds - StartSeconds;
}
