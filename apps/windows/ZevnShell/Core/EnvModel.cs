using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Satu baris file .env: kosong, komentar, atau pasangan KEY=value.</summary>
internal sealed class EnvLine
{
    public enum Kind { Blank, Comment, Pair }

    public Kind Type { get; init; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Teks asli untuk komentar (termasuk tanda #).</summary>
    public string Text { get; init; } = "";
    /// <summary>"export " bila baris memakainya (dipakai sistemd).</summary>
    public string Prefix { get; init; } = "";
    public bool Secret { get; init; }
    public bool Added { get; init; }

    public string Rendered => Type switch
    {
        Kind.Blank => "",
        Kind.Comment => Text,
        _ => Prefix + Key + "=" + Value,
    };
}

/// <summary>
/// Editor file .env satu project di server: dibaca & ditulis lewat SFTP.
/// Isi lama di server disalin ke &lt;file&gt;.bak sebelum disimpan. Path file
/// disimpan per perangkat (settings.json), bukan di vault.
/// </summary>
internal sealed class EnvModel
{
    public enum Status { Idle, Connecting, Ready, Saving, Failed }

    private readonly AppModel _model;
    private readonly DispatcherQueue _dq;
    private SftpClient? _client;
    private string? _connection;
    private string _newline = "\n";
    private string _saved = "";

    public Status State { get; private set; } = Status.Idle;
    public string? Error { get; private set; }
    public string? Note { get; private set; }
    public string Path { get; private set; } = ".env";
    public string? HostId { get; private set; }
    public List<EnvLine> Lines { get; } = [];
    public bool Loaded { get; private set; }
    public bool Exists { get; private set; }
    public bool Reveal { get; private set; }
    public bool Busy => State is Status.Connecting or Status.Saving;

    public EnvModel(AppModel model)
    {
        _model = model;
        _dq = DispatcherQueue.GetForCurrentThread();
    }

    public bool Dirty => Loaded && Serialize() != _saved;
    public int PairCount => Lines.Count(l => l.Type == EnvLine.Kind.Pair);

    private void Raise() => _model.Raise("env");

    public static string PathKey(string projectId) => "env.path." + projectId;

    /// <summary>Lokasi .env bawaan: folder rilis deploy project, atau home user.</summary>
    public static string DefaultPath(Project p)
    {
        var dir = (p.DeployPath ?? "").Trim().TrimEnd('/');
        return dir.Length == 0 ? ".env" : dir + "/.env";
    }

    /// <summary>Path yang dipakai project ini di perangkat ini.</summary>
    public static string PathFor(Project p) => Settings.Str(PathKey(p.Id)) is { Length: > 0 } s ? s : DefaultPath(p);

    /// <summary>Buka file project (memakai path tersimpan).</summary>
    public async Task OpenAsync(Project p, Host host, SshTarget target)
    {
        if (Busy) return;
        HostId = host.Id;
        Path = PathFor(p);
        await ReadAsync(target);
    }

    /// <summary>Baca ulang file dari server.</summary>
    public async Task ReadAsync(SshTarget target)
    {
        if (State == Status.Saving) return;
        State = Status.Connecting;
        Error = null;
        Note = null;
        Raise();
        try
        {
            var client = await ConnectAsync(target);
            var text = await client.ReadText(Path);
            Exists = text != null;
            Parse(text ?? "");
            _saved = Serialize();
            Loaded = true;
            State = Status.Ready;
        }
        catch (Exception e)
        {
            Fail(e);
        }
        Raise();
    }

    /// <summary>Ganti path lalu muat ulang (disimpan per project di perangkat ini).</summary>
    public async Task SetPathAsync(Project p, string path, SshTarget target)
    {
        var clean = path.Trim();
        if (clean.Length == 0) clean = DefaultPath(p);
        if (clean != Path) Settings.Set(PathKey(p.Id), clean);
        Path = clean;
        await ReadAsync(target);
    }

    public void SetValue(EnvLine line, string value)
    {
        if (line.Type != EnvLine.Kind.Pair) return;
        line.Value = value;
        _model.Raise("env.dirty");
    }

    public void AddPair(string key, string value)
    {
        var k = key.Trim();
        if (k.Length == 0) return;
        Lines.Add(new EnvLine { Type = EnvLine.Kind.Pair, Key = k, Value = value, Secret = IsSecretKey(k), Added = true });
        _model.Raise("env.dirty");
    }

    public void Remove(EnvLine line)
    {
        Lines.Remove(line);
        _model.Raise("env.dirty");
    }

    public void ToggleReveal()
    {
        Reveal = !Reveal;
        Raise();
    }

    /// <summary>Tulis file ke server (backup .bak otomatis bila file sudah ada).</summary>
    public async Task SaveAsync()
    {
        if (State != Status.Ready || _client == null) return;
        var content = Serialize();
        if (content == _saved)
        {
            Note = "Tidak ada perubahan";
            Raise();
            return;
        }
        State = Status.Saving;
        Error = null;
        Note = null;
        Raise();
        try
        {
            var backup = await _client.WriteText(Path, content, true);
            _saved = content;
            Exists = true;
            State = Status.Ready;
            Note = backup == null ? "File baru dibuat di server" : $"Backup lama: {backup}";
            _model.Flash("Variabel disimpan ke " + Path);
        }
        catch (Exception e)
        {
            // Isi editor tetap ada dan tombol Simpan tetap bisa dicoba lagi setelah masalahnya dibetulkan.
            Error = Errors.Text(e);
            State = Status.Ready;
        }
        Raise();
    }

    /// <summary>Dipakai saat vault dikunci.</summary>
    public void Close()
    {
        var c = _client;
        _client = null;
        _connection = null;
        if (c != null) _ = c.Close();
        Lines.Clear();
        Loaded = false;
        Exists = false;
        DirtyStateReset();
        State = Status.Idle;
        Error = null;
        Note = null;
        HostId = null;
    }

    private void DirtyStateReset() => _saved = "";

    // MARK: Isi file

    private void Parse(string text)
    {
        Lines.Clear();
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Lines.Add(new EnvLine { Type = EnvLine.Kind.Blank }); continue; }
            if (line.TrimStart().StartsWith('#')) { Lines.Add(new EnvLine { Type = EnvLine.Kind.Comment, Text = line }); continue; }
            var body = line;
            var prefix = "";
            if (body.StartsWith("export ")) { prefix = "export "; body = body[7..]; }
            var eq = body.IndexOf('=');
            if (eq <= 0) { Lines.Add(new EnvLine { Type = EnvLine.Kind.Comment, Text = line }); continue; }
            var key = body[..eq].Trim();
            Lines.Add(new EnvLine
            {
                Type = EnvLine.Kind.Pair, Key = key, Value = body[(eq + 1)..], Prefix = prefix, Secret = IsSecretKey(key),
            });
        }
        while (Lines.Count > 0 && Lines[^1].Type == EnvLine.Kind.Blank) Lines.RemoveAt(Lines.Count - 1);
    }

