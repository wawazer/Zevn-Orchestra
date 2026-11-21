using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZevnShell;

internal sealed class AuthCancelledException() : Exception("Login dibatalkan");
internal sealed class NotSignedInException() : Exception("Sesi Google berakhir. Masuk lagi untuk melanjutkan sync.");

/// <summary>
/// Login Google (OAuth 2.0 + PKCE). Memakai client ID yang sama dengan versi macOS;
/// redirect lewat skema URL kustom yang didaftarkan ke Zevn Shell, lalu diteruskan ke instance yang sedang jalan.
/// </summary>
internal static class GoogleAuth
{
    public sealed record Result(string Email, string? Name, string? Picture);

    public static string Scheme => string.Join(".", Enumerable.Reverse(Config.GoogleClientID.Split('.')));
    private static string Redirect => Scheme + ":/oauth2redirect";
    private static string PipeName => "zevnshell-oauth-" + Environment.UserName;
    private static CancellationTokenSource? _pending;

    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Dipanggil saat Windows membuka ZevnShell.exe dengan URL redirect: kirim ke instance utama.</summary>
    public static void ForwardToRunningInstance(string url)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(3000);
            var b = Encoding.UTF8.GetBytes(url);
            c.Write(b, 0, b.Length);
        }
        catch { }
    }

    /// <summary>Daftarkan skema URL di HKCU (tanpa admin) agar browser bisa kembali ke app.</summary>
    private static void RegisterScheme()
    {
        var exe = Environment.ProcessPath ?? "";
        using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        k.SetValue("", "URL:Zevn Shell");
        k.SetValue("URL Protocol", "");
        using var cmd = k.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{exe}\" \"%1\"");
    }

    public static void Cancel() => _pending?.Cancel();

    public static async Task<Result> SignIn()
    {
        RegisterScheme();
        _pending?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _pending = cts;

        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var q = new Dictionary<string, string>
        {
            ["client_id"] = Config.GoogleClientID, ["redirect_uri"] = Redirect, ["response_type"] = "code",
            ["scope"] = "openid email profile https://www.googleapis.com/auth/drive.appdata",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state,
        };
        var url = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        // Tunggu redirect lewat named pipe
        var wait = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cts.Token);
            using var r = new StreamReader(server, Encoding.UTF8);
            return await r.ReadToEndAsync(cts.Token);
        }, cts.Token);
        Ui.Open(url);

        string callback;
        try { callback = await wait; }
        catch (OperationCanceledException) { throw new AuthCancelledException(); }
        finally { if (_pending == cts) _pending = null; }

        var query = ParseQuery(callback);
        if (query.GetValueOrDefault("state") != state) throw new Exception("Respons login tidak cocok, coba lagi");
        if (query.TryGetValue("error", out var err)) throw err == "access_denied" ? new AuthCancelledException() : new Exception("Login gagal: " + err);
        if (!query.TryGetValue("code", out var code)) throw new Exception("Kode otorisasi tidak diterima");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = Config.GoogleClientID, ["redirect_uri"] = Redirect,
            ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
        });
        var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", form);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var idToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var claims = idToken == null ? null : Claims(idToken);
        if (claims == null || !claims.RootElement.TryGetProperty("email", out var email))
            throw new Exception("Tidak bisa membaca email akun Google");
        GoogleTokens.Store(
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            root.TryGetProperty("access_token", out var at) ? at.GetString() : null,
            root.TryGetProperty("expires_in", out var ex) ? ex.GetDouble() : 3600);
        App.Window?.BringToFront();
        return new Result(email.GetString()!,
            claims.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null,
            claims.RootElement.TryGetProperty("picture", out var p) ? p.GetString() : null);
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var d = new Dictionary<string, string>();
        var i = url.IndexOf('?');
        if (i < 0) return d;
        foreach (var part in url[(i + 1)..].Trim().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    private static JsonDocument? Claims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0) b64 += "=";
        try { return JsonDocument.Parse(Convert.FromBase64String(b64)); } catch { return null; }
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>Refresh token Google di Windows Credential Manager; access token hanya di memori.</summary>
internal static class GoogleTokens
{
    private const string Target = "ZevnShell/google-refresh-token";
    private static string? _access;
    private static DateTime _expiry = DateTime.MinValue;

    public static bool HasRefreshToken => CredentialStore.Read(Target) != null;

    public static void Store(string? refresh, string? access, double expiresIn)
    {
        if (refresh != null) CredentialStore.Save(Target, Encoding.UTF8.GetBytes(refresh));
        if (access != null) { _access = access; _expiry = DateTime.UtcNow.AddSeconds(expiresIn - 60); }
    }

