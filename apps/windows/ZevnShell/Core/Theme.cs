using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace ZevnShell;

/// <summary>
/// Token warna & tipografi Zevn Shell.
/// Palet "graphite": hampir hitam, panel rata, garis rambut tipis, satu aksen hangat.
/// Semua halaman mengambil warna dari sini supaya tema bisa diganti di satu tempat.
/// </summary>
internal static class Theme
{
    public static bool Dark => App.Model.IsDark;

    public static Color Hex(string? hex, byte alpha = 255)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) || s.Length != 6) v = 0x08DB89;
        return Color.FromArgb(alpha, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static SolidColorBrush Brush(string hex, double opacity = 1) => new(Hex(hex, (byte)Math.Round(255 * opacity)));
    public static SolidColorBrush Brush(Color c, double opacity = 1) => new(Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B));

    // MARK: Aksen & status

    public const string Accent = "#08DB89";
    public const string Accent2 = "#7AA2F7";
    public const string Accent3 = "#B18CF7";
    public const string Green = "#2DD4BF";
    public const string Orange = "#F5A524";
    public const string Red = "#F0616D";

    // MARK: Permukaan

    /// <summary>Warna teks/ikon dengan opasitas, di atas permukaan apa pun.</summary>
    public static SolidColorBrush Ink(double opacity) => new(Dark ? Color.FromArgb((byte)(255 * opacity), 255, 255, 255) : Color.FromArgb((byte)(255 * opacity), 0, 0, 0));

    public static SolidColorBrush Bg => Brush(Dark ? "#08090A" : "#F5F6F7");
    /// <summary>Kanvas di belakang panel mengambang (sidebar, konten, inspector).</summary>
    public static SolidColorBrush Canvas => Brush(Dark ? "#040506" : "#E8EAED");
    public static SolidColorBrush Panel => Brush(Dark ? "#0C0E10" : "#FFFFFF");
    /// <summary>Garis tepi panel mengambang.</summary>
    public static SolidColorBrush PanelEdge => Brush(Dark ? "#1D2025" : "#E0E2E6");
    public static SolidColorBrush Card => Brush(Dark ? "#14161A" : "#FFFFFF");
    public static SolidColorBrush CardHover => Brush(Dark ? "#1A1D22" : "#FAFAFB");
    public static SolidColorBrush CardStroke => Brush(Dark ? "#23262B" : "#E5E6E9");
    public static SolidColorBrush Field => Brush(Dark ? "#0A0B0D" : "#F2F3F5");
    public static SolidColorBrush Hairline => Brush(Dark ? "#1C1F23" : "#E9EAEC");
    public static SolidColorBrush Text => Brush(Dark ? "#EDEEF1" : "#17181A");
    public static SolidColorBrush Text2 => Brush(Dark ? "#9CA1AA" : "#63666E");
    public static SolidColorBrush Text3 => Brush(Dark ? "#6B7079" : "#9A9DA5");
    public static SolidColorBrush AccentBrush => Brush(Accent);

    // MARK: Gradien & ikon

    public static LinearGradientBrush Gradient(params string[] hexes)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i < hexes.Length; i++)
            g.GradientStops.Add(new GradientStop { Color = Hex(hexes[i]), Offset = hexes.Length == 1 ? 0 : (double)i / (hexes.Length - 1) });
        return g;
    }

    public static SolidColorBrush BrandBrush => new(Hex(Accent));

    /// <summary>Warna solid dari hex, untuk ikon host & menu.</summary>
    public static SolidColorBrush TintGradient(string hex) => new(Hex(hex));

    public static readonly string[] HostColors = ["#08DB89", "#7AA2F7", "#B18CF7", "#F5A524", "#F0616D", "#4ADE80", "#E879C6", "#94A3B8"];
    public static readonly string[] CardPalette = ["#08DB89", "#4ADE80", "#A3E635", "#F5A524", "#F2A65A", "#FB923C", "#F0616D", "#E11D48",
                                                   "#E879C6", "#C084FC", "#B18CF7", "#6366F1", "#7AA2F7", "#06B6D4", "#14B8A6", "#94A3B8"];

    public const string Mono = "Cascadia Mono, Consolas";
    public static FontFamily MonoFont => new(Mono);
}

/// <summary>Glyph Segoe Fluent Icons yang dipakai di app.</summary>
internal static class G
{
    public const string Server = "", Key = "", Forward = "", Folder = "", Database = "",
        Code = "", Fingerprint = "", History = "", Heart = "", Info = "", Person = "",
        Lock = "", Search = "", Add = "", Edit = "", Delete = "", Copy = "",
        Refresh = "", Play = "", Bolt = "", Eye = "", EyeOff = "", Grid = "",
        List = "", More = "", Close = "", Upload = "", Download = "", NewFolder = "",
        Up = "", Home = "", Sync = "", Cloud = "", Globe = "", Back = "",
        Next = "", Save = "", Terminal = "", Table = "", Check = "", Warning = "",
        Sparkle = "", Sun = "", Moon = "", Contrast = "", Apps = "", Document = "",
        Link = "", Bug = "", Chat = "", Hello = "", SignOut = "", Tag = "",
        Text = "", Clock = "", Filter = "", Export = "", Structure = "", First = "",
        Prev = "", NextPage = "", Last = "", ChevronDown = "", ChevronRight = "",
        Shield = "", Rename = "", Permissions = "", Stop = "", Link2 = "";
}
