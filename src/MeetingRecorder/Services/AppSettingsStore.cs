using System.IO;
using System.Text.Json;

namespace MeetingRecorder.Services;

/// <summary>
/// App-level preferences: the things an operator sets once on this PC
/// rather than before every meeting — where session folders are created,
/// and the recording defaults a new setup starts from. Deliberately stored under
/// <c>%LocalAppData%\VoxMark\</c> rather than beside <c>presets.json</c> and
/// the other <c>Documents\VoxMark\</c> files: LocalAppData is never
/// cloud-redirected the way Documents can be, so the one setting that exists
/// to work around a broken Documents\VoxMark\ can't itself depend on it.
/// </summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class Settings
    {
        /// <summary>
        /// Absolute path to create session folders under, overriding
        /// Documents\VoxMark\Sessions. Empty means "use the default".
        /// </summary>
        public string SessionsRoot { get; set; } = "";

        /// <summary>
        /// The mark-start offset a new setup begins with. It calibrates how
        /// long this operator takes to react, not what kind of meeting this
        /// is, so it belongs to the PC. Defaults match
        /// <see cref="MeetingRecorder.Models.SessionOptions"/> so a settings.json written
        /// before these keys existed round-trips unchanged.
        /// </summary>
        public double MarkStartOffsetSeconds { get; set; } = 0.8;

        /// <summary>The MP3 bitrate a new setup begins with.</summary>
        public int Mp3BitrateKbps { get; set; } = 128;
    }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxMark", "settings.json");

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
            // A corrupt or unreadable settings file just means the default applies.
        }

        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            AppPaths.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception)
        {
            // Failing to remember the choice must not block starting a meeting.
        }
    }
}
