using System.Text.Json.Serialization;

namespace MeetingRecorder.Models;

/// <summary>
/// A marking key as the design guide's keyboard map defines it (section 09):
/// <c>1…9, 0</c> for slots 1–10 and <c>Shift+1 … Shift+6</c> for slots 11–16
/// (the guide stopped at twelve; the roster was raised to sixteen). Shift is
/// only a prefix for those six — Shift+7…0 are unbound, not aliases.
///
/// The key is a property of the <see cref="Speaker"/>, not of the slot: the
/// guide's "keys live on the preset" rule means a roster row carries its own
/// key cell and the operator can reassign it, while the slot (and therefore
/// the colour and the <c>Sn</c> id in the Markdown) never moves.
/// </summary>
public readonly record struct MarkKey(int Digit, bool Shift)
{
    /// <summary>The key as printed on a tile: "1" … "9", "0", "⇧1" … "⇧6".</summary>
    [JsonIgnore]
    public string Label => Shift ? "⇧" + Digit.ToString() : Digit.ToString();

    /// <summary>The matching global hotkey, section 09: Alt+1…0 / Alt+Shift+1…6.</summary>
    [JsonIgnore]
    public string GlobalLabel => Shift ? "Alt+Shift+" + Digit.ToString() : "Alt+" + Digit.ToString();

    [JsonIgnore]
    public bool IsValid => Digit is >= 0 and <= 9 && (!Shift || Digit is >= 1 and <= MaxShiftDigit);

    /// <summary>The highest digit Shift may prefix: slots 11–16 are ⇧1–⇧6.</summary>
    public const int MaxShiftDigit = 6;

    /// <summary>The default key for a slot, before the operator reassigns anything.</summary>
    public static MarkKey ForSlot(int slotIndex) => slotIndex switch
    {
        >= 0 and <= 8 => new MarkKey(slotIndex + 1, false),
        9 => new MarkKey(0, false),
        >= 10 and <= 15 => new MarkKey(slotIndex - 9, true),
        _ => new MarkKey(0, false),
    };
}
