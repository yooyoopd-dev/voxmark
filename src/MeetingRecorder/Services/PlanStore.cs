using System.IO;
using System.Text.Json;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Meeting setups prepared ahead of time, saved next to the sessions in
/// <c>plans.json</c>. The library lists them so the operator can walk into a
/// room, pick the meeting they already set up, and be one click from Start.
/// </summary>
public static class PlanStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppPaths.Root, "plans.json");

    public static List<MeetingPlan> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<MeetingPlan>();
            return JsonSerializer.Deserialize<List<MeetingPlan>>(File.ReadAllText(FilePath))
                   ?? new List<MeetingPlan>();
        }
        catch (Exception)
        {
            return new List<MeetingPlan>();
        }
    }

    public static void Save(List<MeetingPlan> plans)
    {
        AppPaths.EnsureCreated();
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(plans, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Add or replace by id, newest scheduled first.</summary>
    public static List<MeetingPlan> Upsert(MeetingPlan plan)
    {
        var plans = Load();
        plans.RemoveAll(p => p.Id == plan.Id);
        plan.SavedAt = DateTimeOffset.Now;
        plans.Add(plan);
        plans = plans.OrderBy(p => p.ScheduledAt).ToList();
        Save(plans);
        return plans;
    }

    public static List<MeetingPlan> Remove(string id)
    {
        var plans = Load();
        plans.RemoveAll(p => p.Id == id);
        Save(plans);
        return plans;
    }
}
