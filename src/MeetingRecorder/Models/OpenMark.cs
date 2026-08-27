namespace MeetingRecorder.Models;

/// <summary>A mark that has been opened and not yet closed.</summary>
public sealed class OpenMark
{
    public int SpeakerSlot { get; set; }
    public double StartSeconds { get; set; }
    public double RawPressSeconds { get; set; }

    public OpenMark Clone() => new()
    {
        SpeakerSlot = SpeakerSlot, StartSeconds = StartSeconds, RawPressSeconds = RawPressSeconds,
    };
}
