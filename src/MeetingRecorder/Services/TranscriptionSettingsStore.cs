using System.IO;
using System.Text.Json;

namespace MeetingRecorder.Services;

/// <summary>
/// The transcription choices that outlive one meeting: which model file, which
/// language, where the CUDA libraries are, which engine and how hard to
/// search, the vocabulary to expect, and whether the toggle starts on. Sits beside <c>presets.json</c> in the app
/// folder for the same reason presets do — picking a model once is setup, and
/// re-picking it before every meeting would be a chore, not a decision.
///
/// Compiled into both editions so the file round-trips; Lite simply never
/// reads it.
/// </summary>
public static class TranscriptionSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class Settings
    {
        /// <summary>Absolute path to a local ggml model, or empty to auto-detect in the Models folder.</summary>
        public string ModelPath { get; set; } = "";

        /// <summary>Whether the setup screen's toggle starts on.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Folder holding NVIDIA's CUDA 12 libraries, or empty for the
        /// default <c>Documents\VoxMark\cuda</c>. Settable because those
        /// three files come to about 700 MB and a machine whose C: drive is
        /// tight has every reason to keep them on another one — the same
        /// argument that made the save location settable.
        /// </summary>
        public string CudaPath { get; set; } = "";

        /// <summary>
        /// Whisper's language code. English unless the operator chose
        /// otherwise in Settings — an empty or missing value reads as English
        /// too (see <see cref="Load"/>), never as auto-detect.
        /// </summary>
        public string Language { get; set; } = DefaultLanguage;

        /// <summary>
        /// Names, products and jargon the recogniser should expect, one per
        /// line or comma-separated. Fed to whisper as its initial prompt, which
        /// is the one lever that teaches it a word it keeps getting wrong
        /// without a different model: a prompt mentioning "VoxMark" makes the
        /// decoder prefer that spelling over "Fox Mark" every time it hears
        /// it. Machine-wide, like the model — the people and the products in
        /// a company's meetings are mostly the same from one to the next.
        ///
        /// This is the <b>English</b> list; the name predates the split by
        /// language, and renaming it would orphan what is already saved.
        /// <see cref="TranscriptDictionary"/> decides which lists a meeting gets.
        /// </summary>
        public string Vocabulary { get; set; } = "";

        /// <summary>The Korean vocabulary, used when a meeting is recognised as Korean.</summary>
        public string VocabularyKo { get; set; } = "";

        /// <summary>
        /// English corrections, one <c>heard =&gt; correct</c> per line, applied
        /// to recognised text after whisper has produced it. Where the
        /// vocabulary only biases the decoder, these are rules — the fix for a
        /// word that comes back wrong the same way however it is prompted.
        /// </summary>
        public string Corrections { get; set; } = "";

        /// <summary>The Korean corrections.</summary>
        public string CorrectionsKo { get; set; } = "";

        /// <summary>
        /// "auto" (default), "fast" or "best". Decides the decoder's search:
        /// beam search finds noticeably better transcripts than greedy
        /// decoding at roughly twice the compute. Auto spends that on a GPU
        /// and saves it on a CPU, where the live strip is already behind.
        /// </summary>
        public string Accuracy { get; set; } = "auto";

        /// <summary>
        /// "auto" (default), "cuda", "vulkan" or "cpu": which whisper.cpp
        /// engine to try first. Auto is CUDA, then Vulkan, then the CPU. The
        /// explicit choices exist for the machine whose GPU driver misbehaves
        /// under one of them — a forced CPU is slow but never wrong.
        /// </summary>
        public string Engine { get; set; } = "auto";
    }

    /// <summary>The language every meeting starts in until Settings says otherwise.</summary>
    public const string DefaultLanguage = "en";

    /// <summary>
    /// A language code as the operator reads it, for Setup, the recording
    /// screen and Settings. A code this list does not carry is shown as
    /// itself rather than as "English": whisper accepts more languages than
    /// the eight Settings offers, and mislabelling one would be wrong.
    /// </summary>
    public static string LanguageName(string? code) => (code ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "en" => "English",
        "auto" => "Auto-detect",
        "ko" => "Korean",
        "ja" => "Japanese",
        "zh" => "Chinese",
        "es" => "Spanish",
        "fr" => "French",
        "de" => "German",
        var other => other,
    };

    private static string Path => System.IO.Path.Combine(AppPaths.Root, "transcription.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
                if (string.IsNullOrWhiteSpace(settings.Language)) settings.Language = DefaultLanguage;
                return settings;
            }
        }
        catch (Exception)
        {
            // A corrupt settings file costs a re-pick, nothing more.
        }

        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception)
        {
            // Failing to remember the choice must not block starting a meeting.
        }
    }
}