    public static void Forget()
    {
        _access = null;
        _expiry = DateTime.MinValue;
        CredentialStore.Delete(Target);
    }

    public static async Task<string> ValidAccessToken()
    {
        if (_access != null && DateTime.UtcNow < _expiry) return _access;
        var raw = CredentialStore.Read(Target) ?? throw new NotSignedInException();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Config.GoogleClientID, ["grant_type"] = "refresh_token", ["refresh_token"] = Encoding.UTF8.GetString(raw),
        });
        var resp = await GoogleAuth.Http.PostAsync("https://oauth2.googleapis.com/token", form);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("access_token", out var at))
        {
            if ((int)resp.StatusCode == 400) Forget();
            throw new NotSignedInException();
        }
        Store(null, at.GetString(), doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetDouble() : 3600);
        return _access!;
    }
}

/// <summary>Akses vault.zevn di appDataFolder Google Drive (folder tersembunyi khusus app).</summary>
internal static class DriveClient
{
    public const string FileName = "vault.zevn";
    public sealed record RemoteFile(string Id);

    public static async Task<RemoteFile?> Find()
    {
        var q = Uri.EscapeDataString($"name = '{FileName}' and trashed = false");
        var url = $"https://www.googleapis.com/drive/v3/files?spaces=appDataFolder&q={q}&fields=files(id,modifiedTime)&orderBy=modifiedTime%20desc";
        var data = await Send(new HttpRequestMessage(HttpMethod.Get, url));
        using var doc = JsonDocument.Parse(data);
        var files = doc.RootElement.GetProperty("files");
        return files.GetArrayLength() == 0 ? null : new RemoteFile(files[0].GetProperty("id").GetString()!);
    }

    public static Task<byte[]> Download(RemoteFile f) =>
        Send(new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{f.Id}?alt=media"));

    public static async Task Upload(byte[] blob, RemoteFile? replacing)
    {
        if (replacing != null)
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{replacing.Id}?uploadType=media")
            { Content = new ByteArrayContent(blob) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await Send(req);
            return;
        }
        var multi = new MultipartContent("related", "zevn-" + Guid.NewGuid().ToString("N"));
        var meta = new StringContent($"{{\"name\":\"{FileName}\",\"parents\":[\"appDataFolder\"]}}", Encoding.UTF8, "application/json");
        var body = new ByteArrayContent(blob);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multi.Add(meta); multi.Add(body);
        await Send(new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart") { Content = multi });
    }

    private static async Task<byte[]> Send(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GoogleTokens.ValidAccessToken());
        var resp = await GoogleAuth.Http.SendAsync(req);
        var data = await resp.Content.ReadAsByteArrayAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 401) { GoogleTokens.Forget(); throw new NotSignedInException(); }
            var msg = Encoding.UTF8.GetString(data);
            throw new Exception($"Google Drive error {(int)resp.StatusCode}: {msg[..Math.Min(200, msg.Length)]}");
        }
        return data;
    }
}

/// <summary>Buka vault dengan Windows Hello: kunci vault di Credential Manager, dibaca setelah verifikasi Hello.</summary>
internal static class HelloUnlock
{
    private const string Target = "ZevnShell/vault-key";

    public static bool HasStoredKey => CredentialStore.Read(Target) != null;

    public static async Task<bool> IsAvailable()
    {
        try
        {
            var a = await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync();
            return a == Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available;
        }
        catch { return false; }
    }

    public static void Store(byte[] key) => CredentialStore.Save(Target, key);
    public static void Forget() => CredentialStore.Delete(Target);

    public static async Task<byte[]> Retrieve()
    {
        var r = await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync("Membuka vault Zevn Shell");
        if (r != Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified) throw new AuthCancelledException();
        return CredentialStore.Read(Target) ?? throw new Exception("Kunci Windows Hello tidak ditemukan");
    }
}

internal sealed record AvailableUpdate(string Version, string Notes, string Page, string? Download);

internal static class UpdateChecker
{
    public static bool IsNewer(string remote, string local)
    {
        static int[] Parts(string v) => v.TrimStart('v', 'V').Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        var a = Parts(remote); var b = Parts(local);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0; var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    public static async Task<AvailableUpdate?> Check()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/wawazer/zevnshell/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("ZevnShell-Windows");
        var resp = await GoogleAuth.Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null; // belum ada rilis
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag, Config.Version)) return null;
        string? dl = null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msi") || name.EndsWith(".exe"))
            { dl = a.GetProperty("browser_download_url").GetString(); break; }
        }
        return new AvailableUpdate(tag.TrimStart('v', 'V'), r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            r.GetProperty("html_url").GetString() ?? Config.Releases, dl);
    }
}
