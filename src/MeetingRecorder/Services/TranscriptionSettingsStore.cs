using System.IO;
using System.Text.Json;

namespace MeetingRecorder.Services;

/// <summary>
/// The transcription choices that outlive one meeting: which model file, and
/// whether the toggle starts on. Sits beside <c>presets.json</c> in the app
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

        public string Language { get; set; } = "en";
    }

    private static string Path => System.IO.Path.Combine(AppPaths.Root, "transcription.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
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
