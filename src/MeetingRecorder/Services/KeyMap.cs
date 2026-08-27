using System.Windows.Input;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// The keyboard map from design guide section 09.
///
/// <c>1 … 9, 0</c> toggle speakers 1–10. <c>Shift+1</c> and <c>Shift+2</c>
/// are speakers 11 and 12 — and Shift is <i>only</i> a prefix for those two:
/// Shift+3…0 are unbound, not aliases, so a stray Shift can never fire the
/// wrong speaker.
/// </summary>
public static class KeyMap
{
    /// <summary>Translate a key press into a marking key, or null if it is unbound.</summary>
    public static MarkKey? ToMarkKey(Key key, bool shift)
    {
        var digit = ToDigit(key);
        if (digit is not int value) return null;

        if (!shift) return new MarkKey(value, false);
        return value is 1 or 2 ? new MarkKey(value, true) : null;
    }

    private static int? ToDigit(Key key) => key switch
    {
        Key.D0 or Key.NumPad0 => 0,
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D8 or Key.NumPad8 => 8,
        Key.D9 or Key.NumPad9 => 9,
        _ => null,
    };
}
