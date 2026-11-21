using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using uniffi.zevn_core;

namespace ZevnShell;

internal static class Config
{
    /// <summary>OAuth Client ID Google (sama dengan versi macOS).</summary>
    public const string GoogleClientID = "311371837872-fj25ea2esclbv4kt79beo4edjsutaig2.apps.googleusercontent.com";
    public const string Website = "https://zevn.web.id/";
    public const string GitHub = "https://github.com/wawazer/zevnshell";
    public const string Issues = "https://github.com/wawazer/zevnshell/issues";
    public const string Releases = "https://github.com/wawazer/zevnshell/releases";
    public const string Privacy = "https://zevn.web.id/privacy.html";
    public const string Terms = "https://zevn.web.id/terms.html";

    /// <summary>Data tersinkron (vault) di %APPDATA%\Zevn Shell.</summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zevn Shell");
#if DEBUG
            if (Environment.GetEnvironmentVariable("ZEVN_DATA_DIR") is { Length: > 0 } test) dir = test;
#endif
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Data khusus perangkat ini (pengaturan, riwayat, key sementara) di %LOCALAPPDATA%\Zevn Shell.</summary>
    public static string LocalDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zevn Shell");
#if DEBUG
            if (Environment.GetEnvironmentVariable("ZEVN_DATA_DIR") is { Length: > 0 } test) dir = Path.Combine(test, "local");
#endif
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string VaultPath => Path.Combine(DataDir, "vault.zevn");

    public static string Version => typeof(Config).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary>Pengaturan lokal (pengganti UserDefaults/AppStorage).</summary>
internal static class Settings
{
    private static Dictionary<string, JsonElement>? _cache;
    private static string FilePath => Path.Combine(Config.LocalDir, "settings.json");

    private static Dictionary<string, JsonElement> All
    {
        get
        {
            if (_cache != null) return _cache;
            try { _cache = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(FilePath)); } catch { }
            return _cache ??= [];
        }
    }

    public static bool Bool(string key, bool fallback) =>
        All.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;

    public static string? Str(string key) => All.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static void Set(string key, object? value)
    {
        if (value == null) All.Remove(key);
        else All[key] = JsonSerializer.SerializeToElement(value);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(All)); } catch { }
    }

    public const string Appearance = "appearance";        // system | light | dark
    public const string HideAddresses = "hideAddresses";
    public const string HostLayout = "hostLayout";        // grid | list
    public const string Autocomplete = "terminal.autocomplete";
    public const string ProfileName = "profile.name";
    public const string ProfilePicture = "profile.picture";
    public const string DbShowTree = "db.showTree";
    public const string LocalShell = "terminal.localShell";   // shell bawaan tab terminal lokal        // daftar database & tabel di kiri
    public const string ActiveProject = "project.active";     // id project yang sedang dipakai
}

/// <summary>Pesan error dari zevn-core dalam bahasa Indonesia (binding C# tidak membawa pesannya).</summary>
internal static class Errors
{
    public static string Text(Exception e) => e switch
    {
        AggregateException a when a.InnerException != null => Text(a.InnerException),
        CoreException.WrongPassword => "Master password salah",
        CoreException.AlreadyExists => "Vault sudah ada",
        CoreException.NotFound => "Data tidak ditemukan",
        CoreException.WeakPassword => "Password minimal 8 karakter",
        CoreException.KeyMismatch => "Vault di Google Drive memakai master password lain",
        CoreException.InvalidKey k => $"SSH key tidak valid: {k.msg}",
        CoreException.Corrupt c => $"File vault rusak: {c.msg}",
        CoreException.Ssh s => $"Koneksi SSH gagal: {s.msg}",
        CoreException.Proxy px => $"Proxy: {px.msg}",
        CoreException.AuthFailed => "Login ditolak server — periksa username, password, atau key",
        CoreException.HostKeyChanged => "Kunci server berubah! Bisa jadi server diganti atau ada serangan man-in-the-middle. Hapus host ini di Known Hosts jika memang server berganti.",
        CoreException.Db d => $"Database: {d.msg}",
        CoreException.Cancelled => "Transfer dibatalkan",
        CoreException.Deploy d => $"Deploy: {d.msg}",
        CoreException.Io io => $"Gagal membaca/menulis file: {io.msg}",
        _ => e.Message
    };
}

/// <summary>Riwayat perintah per host — hanya di perangkat ini, tidak ikut sync.</summary>
internal static class CommandHistory
{
    private const int Limit = 500;
    private static Dictionary<string, List<string>>? _cache;

    private static string FilePath
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("ZEVN_HISTORY_DIR") is { Length: > 0 } d) return Path.Combine(d, "command-history.json");
#endif
            return Path.Combine(Config.LocalDir, "command-history.json");
        }
    }

    private static Dictionary<string, List<string>> All()
    {
        if (_cache != null) return _cache;
        try { _cache = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath)); } catch { }
        return _cache ??= [];
    }

    public static List<string> Commands(string hostId) => All().TryGetValue(hostId, out var l) ? l : [];

    public static void Add(string command, string hostId)
    {
        var cmd = command.Trim();
        // Perintah diawali spasi tidak disimpan (HISTCONTROL=ignorespace)
        if (cmd.Length < 2 || command.StartsWith(' ')) return;
        var map = All();
        if (!map.TryGetValue(hostId, out var list)) map[hostId] = list = [];
        list.Remove(cmd);
        list.Insert(0, cmd);
        if (list.Count > Limit) list.RemoveRange(Limit, list.Count - Limit);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(map)); } catch { }
    }

    public static void Clear()
    {
        _cache = [];
        try { File.Delete(FilePath); } catch { }
    }

    public static int Count => All().Values.Sum(l => l.Count);
}

