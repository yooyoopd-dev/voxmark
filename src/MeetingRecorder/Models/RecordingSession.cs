using System.IO;

namespace MeetingRecorder.Models;

/// <summary>Everything about one meeting: the roster, the marks, and where its output files live.</summary>
public sealed class RecordingSession
{
    public required string Title { get; init; }
    public required string SessionFolder { get; init; }
    public required string AudioFileName { get; init; }
    public required List<Speaker> Speakers { get; init; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>Length of the written MP3 in seconds (paused spans excluded — see AudioCaptureService).</summary>
    public double AudioDurationSeconds { get; set; }

    /// <summary>Total wall-clock time spent paused, for the Markdown front-matter only.</summary>
    public double PausedTotalSeconds { get; set; }
    public int PauseCount { get; set; }

    public List<Mark> Marks { get; set; } = new();
    public List<(double Start, double End)> Gaps { get; set; } = new();

    public string Mp3Path => Path.Combine(SessionFolder, AudioFileName);
    public string MarkdownPath => Path.Combine(SessionFolder, Path.ChangeExtension(AudioFileName, ".md"));
}
