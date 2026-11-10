using System.Drawing;

namespace AIQuota;

/// <summary>
/// The colours shared by every renderer - tray icon (<see cref="TrayIconFactory"/>), Stream
/// Dock keys and the tray flyout - so they always agree. Each renderer is handed the active
/// palette instead of reading the preference itself, which keeps rendering easy to test.
///
/// Two families, each in a variant for a dark and a light background:
/// <list type="bullet">
/// <item><see cref="Standard"/>: the usage level is green (≤ 50%), yellow (≤ 80%) or red
/// (above).</item>
/// <item><see cref="Greyscale"/> (accessibility mode, for achromatopsia): no hue at all. The
/// level is shown by brightness - the fill gets more contrast against the track the higher
/// the usage - plus a shape cue above 80% (<see cref="MarksHighLevel"/>): a notch at the fill
/// edge on the tray icon and in the flyout, a hatched fill on the deck keys. Every fill has
/// at least 3:1 contrast against the track, and the track's outline at least 3:1 against
/// the taskbar (WCAG 1.4.11).</item>
/// </list>
/// </summary>
internal sealed record UsagePalette
{
    /// <summary>Percentages above this are "nearly used up" - red, or the greyscale
    /// palette's brightest fill plus its shape cue.</summary>
    public const int HighThreshold = 80;

    /// <summary>Percentages above this (up to <see cref="HighThreshold"/>) are the middle
    /// level.</summary>
    public const int MediumThreshold = 50;

    /// <summary>Drawn for a light taskbar / light theme.</summary>
    public required bool IsLight { get; init; }

    /// <summary>The accessibility palette: no information in hue.</summary>
    public required bool IsGreyscale { get; init; }

    public required Color Low { get; init; }
    public required Color Medium { get; init; }
    public required Color High { get; init; }

    /// <summary>Background (empty part) of a usage bar.</summary>
    public required Color Track { get; init; }

    /// <summary>1px outline around the track, so the bar's extent stays visible where the
    /// track itself has too little contrast against the taskbar; null draws none.</summary>
    public Color? TrackOutline { get; init; }

    /// <summary>The session bar's outline and the countdown ring while no session has
    /// started ("available, but not active").</summary>
    public required Color NotStarted { get; init; }

    /// <summary>The session countdown ring - kept apart from every fill colour, so "time
    /// left" is never mistaken for "quota used".</summary>
    public required Color Ring { get; init; }

    public required Color PaceCore { get; init; }
    public required Color PaceOutline { get; init; }

    /// <summary>Colour of the notch drawn at the fill edge of a bar above <see
    /// cref="HighThreshold"/> (only when <see cref="MarksHighLevel"/>).</summary>
    public required Color HighNotch { get; init; }

    /// <summary>Stripes of the hatched fill above <see cref="HighThreshold"/> on deck keys
    /// (only when <see cref="MarksHighLevel"/>), drawn over <see cref="High"/>.</summary>
    public required Color HighHatch { get; init; }

    /// <summary>Text drawn over the empty track and over the fill (deck keys). The text is
    /// split at the fill edge, so each part keeps at least 4.5:1 contrast (WCAG 1.4.3).</summary>
    public required Color TextOnTrack { get; init; }
    public required Color TextOnFill { get; init; }

    /// <summary>Warning icon (network error): triangle fill, optional outline, and "!".</summary>
    public required Color WarningFill { get; init; }
    public Color? WarningOutline { get; init; }
    public required Color WarningGlyph { get; init; }

    /// <summary>"Update available" dot in the icon's top-right corner.</summary>
    public required Color BadgeFill { get; init; }
    public required Color BadgeBorder { get; init; }

    /// <summary>Whether levels above <see cref="HighThreshold"/> get an extra shape cue,
    /// because brightness alone doesn't separate them clearly enough from the middle level.</summary>
    public bool MarksHighLevel => IsGreyscale;

    public static bool IsHigh(int percent) => percent > HighThreshold;

    public Color ForPercent(int percent) => percent switch
    {
        > HighThreshold => High,
        > MediumThreshold => Medium,
        _ => Low,
    };

    /// <summary>The palette for the given mode and background.</summary>
    public static UsagePalette For(bool greyscale, bool isLight) => (greyscale, isLight) switch
    {
        (false, false) => StandardDark,
        (false, true) => StandardLight,
        (true, false) => GreyscaleDark,
        (true, true) => GreyscaleLight,
    };