/// <summary>Satu baris ~/.ssh/known_hosts.</summary>
internal sealed record KnownHost(int Line, string Hosts, string KeyType, string Fingerprint)
{
    public bool IsHashed => Hosts.StartsWith("|1|");
    public string ShortType => KeyType.Replace("ssh-", "").Replace("ecdsa-sha2-", "").ToUpperInvariant();
    public string Display(bool hidden) => IsHashed ? "Host ter-hash" : hidden ? string.Join(", ", Hosts.Split(',').Select(_ => "••••••")) : Hosts;
}

internal static class KnownHostsFile
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    public static List<KnownHost> Read()
    {
        var result = new List<KnownHost>();
        string[] lines;
        try { lines = File.ReadAllText(FilePath).Split('\n'); } catch { return result; }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count > 0 && parts[0].StartsWith('@')) parts.RemoveAt(0);
            if (parts.Count < 3) continue;
            try
            {
                var key = Convert.FromBase64String(parts[2]);
                var digest = Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
                result.Add(new KnownHost(i, parts[0], parts[1], "SHA256:" + digest));
            }
            catch { }
        }
        return result;
    }

    /// <summary>Hapus satu baris; salinan lama disimpan sebagai known_hosts.old (seperti ssh-keygen -R).</summary>
    public static void Remove(int index)
    {
        var lines = File.ReadAllText(FilePath).Split('\n').ToList();
        if (index >= lines.Count) return;
        File.Copy(FilePath, FilePath + ".old", true);
        lines.RemoveAt(index);
        File.WriteAllText(FilePath, string.Join("\n", lines));
    }
}

/// <summary>Windows Credential Manager (pengganti Keychain macOS).</summary>
internal static class CredentialStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL cred, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);

    private const uint Generic = 1, LocalMachine = 2;

    public static void Save(string target, byte[] secret)
    {
        var blob = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var c = new CREDENTIAL
            {
                Type = Generic, TargetName = target, CredentialBlobSize = (uint)secret.Length, CredentialBlob = blob,
                Persist = LocalMachine, UserName = Environment.UserName, Comment = "Zevn Shell"
            };
            CredWriteW(ref c, 0);
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static byte[]? Read(string target)
    {
        if (!CredReadW(target, Generic, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            var data = new byte[c.CredentialBlobSize];
            if (c.CredentialBlobSize > 0) Marshal.Copy(c.CredentialBlob, data, 0, data.Length);
            return data;
        }
        finally { CredFree(p); }
    }

    public static void Delete(string target) => CredDeleteW(target, Generic, 0);
}

internal static class HostExt
{
    public static Host Blank() => new(
        Id: "", Hostname: "", Label: "", Group: null, Tags: [], Color: Theme.HostColors[0],
        Port: 22, Username: "root", Auth: AuthMethod.Password, Password: null, KeyId: null,
        AgentForwarding: false, StartupSnippet: null, JumpHostId: null, Proxy: null, Env: [],
        Charset: "UTF-8", TerminalTheme: null, UpdatedAt: 0, Deleted: false);

    public static string Title(this Host h) => string.IsNullOrEmpty(h.Label) ? h.Hostname : h.Label;
    public static string Address(this Host h) => $"{h.Username}@{h.Hostname}" + (h.Port == 22 ? "" : $":{h.Port}");
    public static string DisplayAddress(this Host h, bool hidden) => hidden ? $"{h.Username}@••••••••" : h.Address();

    public static Snippet BlankSnippet() => new("", "", "", 0, false);

    public static Project BlankProject() => new("", "", Theme.HostColors[0], [], null, false, null, null, null, null, null, 0, false);

    public static PortForward BlankForward(string hostId) => new("", "", hostId, ForwardKind.Local, "127.0.0.1", 8080, "localhost", 80, 0, false);

    public static string Summary(this PortForward f, string via, bool hide)
    {
        var dest = hide && f.Kind == ForwardKind.Local && f.DestHost != "localhost" && f.DestHost != "127.0.0.1" ? "••••" : f.DestHost;
        return f.Kind switch
        {
            ForwardKind.Local => $"{f.BindAddress}:{f.BindPort} → {dest}:{f.DestPort}  via {via}",
            ForwardKind.Remote => $"{via} {f.BindAddress}:{f.BindPort} → {dest}:{f.DestPort}",
            _ => $"SOCKS {f.BindAddress}:{f.BindPort}  via {via}",
        };
    }

    public static string Short(this ForwardKind k) => k switch { ForwardKind.Local => "LOCAL", ForwardKind.Remote => "REMOTE", _ => "SOCKS" };

    public static string Explanation(this ForwardKind k) => k switch
    {
        ForwardKind.Local => "Port di PC ini diteruskan ke tujuan yang bisa dijangkau server. Contoh: 127.0.0.1:5433 → localhost:5432 (database di server).",
        ForwardKind.Remote => "Port di server diteruskan ke tujuan di sisi PC ini. Contoh: membuka web lokal ke server.",
        _ => "Proxy SOCKS di PC ini; semua lalu lintas keluar lewat server. Atur browser ke proxy SOCKS tersebut.",
    };
}
