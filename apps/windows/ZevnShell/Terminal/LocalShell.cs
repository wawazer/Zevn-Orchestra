using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Shell lokal yang bisa dibuka sebagai tab terminal (padanan terminal lokal di versi macOS).</summary>
internal sealed record LocalShell(string Id, string Name, string Path, string Args = "")
{
    public string CommandLine => SshCommand.Quote(Path) + (Args.Length > 0 ? " " + Args : "");

    /// <summary>Shell yang terpasang di PC ini, urutan pertama = bawaan.</summary>
    public static IReadOnlyList<LocalShell> Available
    {
        get
        {
            var sys = Environment.SystemDirectory;
            var list = new List<LocalShell>();
            var pwsh = new[]
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7-preview", "pwsh.exe"),
            }.FirstOrDefault(File.Exists);
            if (pwsh != null) list.Add(new("pwsh", "PowerShell 7", pwsh, "-NoLogo"));
            var winPs = System.IO.Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(winPs)) list.Add(new("powershell", "Windows PowerShell", winPs, "-NoLogo"));
            list.Add(new("cmd", "Command Prompt", System.IO.Path.Combine(sys, "cmd.exe")));
            var wsl = System.IO.Path.Combine(sys, "wsl.exe");
            if (File.Exists(wsl)) list.Add(new("wsl", "WSL", wsl, "~"));
            return list;
        }
    }

    public static LocalShell Default
    {
        get
        {
            var all = Available;
            return all.FirstOrDefault(s => s.Id == Settings.Str(Settings.LocalShell)) ?? all[0];
        }
    }

    /// <summary>"Host" semu untuk tab terminal lokal (tidak disimpan di vault).</summary>
    public Host AsHost() => HostExt.Blank() with
    {
        Id = "local", Label = Name, Hostname = "localhost", Username = Environment.UserName, Color = "#94A3B8",
    };
}