    public static UsagePalette Standard(bool isLight) => For(greyscale: false, isLight);
    public static UsagePalette Greyscale(bool isLight) => For(greyscale: true, isLight);

    private static readonly UsagePalette StandardDark = new()
    {
        IsLight = false,
        IsGreyscale = false,
        Low = Color.FromArgb(90, 190, 110),
        Medium = Color.FromArgb(240, 170, 60),
        High = Color.FromArgb(235, 70, 70),
        Track = Color.FromArgb(127, Color.White),
        NotStarted = Color.FromArgb(127, Color.White),
        Ring = Color.FromArgb(235, Color.White),
        PaceCore = Color.FromArgb(235, Color.White),
        PaceOutline = Color.FromArgb(230, 20, 20, 20),
        HighNotch = Color.Black,
        HighHatch = Color.FromArgb(235, 70, 70),
        TextOnTrack = Color.White,
        TextOnFill = Color.White,
        WarningFill = Color.FromArgb(240, 170, 60),
        WarningGlyph = Color.FromArgb(230, 40, 30, 0),
        BadgeFill = Color.FromArgb(255, 255, 205, 0),
        BadgeBorder = Color.FromArgb(220, 40, 40, 40),
    };

    private static readonly UsagePalette StandardLight = StandardDark with
    {
        IsLight = true,
        Track = Color.FromArgb(127, Color.Black),
        NotStarted = Color.FromArgb(127, Color.Black),
        Ring = Color.FromArgb(235, 40, 40, 40),
        PaceCore = Color.FromArgb(235, 40, 40, 40),
        PaceOutline = Color.FromArgb(230, Color.White),
    };

    // Greyscale on a dark taskbar (#202020-ish): fills get brighter with the level; the track
    // is dark (no bright area beyond the fill itself, for light sensitivity) and carries a
    // mid-grey outline for contrast against the taskbar. The ring is a mid grey that differs
    // from the white > 80% fill, so a full bar and the ring don't merge.
    private static readonly UsagePalette GreyscaleDark = new()
    {
        IsLight = false,
        IsGreyscale = true,
        Low = Color.FromArgb(0x9A, 0x9A, 0x9A),
        Medium = Color.FromArgb(0xD0, 0xD0, 0xD0),
        High = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Track = Color.FromArgb(0x3C, 0x3C, 0x3C),
        TrackOutline = Color.FromArgb(0x80, 0x80, 0x80),
        NotStarted = Color.FromArgb(0x80, 0x80, 0x80),
        Ring = Color.FromArgb(0xB0, 0xB0, 0xB0),
        PaceCore = Color.White,
        PaceOutline = Color.Black,
        HighNotch = Color.Black,
        HighHatch = Color.FromArgb(0x9A, 0x9A, 0x9A),
        TextOnTrack = Color.White,
        TextOnFill = Color.Black,
        WarningFill = Color.Black,
        WarningOutline = Color.White,
        WarningGlyph = Color.White,
        BadgeFill = Color.White,
        BadgeBorder = Color.Black,
    };

    // Greyscale on a light taskbar (#F3F3F3-ish): the mirror image - fills get darker with
    // the level, on a light track with a mid-grey outline.
    private static readonly UsagePalette GreyscaleLight = GreyscaleDark with
    {
        IsLight = true,
        Low = Color.FromArgb(0x6A, 0x6A, 0x6A),
        Medium = Color.FromArgb(0x3A, 0x3A, 0x3A),
        High = Color.FromArgb(0x00, 0x00, 0x00),
        Track = Color.FromArgb(0xD8, 0xD8, 0xD8),
        TrackOutline = Color.FromArgb(0x70, 0x70, 0x70),
        NotStarted = Color.FromArgb(0x70, 0x70, 0x70),
        Ring = Color.FromArgb(0x50, 0x50, 0x50),
        PaceCore = Color.Black,
        PaceOutline = Color.White,
        HighNotch = Color.White,
        HighHatch = Color.FromArgb(0x6A, 0x6A, 0x6A),
        TextOnTrack = Color.Black,
        TextOnFill = Color.White,
        BadgeFill = Color.Black,
        BadgeBorder = Color.White,
    };
}
