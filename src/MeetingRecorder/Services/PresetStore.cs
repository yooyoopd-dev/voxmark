using System.IO;
using System.Text.Json;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Named rosters, saved next to the sessions. Section 06: a preset stores
/// names, roles, slot colours and key assignments together — the slot colour
/// is positional, so the row order carries it.
/// </summary>
public static class PresetStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<Preset> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.PresetsFile)) return new List<Preset>();
            return JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(AppPaths.PresetsFile))
                   ?? new List<Preset>();
        }
        catch (Exception)
        {
            return new List<Preset>();
        }
    }

    public static void Save(List<Preset> presets)
    {
        AppPaths.EnsureRoot();
        var temp = AppPaths.PresetsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(presets, Json));
        File.Move(temp, AppPaths.PresetsFile, overwrite: true);
    }

    /// <summary>Add or replace by name, keeping the list stable for the chip row.</summary>
    public static List<Preset> Upsert(string name, IEnumerable<Speaker> speakers)
    {
        var presets = Load();
        presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        presets.Add(new Preset { Name = name, Speakers = speakers.Select(s => s.Clone()).ToList() });
        Save(presets);
        return presets;
    }

    public static List<Preset> Remove(string name)
    {
        var presets = Load();
        presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Save(presets);
        return presets;
    }
}
