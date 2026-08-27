using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// A saved roster. Section 06: "a preset stores names, roles, slot colours
/// and key assignments together" — slot colour is positional, so storing the
/// rows in order is enough to reproduce it.
/// </summary>
public sealed class Preset
{
    public string Name { get; set; } = "";
    public List<Speaker> Speakers { get; set; } = new();

    [JsonIgnore]
    public string ChipLabel => Name + " · " + Speakers.Count.ToString();
}
