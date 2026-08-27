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

    public static string SessionsRoot => Path.Combine(Root, "Sessions");

    public static string PresetsFile => Path.Combine(Root, "presets.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(SessionsRoot);
    }

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
