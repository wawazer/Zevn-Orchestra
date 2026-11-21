using System.Linq;

namespace ZevnShell;

/// <summary>Tema terminal — sama persis dengan TerminalThemes.swift.</summary>
internal static class TerminalThemes
{
    public sealed record Def(string Id, string Name, string Bg, string Fg, string Accent, string[] Ansi)
    {
        public bool IsLight => Id is "paper" or "sand";
    }

    public static readonly Def[] All =
    [
        new("zevn", "Zevn Night", "#0B0E13", "#E6EDF3", "#3FD0B4",
            ["#1B212A", "#F0616D", "#8BD450", "#F2C55A", "#5B9CF5", "#B48CF2", "#3FD0B4", "#C9D1D9",
             "#4B5563", "#FF7B86", "#A6E36E", "#FFD77A", "#7DB3FF", "#CBA6FF", "#6EE7CF", "#FFFFFF"]),
        new("ocean", "Deep Ocean", "#0A1628", "#D6E4F5", "#5B9CF5",
            ["#14243B", "#F07178", "#9ADE7B", "#FFCB6B", "#5B9CF5", "#C792EA", "#4FD6E8", "#C3D3E8",
             "#3B4E6B", "#FF8B92", "#B5F09A", "#FFDB8E", "#82B6FF", "#DDB3FF", "#7DE8F5", "#FFFFFF"]),
        new("violet", "Violet Dusk", "#14101F", "#E9E2F7", "#9D7CF2",
            ["#221B33", "#FF6B8B", "#7EE0A2", "#F5D07A", "#7A9CFF", "#C49BFF", "#6FD9E0", "#D9D0EC",
             "#4A4063", "#FF8FA8", "#9FF0BC", "#FFE19C", "#9DB6FF", "#DCBDFF", "#93EBF0", "#FFFFFF"]),
        new("forest", "Forest Mist", "#0E1612", "#DCEADF", "#7FD18B",
            ["#1A2620", "#E5786D", "#7FD18B", "#E3C76A", "#6FA8DC", "#C59AD9", "#6CCFBF", "#C8D8CC",
             "#3E5247", "#F2958B", "#A0E3A9", "#F0DA8E", "#93C1E8", "#D8B6E6", "#8FE0D3", "#FFFFFF"]),
        new("ember", "Ember", "#17110E", "#F2E6DC", "#F2A65A",
            ["#271D18", "#F0616D", "#B6D46B", "#F2A65A", "#7BA7E0", "#D88AC0", "#68C6B8", "#E0D2C5",
             "#544036", "#FF8590", "#CBE68C", "#FFC07E", "#9CC0F0", "#EBA6D4", "#8ADBCE", "#FFFFFF"]),
        new("paper", "Paper Light", "#F7F6F2", "#24262B", "#0F9D84",
            ["#24262B", "#C8323F", "#3C8A2E", "#A86E00", "#2462C4", "#8A3FC0", "#0F8C80", "#C7C4BC",
             "#6B6E75", "#E04452", "#4FA63F", "#C48612", "#3A7BE0", "#A257D9", "#18A898", "#F7F6F2"]),
        new("sand", "Sand", "#F3EDE2", "#3A3226", "#C46F2E",
            ["#3A3226", "#B83A3A", "#5E8A2E", "#B07A12", "#3567A8", "#8E4C9E", "#2F8A86", "#CFC6B6",
             "#7A6F5E", "#D14F4F", "#73A33C", "#CC921F", "#4A80C4", "#A865B8", "#3BA39E", "#F3EDE2"]),
    ];

    /// <summary>Tanpa pilihan tema: ikut mode app (gelap → Zevn Night, terang → Paper Light).</summary>
    public static Def Get(string? id) =>
        All.FirstOrDefault(t => t.Id == id) ?? (App.Model.IsDark ? All[0] : All.First(t => t.Id == "paper"));

    /// <summary>Objek tema untuk xterm.js.</summary>
    public static object ToXterm(Def d)
    {
        string[] names = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];
        var t = new System.Collections.Generic.Dictionary<string, string>
        {
            ["background"] = d.Bg, ["foreground"] = d.Fg, ["cursor"] = d.Accent, ["cursorAccent"] = d.Bg,
            ["selectionBackground"] = d.Accent + "59",
        };
        for (int i = 0; i < 8; i++)
        {
            t[names[i]] = d.Ansi[i];
            t["bright" + char.ToUpperInvariant(names[i][0]) + names[i][1..]] = d.Ansi[i + 8];
        }
        return t;
    }
}
