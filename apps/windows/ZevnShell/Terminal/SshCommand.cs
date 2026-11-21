using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>
/// Susun argumen OpenSSH bawaan Windows dari data host (padanan SSHCommand di versi macOS).
/// Password / passphrase dikirim lewat helper askpass (ZevnShell.exe sendiri), bukan argumen.
/// </summary>
internal sealed class SshCommand
{
    public List<string> Args { get; } = [];
    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> TempFiles { get; } = [];

    public static string SshExe
    {
        get
        {
            var p = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
            return File.Exists(p) ? p : "ssh.exe";
        }
    }

    public static bool SshAvailable => File.Exists(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"));

    public SshCommand(Host host, SshKey? key, Host? jump, IEnumerable<string>? extra = null)
    {
        if (extra != null) Args.AddRange(extra);
        Args.AddRange(["-p", host.Port.ToString(), "-o", "ServerAliveInterval=30",
            // Terima host baru otomatis (TOFU), tolak jika fingerprint berubah.
            "-o", "StrictHostKeyChecking=accept-new"]);
#if DEBUG
        if (Environment.GetEnvironmentVariable("ZEVN_TEST_KNOWN_HOSTS") is { Length: > 0 } kh) Args.AddRange(["-o", $"UserKnownHostsFile={kh}"]);
#endif
        if (!string.IsNullOrEmpty(host.Username)) Args.AddRange(["-l", host.Username]);
        if (host.AgentForwarding) Args.Add("-A");
        if (jump != null) Args.AddRange(["-J", $"{jump.Username}@{jump.Hostname}:{jump.Port}"]);
        // Proxy SOCKS5/HTTP: ZevnShell.exe sendiri jadi ProxyCommand (lihat Program.cs).
        // Login proxy lewat environment, bukan argumen.
        var proxyEnv = new Dictionary<string, string>();
        if (jump == null && host.Proxy is { } p && !string.IsNullOrWhiteSpace(p.Host) && Environment.ProcessPath is { } self)
        {
            var kind = p.Kind == ProxyKind.Http ? "http" : "socks5";
            Args.AddRange(["-o", $"ProxyCommand=\"{self}\" --zevn-proxy {kind} {p.Host} {p.Port} %h %p"]);
            if (!string.IsNullOrEmpty(p.Username)) proxyEnv["ZEVN_PROXY_USER"] = p.Username;
            if (!string.IsNullOrEmpty(p.Password)) proxyEnv["ZEVN_PROXY_PASS"] = p.Password;
        }

        string? secret = null;
        switch (host.Auth)
        {
            case AuthMethod.Password:
                secret = host.Password;
                Args.AddRange(["-o", "PreferredAuthentications=password,keyboard-interactive,publickey"]);
                break;
            case AuthMethod.Key:
                if (key != null && WriteKey(key) is { } file)
                {
                    TempFiles.Add(file);
                    Args.AddRange(["-i", file, "-o", "IdentitiesOnly=yes"]);
                    secret = key.Passphrase;
                }
                break;
        }
        foreach (var e in host.Env.Where(e => !string.IsNullOrEmpty(e.Name)))
            Args.AddRange(["-o", $"SetEnv={e.Name}={e.Value}"]);
        Args.Add(host.Hostname);

        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) Env[(string)e.Key] = (string?)e.Value ?? "";
        Env["TERM"] = "xterm-256color";
        Env["COLORTERM"] = "truecolor";
        Env["LANG"] = host.Charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ? "en_US.UTF-8" : "C";
        foreach (var (k, v) in proxyEnv) Env[k] = v;
        if (!string.IsNullOrEmpty(secret) && Environment.ProcessPath is { } exe)
        {
            Env["SSH_ASKPASS"] = exe;
            Env["SSH_ASKPASS_REQUIRE"] = "force";
            Env["DISPLAY"] = ":0";
            Env["ZEVN_ASKPASS"] = "1";
            Env["ZEVN_SECRET"] = secret;
        }
    }

    /// <summary>Baris perintah lengkap (dengan kutip ala Windows).</summary>
    public string CommandLine => Quote(SshExe) + " " + string.Join(" ", Args.Select(Quote));

    public void Cleanup()
    {
        foreach (var f in TempFiles) try { File.Delete(f); } catch { }
        TempFiles.Clear();
    }

    /// <summary>Kutip satu argumen sesuai aturan CommandLineToArgvW.</summary>
    public static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (var ch in a)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { sb.Append('\\', slashes * 2 + 1); slashes = 0; sb.Append('"'); continue; }
            sb.Append('\\', slashes); slashes = 0; sb.Append(ch);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Tulis private key ke file sementara yang hanya bisa dibaca user ini (syarat OpenSSH Windows).</summary>
    private static string? WriteKey(SshKey key)
    {
        try
        {
            var dir = Path.Combine(Config.LocalDir, "session-keys");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            var pem = key.PrivateKey.EndsWith('\n') ? key.PrivateKey : key.PrivateKey + "\n";
            // File dibuat langsung dengan izin khusus user ini (tanpa warisan), syarat OpenSSH Windows
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            var me = WindowsIdentity.GetCurrent().User!;
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
            using (var fs = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, sec))
            {
                var bytes = new UTF8Encoding(false).GetBytes(pem.Replace("\r\n", "\n"));
                fs.Write(bytes, 0, bytes.Length);
            }
            return path;
        }
        catch { return null; }
    }

    /// <summary>Bersihkan sisa key sementara dari sesi sebelumnya (mis. app ditutup paksa).</summary>
    public static void CleanupStaleKeys()
    {
        try
        {
            var dir = Path.Combine(Config.LocalDir, "session-keys");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir)) try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
