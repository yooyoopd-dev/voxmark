using System.Windows.Media;

namespace MeetingRecorder.Theme;

/// <summary>
/// The design guide's colour tokens (section 02) for the parts of the UI
/// that are built in code rather than XAML — tiles, the waveform, the block
/// lane. Same values as <c>Tokens.xaml</c>; keep the two in step.
/// </summary>
public static class Palette
{
    /// <summary>
    /// Twelve fixed speaker slots, chosen to be told apart rather than to sit
    /// evenly on a ring.
    ///
    /// The previous set was one OKLCH ring at a single lightness, 30° apart.
    /// Even spacing reads well as a swatch strip and badly as twelve tiles in
    /// a dim room: neighbouring hues differ on one axis only, so slots 08–10
    /// were three greens and 01/11 were two blues. This set varies hue,
    /// chroma <em>and</em> lightness together, and includes the two families
    /// a categorical palette needs to reach twelve — a desaturated tan and a
    /// near-neutral grey — so no two slots differ by hue alone.
    ///
    /// Slot 01 stays in the blue family it was asked to be, but readable:
    /// <c>#1428A0</c> measured 1.45:1 against the well behind it, which on
    /// this dark theme is a block you cannot find. Every slot here is at
    /// least 5:1, and none is above 9:1, so no colour shouts over the rest.
    ///
    /// Colour is still a secondary cue — the slot number and the title remain
    /// the primary identifiers, because twelve of anything is more than a
    /// glance can resolve.
    /// </summary>
    public static readonly Color[] Speakers =
    {
        Rgb(0x5A8DFF), // 01 blue
        Rgb(0xB98CFF), // 02 lavender
        Rgb(0xED72E0), // 03 orchid
        Rgb(0xFF7A94), // 04 rose
        Rgb(0xFF9455), // 05 orange
        Rgb(0xE0B93C), // 06 gold
        Rgb(0xA7CC3D), // 07 lime
        Rgb(0x4FB85E), // 08 green
        Rgb(0x2FD1A0), // 09 emerald
        Rgb(0x38C3E8), // 10 cyan
        Rgb(0xB58463), // 11 tan
        Rgb(0xA8A3B5), // 12 grey
    };

    public static readonly Color Void = Rgb(0x0E1019);
    public static readonly Color Window = Rgb(0x161826);
    public static readonly Color Chrome = Rgb(0x1A1C29);
    public static readonly Color Well = Rgb(0x1C1E2C);
    public static readonly Color Surface = Rgb(0x232532);
    public static readonly Color SurfaceAccent = Rgb(0x2B2741);
    public static readonly Color Hairline = Rgb(0x3F424D);
    public static readonly Color HairlineSoft = Rgb(0x2C2F3D);
    public static readonly Color Accent = Rgb(0x9184D9);
    public static readonly Color AccentDim = Rgb(0x5D5294);
    public static readonly Color AccentEdge = Rgb(0x423A6A);
    public static readonly Color AccentText = Rgb(0xB5ABFC);
    public static readonly Color AccentTextStrong = Rgb(0xD2CEFD);
    public static readonly Color Rec = Rgb(0xE58095);
    public static readonly Color RecEdge = Rgb(0x5D3038);
    public static readonly Color RecText = Rgb(0xF7D5DC);
    public static readonly Color Text = Rgb(0xE9E9ED);
    public static readonly Color TextSecondary = Rgb(0xCFD3E5);
    public static readonly Color TextBody = Rgb(0xB2B6CA);
    public static readonly Color TextDim = Rgb(0x9397AB);
    public static readonly Color TextMuted = Rgb(0x75798C);
    public static readonly Color TextFaint = Rgb(0x595D6C);
    public static readonly Color Good = Rgb(0x4EC39B);
    public static readonly Color Warn = Rgb(0xC9A04F);

    public static readonly SolidColorBrush VoidBrush = Frozen(Void);
    public static readonly SolidColorBrush WindowBrush = Frozen(Window);
    public static readonly SolidColorBrush ChromeBrush = Frozen(Chrome);
    public static readonly SolidColorBrush WellBrush = Frozen(Well);
    public static readonly SolidColorBrush SurfaceBrush = Frozen(Surface);
    public static readonly SolidColorBrush HairlineBrush = Frozen(Hairline);
    public static readonly SolidColorBrush HairlineSoftBrush = Frozen(HairlineSoft);
    public static readonly SolidColorBrush AccentBrush = Frozen(Accent);
    public static readonly SolidColorBrush AccentEdgeBrush = Frozen(AccentEdge);
    public static readonly SolidColorBrush AccentTextBrush = Frozen(AccentText);
    public static readonly SolidColorBrush AccentTextStrongBrush = Frozen(AccentTextStrong);
    public static readonly SolidColorBrush RecBrush = Frozen(Rec);
    public static readonly SolidColorBrush RecEdgeBrush = Frozen(RecEdge);
    public static readonly SolidColorBrush RecTextBrush = Frozen(RecText);
    public static readonly SolidColorBrush TextBrush = Frozen(Text);
    public static readonly SolidColorBrush TextSecondaryBrush = Frozen(TextSecondary);
    public static readonly SolidColorBrush TextBodyBrush = Frozen(TextBody);
    public static readonly SolidColorBrush TextDimBrush = Frozen(TextDim);
    public static readonly SolidColorBrush TextMutedBrush = Frozen(TextMuted);
    public static readonly SolidColorBrush TextFaintBrush = Frozen(TextFaint);
    public static readonly SolidColorBrush GoodBrush = Frozen(Good);
    public static readonly SolidColorBrush WarnBrush = Frozen(Warn);
    public static readonly SolidColorBrush TransparentBrush = Frozen(Colors.Transparent);

    public static Color ForSlot(int slotIndex) =>
        Speakers[((slotIndex % Speakers.Length) + Speakers.Length) % Speakers.Length];

    public static SolidColorBrush BrushForSlot(int slotIndex) => Frozen(ForSlot(slotIndex));

    /// <summary>The same hue at a lower alpha, for a tile's active fill.</summary>
    public static Color Tint(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255.0, 0, 255), color.R, color.G, color.B);

    public static SolidColorBrush TintBrush(Color color, double alpha) => Frozen(Tint(color, alpha));

    private static Color Rgb(int hex) =>
        Color.FromRgb((byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
