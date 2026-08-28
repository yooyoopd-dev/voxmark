using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// One MP3 file of a session. A meeting is a single part unless the operator
/// asked for a split, in which case the recorder rolls to a new file every
/// N minutes.
///
/// <see cref="StartSeconds"/> and <see cref="EndSeconds"/> are session file
/// time — measured from the start of part 1, not from the start of this
/// file — because that is the timebase every mark is recorded against.
/// </summary>
public sealed class AudioPart
{
    /// <summary>1-based, and the number that appears in the file name.</summary>
    public int Index { get; set; }

    public string FileName { get; set; } = "";

    /// <summary>Where this file begins on the session-wide timeline.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Where this file ends on the session-wide timeline.</summary>
    public double EndSeconds { get; set; }

    [JsonIgnore] public double DurationSeconds => EndSeconds - StartSeconds;

    /// <summary>True when the range overlaps this part at all.</summary>
    public bool Covers(double start, double end) => end > StartSeconds && start < EndSeconds;
}
