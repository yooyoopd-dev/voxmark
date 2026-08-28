using System.IO;

namespace MeetingRecorder.Services;

/// <summary>
/// Where everything lives. Offline only — one local folder tree, no accounts,
/// no sync (design guide, "Offline only").
/// </summary>
public static class AppPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoxMark");

    /// <summary>
    /// Where session folders are created. Defaults to Documents\VoxMark\
    /// Sessions; settable from Setup ("Save recordings to") when that
    /// default can't be used — most often a OneDrive-redirected Documents
    /// folder that intermittently fails to create directories — or simply
    /// preferred elsewhere. Read fresh from <see cref="AppSettingsStore"/>
    /// every time rather than cached, so a change made in Setup is visible
    /// to the very next caller with no extra wiring.
    /// </summary>
    public static string SessionsRoot
    {
        get
        {
            var custom = AppSettingsStore.Load().SessionsRoot;
            return string.IsNullOrWhiteSpace(custom) ? Path.Combine(Root, "Sessions") : custom;
        }
    }

    public static string PresetsFile => Path.Combine(Root, "presets.json");

    public static void EnsureCreated() => CreateDirectory(SessionsRoot);

    /// <summary>
    /// Directory.CreateDirectory, retried past a real Windows/OneDrive
    /// quirk: when an ancestor folder is a not-yet-hydrated cloud
    /// placeholder, the Win32 call can throw FileNotFoundException
    /// ("Could not find file '&lt;path&gt;'") instead of succeeding or
    /// throwing DirectoryNotFoundException — even though a directory, not a
    /// file, was being created. A few hundred ms almost always lets OneDrive
    /// finish materializing the folder, so retrying past that instant
    /// resolves it; if it still fails after four attempts, throw with the
    /// full path and the last exception attached so the caller can show a
    /// real diagnostic rather than a bare, misleading "file not found".
    /// </summary>
    public static void CreateDirectory(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.CreateDirectory(path);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(150 * (attempt + 1));
            }
        }

        throw new IOException(
            "Could not create \"" + path + "\" after 4 attempts. " +
            last!.GetType().Name + ": " + last.Message, last);
    }

    /// <summary>A one-line hint when the path is inside OneDrive; "" otherwise.</summary>
    public static string OneDriveHint(string path) =>
        path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)
            ? "This folder is inside OneDrive. If OneDrive is paused, signed out, or offline, " +
              "folder creation can fail like this — open OneDrive and let it finish syncing, " +
              "or pick a different save location under \"Save recordings to\" above."
            : "";

    /// <summary>"Weekly Product Review" → "weekly-product-review".</summary>
    public static string Slugify(string title)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length > 0 ? slug : "meeting";
    }
}
