using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// A marking key as the design guide's keyboard map defines it (section 09):
/// <c>1…9, 0</c> for slots 1–10 and <c>Shift+1 / Shift+2</c> for slots 11–12.
/// Shift is only a prefix for those two — Shift+3…0 are unbound, not aliases.
///
/// The key is a property of the <see cref="Speaker"/>, not of the slot: the
/// guide's "keys live on the preset" rule means a roster row carries its own
/// key cell and the operator can reassign it, while the slot (and therefore
/// the colour and the <c>Sn</c> id in the Markdown) never moves.
/// </summary>
public readonly record struct MarkKey(int Digit, bool Shift)
{
    /// <summary>The key as printed on a tile: "1" … "9", "0", "⇧1", "⇧2".</summary>
    [JsonIgnore]
    public string Label => Shift ? "⇧" + Digit.ToString() : Digit.ToString();

    /// <summary>The matching global hotkey, section 09: Alt+1…0 / Alt+Shift+1,2.</summary>
    [JsonIgnore]
    public string GlobalLabel => Shift ? "Alt+Shift+" + Digit.ToString() : "Alt+" + Digit.ToString();

    [JsonIgnore]
    public bool IsValid => Digit is >= 0 and <= 9 && (!Shift || Digit is 1 or 2);

    /// <summary>The default key for a slot, before the operator reassigns anything.</summary>
    public static MarkKey ForSlot(int slotIndex) => slotIndex switch
    {
        >= 0 and <= 8 => new MarkKey(slotIndex + 1, false),
        9 => new MarkKey(0, false),
        10 => new MarkKey(1, true),
        11 => new MarkKey(2, true),
        _ => new MarkKey(0, false),
    };
}