    private string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var l in Lines)
        {
            sb.Append(l.Rendered);
            sb.Append(_newline);
        }
        return sb.ToString();
    }

    /// <summary>Nilai sensitif disembunyikan di layar (password, token, key, dsb.).</summary>
    public static bool IsSecretKey(string key)
    {
        var k = key.ToUpperInvariant();
        string[] words = ["SECRET", "TOKEN", "PASSWORD", "PASSWD", "PWD", "APIKEY", "API_KEY", "PRIVATE", "CREDENTIAL", "SALT", "AUTH", "SIGNATURE", "CERT", "WEBHOOK", "DSN"];
        if (words.Any(k.Contains)) return true;
        return k == "KEY" || k.EndsWith("_KEY") || k.StartsWith("KEY_") || k.EndsWith("PASS");
    }

    // MARK: Koneksi

    private async Task<SftpClient> ConnectAsync(SshTarget target)
    {
        var key = $"{target.Hostname}|{target.Port}|{target.Username}";
        if (_client != null && _connection == key) return _client;
        Close();
        var c = await SftpClient.Connect(target);
        _client = c;
        _connection = key;
        return c;
    }

    private void Fail(Exception e)
    {
        Error = Errors.Text(e);
        State = Status.Failed;
        _dq.TryEnqueue(Raise);
    }
}
