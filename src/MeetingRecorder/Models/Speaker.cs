namespace MeetingRecorder.Models;

/// <summary>
/// One roster entry. <see cref="SlotIndex"/> — and therefore the palette
/// colour, marking key, and Markdown speaker id — is only meaningful once a
/// session starts. It's assigned by roster order at that point (see
/// <c>SetupWindow.OnStartRecording</c>), not while the operator is still
/// adding or removing people on the setup screen.
/// </summary>
public sealed class Speaker
{
    public int SlotIndex { get; set; }
    public required string Name { get; set; }
    public string Role { get; set; } = "";

    /// <summary>Markdown speaker id ("S1", "S2", ...) — slot-stable within a session.</summary>
    public string Id => $"S{SlotIndex + 1}";

    /// <summary>Marking key shown in the UI: 1-9, 0, then Shift+1/Shift+2 for slots 11-12.</summary>
    public string KeyLabel => SlotIndex switch
    {
        <= 8 => (SlotIndex + 1).ToString(),
        9 => "0",
        10 => "⇧1",
        11 => "⇧2",
        _ => "?",
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Role) ? Name : $"{Name} — {Role}";
}
