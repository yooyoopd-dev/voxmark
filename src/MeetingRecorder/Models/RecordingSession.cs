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

    /// <summary>The first (or only) MP3. Kept for the library and the summary screen.</summary>
    public string AudioFileName { get; set; } = "";

    /// <summary>File name stem without the extension — parts append "_partNN" to it.</summary>
    public string AudioBaseName { get; set; } = "";

    /// <summary>
    /// Every MP3 this session wrote, in order. One entry unless the operator
    /// asked to split the recording; each entry carries where it sits on the
    /// session-wide timeline.
    /// </summary>
    public List<AudioPart> AudioParts { get; set; } = new();

    public List<Speaker> Speakers { get; set; } = new();
    public SessionOptions Options { get; set; } = new();

    public string InputDeviceName { get; set; } = "";

    /// <summary>Written verbatim into the Markdown's <c>audio_format</c> field.</summary>
    public string AudioFormatDescription { get; set; } = "mp3 / 128 kbps / 44100 Hz / mono";

    /// <summary>
    /// When the meeting was scheduled for, as typed on the setup screen. It
    /// names the session folder and lets a plan be prepared in advance; the
    /// Markdown's <c>date</c> stays <see cref="StartedAt"/>, the moment
    /// recording actually began.
    /// </summary>
    public DateTimeOffset ScheduledAt { get; set; }

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

    /// <summary>
    /// What speech recognition ran, verbatim for the Markdown's
    /// <c>transcription</c> field — "small.en / en / CUDA". Empty when none
    /// ran, which is what makes the whole field disappear from the output.
    /// </summary>
    public string TranscriptionDescription { get; set; } = "";

    /// <summary>Seconds of audio the transcriber could not keep up with. Surfaced, never swallowed.</summary>
    public double TranscriptionDroppedSeconds { get; set; }

    /// <summary>
    /// Recognised speech, chronological. Deliberately *not* in session.json:
    /// an hour of dialogue would swamp a file that exists to be a summary.
    /// It lives in transcript.jsonl and <see cref="Services.SessionStore"/>
    /// rehydrates it on load, which is also what makes an interrupted
    /// meeting recoverable with its words rather than just its timings.
    /// </summary>
    [JsonIgnore] public List<TranscriptSegment> Transcript { get; set; } = new();

    /// <summary>False until the export pass has written the Markdown; drives the library's recovery row.</summary>
    public bool Completed { get; set; }

    [JsonIgnore] public string Mp3Path => Path.Combine(SessionFolder, AudioFileName);
    [JsonIgnore] public string MarkdownPath => Path.Combine(SessionFolder, Path.ChangeExtension(AudioFileName, ".md"));
    [JsonIgnore] public string SessionJsonPath => Path.Combine(SessionFolder, "session.json");
    [JsonIgnore] public string JournalPath => Path.Combine(SessionFolder, "marks.jsonl");
    [JsonIgnore] public string TranscriptPath => Path.Combine(SessionFolder, "transcript.jsonl");

    /// <summary>Speakers that appear in the Markdown roster — absent ones keep their slot but are left out.</summary>
    [JsonIgnore] public IEnumerable<Speaker> PresentSpeakers => Speakers.Where(s => !s.IsAbsent);

    /// <summary>True when the recording was rolled into more than one MP3.</summary>
    [JsonIgnore] public bool IsSplit => AudioParts.Count > 1;

    public Speaker? SpeakerForSlot(int slot) => Speakers.FirstOrDefault(s => s.SlotIndex == slot);

    /// <summary>Absolute path of one part's MP3.</summary>
    public string PathOf(AudioPart part) => Path.Combine(SessionFolder, part.FileName);

    /// <summary>The Markdown that goes with one part — same stem, ".md".</summary>
    public string MarkdownPathOf(AudioPart part) =>
        Path.Combine(SessionFolder, Path.ChangeExtension(part.FileName, ".md"));

    /// <summary>
    /// The parts as they should be treated for export. A session recorded
    /// before parts existed, or recovered from an older folder, still has to
    /// produce exactly one file, so it gets a synthetic single part.
    /// </summary>
    public List<AudioPart> EffectiveParts()
    {
        if (AudioParts.Count > 0) return AudioParts;
        return new List<AudioPart>
        {
            new()
            {
                Index = 1,
                FileName = AudioFileName,
                StartSeconds = 0,
                EndSeconds = AudioDurationSeconds,
            },
        };
    }
}
