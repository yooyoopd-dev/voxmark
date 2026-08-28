namespace MeetingRecorder.Services;

/// <summary>
/// Which of the two editions this exe is — see CLAUDE.md "Editions".
///
/// Both are built from this same source tree; the Lite one is compiled
/// without speech recognition so it stays a ~70 MB download for operators who
/// only ever mark speakers. The two look near enough identical once running
/// that the app has to be able to say which one it is, or a support question
/// about a missing transcription toggle has no answer.
/// </summary>
public static class BuildProfile
{
#if VOXMARK_LITE
    public const bool HasTranscription = false;
    public const string Name = "Lite";
#else
    public const bool HasTranscription = true;
    public const string Name = "Full";
#endif

    /// <summary>What the library screen shows beside the heading.</summary>
    public static string Subtitle => HasTranscription
        ? "stored locally · nothing leaves this machine · Full edition, speech recognition available"
        : "stored locally · nothing leaves this machine · Lite edition, no speech recognition";
}
