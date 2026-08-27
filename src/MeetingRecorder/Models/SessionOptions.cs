namespace MeetingRecorder.Models;

/// <summary>The "Recording options" block on the setup screen, design guide section 07.</summary>
public sealed class SessionOptions
{
    /// <summary>
    /// Off by default: opening B always closes A. On, both stay open and the
    /// block lane splits into two rows (section 09).
    /// </summary>
    public bool AllowOverlappingMarks { get; set; }

    /// <summary>
    /// A human presses the key after the speaker has already begun, so every
    /// mark start is shifted back by this much automatically (section 07).
    /// </summary>
    public double MarkStartOffsetSeconds { get; set; } = 0.8;

    public int Mp3BitrateKbps { get; set; } = 128;
}
