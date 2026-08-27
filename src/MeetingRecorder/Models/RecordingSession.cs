using System.IO;
using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// Everything about one meeting. Serialised to <c>session.json</c> beside the
/// audio (section 10, "Session folder"), which is also what lets the library
/// list a session and offer to finish an interrupted one.
/// </summary>
public sealed class RecordingSession
{
    public string Title { get; set; } = "";
    public string Room { get; set; } = "";
    public string SessionFolder { get; set; } = "";
    public string AudioFileName { get; set; } = "";

    public List<Speaker> Speakers { get; set; } = new();
    public SessionOptions Options { get; set; } = new();

    public string InputDeviceName { get; set; } = "";

    /// <summary>Written verbatim into the Markdown's <c>audio_format</c> field.</summary>
    public string AudioFormatDescription { get; set; } = "mp3 / 128 kbps / 44100 Hz / mono";

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>Length of the written MP3 (paused spans are absent from the file, so from it too).</summary>
    public double AudioDurationSeconds { get; set; }

    /// <summary>Wall-clock time spent paused. Front-matter only — never a timestamp base.</summary>
    public double PausedTotalSeconds { get; set; }
    public int PauseCount { get; set; }

    /// <summary>Capture buffers the encoder could not accept. Surfaced in the header, not swallowed.</summary>
    public int DroppedBufferCount { get; set; }

    public List<Mark> Marks { get; set; } = new();
    public List<Gap> Gaps { get; set; } = new();

    /// <summary>Extra lines appended to the Markdown's "## Notes" — device fallbacks, mostly.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>False until the export pass has written the Markdown; drives the library's recovery row.</summary>
    public bool Completed { get; set; }

    [JsonIgnore] public string Mp3Path => Path.Combine(SessionFolder, AudioFileName);
    [JsonIgnore] public string MarkdownPath => Path.Combine(SessionFolder, Path.ChangeExtension(AudioFileName, ".md"));
    [JsonIgnore] public string SessionJsonPath => Path.Combine(SessionFolder, "session.json");
    [JsonIgnore] public string JournalPath => Path.Combine(SessionFolder, "marks.jsonl");

    /// <summary>Speakers that appear in the Markdown roster — absent ones keep their slot but are left out.</summary>
    [JsonIgnore] public IEnumerable<Speaker> PresentSpeakers => Speakers.Where(s => !s.IsAbsent);

    public Speaker? SpeakerForSlot(int slot) => Speakers.FirstOrDefault(s => s.SlotIndex == slot);
}
