using System.Reflection;

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

    /// <summary>
    /// The version this exe was built as — "1.3.0". Comes from the csproj,
    /// which CI overrides with the release tag, so what Settings shows and
    /// what the download was called cannot drift apart.
    ///
    /// Read from the informational version and cut at the first "+": the SDK
    /// appends "+&lt;commit sha&gt;" when it knows one, which is useful in a
    /// crash log and noise in a settings footer. Never throws — a missing
    /// attribute costs a version string, not a screen.
    /// </summary>
    public static string Version
    {
        get
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plus = informational.IndexOf('+');
                    return plus > 0 ? informational[..plus] : informational;
                }

                return assembly.GetName().Version?.ToString(3) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    /// <summary>"VoxMark 1.3.0 · Full edition", or the edition alone if the version is unknown.</summary>
    public static string VersionLine =>
        (Version.Length > 0 ? "VoxMark " + Version + " · " : "VoxMark · ") + Name + " edition";

    /// <summary>What the library screen shows beside the heading.</summary>
    public static string Subtitle => HasTranscription
        ? "stored locally · nothing leaves this machine · Full edition, speech recognition available"
        : "stored locally · nothing leaves this machine · Lite edition, no speech recognition";
}
