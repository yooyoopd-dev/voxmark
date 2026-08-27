using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// One roster entry.
///
/// <see cref="SlotIndex"/> is assigned by registration order and never
/// re-shuffled (design guide section 02) — it fixes the palette colour and
/// the <c>Sn</c> id written into the Markdown. Section 10 is explicit that
/// slots are never renumbered to close a gap: an absent speaker keeps their
/// slot, so a roster can legitimately jump from S4 to S6.
/// </summary>
public sealed class Speaker
{
    public int SlotIndex { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";

    /// <summary>
    /// Marked absent on the setup screen: the tile stays dimmed, the key
    /// stays reserved, and the speaker is left out of the Markdown roster.
    /// </summary>
    public bool IsAbsent { get; set; }

    public int KeyDigit { get; set; }
    public bool KeyShift { get; set; }

    /// <summary>Markdown speaker id ("S1", "S2", …) — slot-stable within a session.</summary>
    [JsonIgnore]
    public string Id => "S" + (SlotIndex + 1).ToString();

    /// <summary>Convenience view over the two stored fields; the fields are what persists.</summary>
    [JsonIgnore]
    public MarkKey Key
    {
        get => new(KeyDigit, KeyShift);
        set { KeyDigit = value.Digit; KeyShift = value.Shift; }
    }

    [JsonIgnore]
    public string KeyLabel => Key.Label;

    /// <summary>Single letter used for the marker flag over the waveform (section 01).</summary>
    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    public Speaker Clone() => new()
    {
        SlotIndex = SlotIndex, Name = Name, Role = Role,
        IsAbsent = IsAbsent, KeyDigit = KeyDigit, KeyShift = KeyShift,
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Role) ? Name : Name + " — " + Role;
}
