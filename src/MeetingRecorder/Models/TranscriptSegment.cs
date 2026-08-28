namespace MeetingRecorder.Models;

/// <summary>
/// One recognised span of speech. Times are offsets into the session's audio
/// on exactly the same timebase as a <see cref="Mark"/> — derived from the
/// sample count written to the MP3, not the wall clock — which is what lets
/// the exporter line the two up without any correction step.
///
/// Deliberately says nothing about *who* spoke: attribution is the operator's
/// job, and joining the two is <see cref="Services.TranscriptMapper"/>'s.
/// </summary>
public sealed class TranscriptSegment
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Whisper's own confidence, 0..1. Carried so a low-quality run can be spotted later.</summary>
    public double Probability { get; set; }

    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);

    /// <summary>Seconds this segment and the given span have in common. 0 when they do not touch.</summary>
    public double OverlapWith(double start, double end) =>
        Math.Max(0, Math.Min(EndSeconds, end) - Math.Max(StartSeconds, start));

    public TranscriptSegment Clone() => new()
    {
        StartSeconds = StartSeconds,
        EndSeconds = EndSeconds,
        Text = Text,
        Probability = Probability,
    };
}
