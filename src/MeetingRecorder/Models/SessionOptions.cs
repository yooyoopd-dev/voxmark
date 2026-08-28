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

    /// <summary>
    /// 0 (the default) records the whole meeting into one MP3. Any other
    /// value rolls to a new MP3 every that many minutes, and splits the
    /// Markdown to match, so a long meeting arrives as chunks small enough to
    /// hand to a tool with an upload or context limit.
    ///
    /// Timestamps do not restart per file — they keep counting from the start
    /// of part 1 — so a mark's time means the same thing in every chunk.
    /// </summary>
    public int SplitMinutes { get; set; }

    /// <summary>
    /// Run speech recognition alongside the recording, so the exported
    /// Markdown carries the words as well as the timings.
    ///
    /// Off by default, and off in the Lite edition regardless: transcription
    /// is a passive tap on the audio that must never be able to affect
    /// capture, and the safest version of that is not starting it unasked.
    /// </summary>
    public bool TranscriptionEnabled { get; set; }

    /// <summary>
    /// Absolute path to a local whisper ggml model file. Nothing is ever
    /// downloaded — the operator supplies this file — so an empty value
    /// means "look in Documents\VoxMark\Models\", not "fetch one".
    /// </summary>
    public string WhisperModelPath { get; set; } = "";

    /// <summary>
    /// Whisper language code, or "auto" to let it detect. Defaults to English
    /// because the models worth running on a laptop GPU are the *.en ones,
    /// which only speak English and are markedly better at it.
    /// </summary>
    public string TranscriptionLanguage { get; set; } = "en";
}
