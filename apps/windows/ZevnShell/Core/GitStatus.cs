using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZevnShell;

/// <summary>Status Git tipis dari folder lokal yang akan di-deploy.</summary>
internal sealed class GitInfo
{
    public string Dir { get; init; } = "";
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? Subject { get; init; }
    public int Changed { get; init; }
    public string? Error { get; init; }

    public bool IsRepo => Branch != null || Commit != null;
    public bool Clean => Changed == 0;

    /// <summary>Branch + hash pendek, atau hash saja untuk HEAD lepas.</summary>
    public string Head => string.IsNullOrWhiteSpace(Branch) ? Commit ?? "—" : $"{Branch} · {Commit ?? "—"}";
}

/// <summary>
/// Membaca branch, commit terakhir, dan jumlah berkas berubah dari folder lokal
/// memakai `git.exe`. Folder yang bukan repositori dikembalikan sebagai `Error`,
/// bukan exception, karena deploy tetap boleh berjalan tanpanya.
/// </summary>
internal static class GitStatus
{
    /// <summary>Baca status folder `dir`. Tidak pernah melempar.</summary>
    public static async Task<GitInfo> ReadAsync(string dir)
    {
        // Satu proses: header branch + daftar berkas berubah.
        var status = await RunAsync(dir, "--no-optional-locks", "status", "--porcelain=v1", "--branch");
        if (status == null) return new GitInfo { Dir = dir, Error = "Git belum terpasang di PATH." };
        if (status.Value.Code != 0)
            return new GitInfo { Dir = dir, Error = FailureText(status.Value.Err) };

        string? branch = null;
        var changed = 0;
        foreach (var line in Lines(status.Value.Out))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) branch = ParseBranch(line[3..]);
            else changed++;
        }

        string? commit = null;
        string? subject = null;
        var log = await RunAsync(dir, "--no-optional-locks", "log", "-1", "--format=%h%x09%s");
        if (log is { Code: 0 } l)
        {
            var parts = l.Out.TrimEnd().Split('\t', 2);
            commit = parts[0].Trim();
            if (commit.Length == 0) commit = null;
            if (parts.Length > 1) subject = parts[1].Trim();
        }

        return new GitInfo { Dir = dir, Branch = branch, Commit = commit, Subject = subject, Changed = changed };
    }

    /// <summary>Tanya konfirmasi bila working tree kotor. `true` = lanjut deploy.</summary>
    public static async Task<bool> ConfirmDirtyAsync(GitInfo? git)
    {
        if (git is not { IsRepo: true, Changed: > 0 }) return true;
        return await Ui.Confirm("Working tree kotor",
            $"{git.Changed} berkas di folder lokal belum di-commit (branch {git.Branch}). "
            + "Deploy mengunggah isi folder apa adanya, jadi perubahan itu ikut terkirim. Lanjut deploy?",
            "Tetap deploy", destructive: false);
    }

    private static string ParseBranch(string head)
    {
        var name = head.Split("...", 2)[0].Trim();
        const string empty = "No commits yet on ";
        if (name.StartsWith(empty, StringComparison.Ordinal)) name = name[empty.Length..];
        if (name.StartsWith("HEAD (no branch)", StringComparison.Ordinal)) return "HEAD terlepas";
        return name;
    }

    private static string FailureText(string err)
    {
        if (err.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
            return "Git menolak folder ini karena pemiliknya berbeda (safe.directory).";
        return "Folder lokal bukan repositori Git.";
    }

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<(int Code, string Out, string Err)?> RunAsync(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (1, "", "Git tidak merespons (timeout).");
            }
            return (p.ExitCode, await output, await error);
        }
        catch
        {
            return null; // git.exe tidak ada
        }
    }
}
