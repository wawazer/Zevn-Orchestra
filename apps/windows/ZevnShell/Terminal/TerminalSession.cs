using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Satu tab terminal = satu proses ssh.exe di dalam ConPTY, ditampilkan oleh xterm.js (WebView2).</summary>
internal sealed class TerminalSession
{
    public enum Status { Connecting, Connected, Disconnected }

    public Guid Id { get; } = Guid.NewGuid();
    public Host Host { get; }
    /// <summary>Shell lokal (PowerShell/CMD/WSL), null = sesi SSH.</summary>
    public LocalShell? Local { get; }
    public bool IsLocal => Local != null;
    public string Title { get; private set; }
    public string? ThemeId { get; private set; }
    public Status State { get; private set; } = Status.Connecting;
    public int? ExitCode { get; private set; }
    public WebView2 View { get; }
    public event Action? Changed;

    private readonly SshKey? _key;
    private readonly Host? _jump;
    private readonly string? _run;
    private readonly AppModel _model;
    private readonly DispatcherQueue _dq;
    private PtyProcess? _pty;
    private SshCommand? _cmd;
    private string? _logId;
    private bool _ready, _gotData, _terminated;
    private int _cols = 100, _rows = 30;
    private readonly StringBuilder _pending = new();
    private bool _flushQueued;

    public TerminalSession(Host host, SshKey? key, Host? jump, string? run, AppModel model, LocalShell? local = null)
    {
        Host = host; _key = key; _jump = jump; _run = run; _model = model; Local = local;
        Title = host.Title();
        ThemeId = host.TerminalTheme;
        _dq = DispatcherQueue.GetForCurrentThread();
        View = new WebView2 { DefaultBackgroundColor = Theme.Hex(TerminalThemes.Get(ThemeId).Bg) };
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await View.EnsureCoreWebView2Async();
        }
        catch (Exception e)
        {
            _model.Flash("WebView2 Runtime tidak tersedia: " + e.Message);
            return;
        }
        var core = View.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
        core.SetVirtualHostNameToFolderMapping("zevn.terminal", dir, CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnMessage;
        // Tautan di terminal tidak dibuka di dalam WebView
        core.NewWindowRequested += (_, a) => { a.Handled = true; Ui.Open(a.Uri); };
        core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("https://zevn.terminal/")) { a.Cancel = true; Ui.Open(a.Uri); } };
        View.Source = new Uri("https://zevn.terminal/terminal.html");
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var doc = JsonDocument.Parse(args.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                _ready = true;
                _cols = m.GetProperty("cols").GetInt32();
                _rows = m.GetProperty("rows").GetInt32();
                PushTheme();
                PushConfig();
                Start();
                break;
            case "in":
                _pty?.Write(m.GetProperty("d").GetString() ?? "");
                break;
            case "resize":
                _cols = m.GetProperty("cols").GetInt32();
                _rows = m.GetProperty("rows").GetInt32();
                _pty?.Resize(_cols, _rows);
                break;
            case "title":
                var t = m.GetProperty("s").GetString();
                // ConPTY mengirim judul jendela console (path ssh.exe) — abaikan, pakai judul dari server saja
                if (!string.IsNullOrWhiteSpace(t) && !t.Contains(":\\") && !t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                { Title = t; Changed?.Invoke(); }
                break;
            case "hist":
                CommandHistory.Add(m.GetProperty("cmd").GetString() ?? "", Host.Id);
                break;
            case "copy":
                Ui.CopyText(m.GetProperty("s").GetString() ?? "");
                break;
            case "paste":
                _ = PasteAsync();
                break;
            case "menu":
                ShowMenu(m.GetProperty("x").GetDouble(), m.GetProperty("y").GetDouble(), m.GetProperty("sel").GetBoolean());
                break;
            case "key":
                _model.HandleTerminalShortcut(m.GetProperty("k").GetString() ?? "");
                break;
        }
    }

    private void Post(object o)
    {
        if (!_ready || View.CoreWebView2 == null) return;
        View.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o));
    }

    public void PushTheme()
    {
        var d = TerminalThemes.Get(ThemeId);
        View.DefaultBackgroundColor = Theme.Hex(d.Bg);
        Post(new { t = "theme", theme = TerminalThemes.ToXterm(d), light = d.IsLight });
    }

    public void PushConfig() => Post(new
    {
        t = "cfg", enabled = Settings.Bool(Settings.Autocomplete, true), hostId = Host.Id,
        history = CommandHistory.Commands(Host.Id), common = CommonCommands,
        snippets = _model.Snippets.Select(s => new { label = s.Label, command = s.Command }).ToArray()
    });

    private async Task PasteAsync()
    {
        var text = await Ui.PasteText();
        if (!string.IsNullOrEmpty(text)) Post(new { t = "paste", d = text.Replace("\r\n", "\n") });
    }

    private void ShowMenu(double x, double y, bool hasSelection)
    {
        var f = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Salin", IsEnabled = hasSelection, Icon = new FontIcon { Glyph = G.Copy } };
        copy.Click += (_, _) => Post(new { t = "copySel" });
        var paste = new MenuFlyoutItem { Text = "Tempel", Icon = new FontIcon { Glyph = "" } };
        paste.Click += (_, _) => _ = PasteAsync();
        var all = new MenuFlyoutItem { Text = "Pilih Semua" };
        all.Click += (_, _) => Post(new { t = "selectAll" });
        var clear = new MenuFlyoutItem { Text = "Bersihkan layar" };
        clear.Click += (_, _) => Send("\u000C");
        f.Items.Add(copy); f.Items.Add(paste); f.Items.Add(all);
        f.Items.Add(new MenuFlyoutSeparator()); f.Items.Add(clear);
        if (State == Status.Disconnected)
        {
            var re = new MenuFlyoutItem { Text = "Sambung ulang", Icon = new FontIcon { Glyph = G.Refresh } };
            re.Click += (_, _) => Reconnect();
            f.Items.Add(re);
        }
        f.ShowAt(View, new FlyoutShowOptions { Position = new Windows.Foundation.Point(x, y) });
    }

    public void ApplyTheme(string? id) { ThemeId = id; PushTheme(); Changed?.Invoke(); }

    /// <summary>Kirim teks seolah diketik (snippet) — ikut dilacak autocomplete.</summary>
    public void Send(string text) => Post(new { t = "input", d = text });

#if DEBUG
    public void DebugAccept() => Post(new { t = "acceptAc" });
#endif

    public void Focus()
    {
        View.Focus(FocusState.Programmatic);
        Post(new { t = "focus" });
    }

    // MARK: Proses

    private void Start()
    {
        _terminated = false;
        _gotData = false;
        if (Local is { } shell)
        {
            State = Status.Connecting;
            ExitCode = null;
            Changed?.Invoke();
            var env = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value ?? "";
            env["TERM"] = "xterm-256color";
            env["COLORTERM"] = "truecolor";
            env["TERM_PROGRAM"] = "ZevnShell";
            try
            {
                var pty = PtyProcess.Start(shell.CommandLine, env, _cols, _rows, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                pty.Output += OnOutput;
                pty.Exited += code => _dq.TryEnqueue(() => { if (_pty == pty) Ended(code); });
                _pty = pty;
            }
            catch (Exception e)
            {
                Write($"\r\n\x1b[31mGagal menjalankan {shell.Name}: {e.Message}\x1b[0m\r\n");
                Ended(255);
            }
            return;
        }
        if (!SshCommand.SshAvailable)
        {
            Write("\r\n\x1b[33mOpenSSH Client belum terpasang di Windows ini.\x1b[0m\r\nPasang lewat Settings → System → Optional features → OpenSSH Client.\r\n");
            State = Status.Disconnected; Changed?.Invoke();
            return;
        }
        _cmd = new SshCommand(Host, _key, _jump);
        State = Status.Connecting;
        ExitCode = null;
        Changed?.Invoke();
        _logId = _model.LogStart(Host);
        try
        {
            var pty = PtyProcess.Start(_cmd.CommandLine, _cmd.Env, _cols, _rows);
            pty.Output += OnOutput;
            pty.Exited += code => _dq.TryEnqueue(() => { if (_pty == pty) Ended(code); });
            _pty = pty;
        }
        catch (Exception e)
        {
            Write($"\r\n\x1b[31mGagal menjalankan ssh: {e.Message}\x1b[0m\r\n");
            Ended(255);
        }
    }

    private void OnOutput(string s)
    {
        lock (_pending)
        {
            _pending.Append(s);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        _dq.TryEnqueue(Flush);
    }

    private void Flush()
    {
        string data;
        lock (_pending)
        {
            data = _pending.ToString();
            _pending.Clear();
            _flushQueued = false;
        }
        if (data.Length == 0) return;
        Write(data);
        if (!_gotData)
        {
            _gotData = true;
            State = Status.Connected;
            Changed?.Invoke();
            _model.SessionStatusChanged();
            var commands = new[] { Host.StartupSnippet, _run }.Where(c => !string.IsNullOrEmpty(c)).ToList();
            for (int i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i]!;
                _ = Task.Delay(600 + i * 300).ContinueWith(_ => _dq.TryEnqueue(() => Send(cmd + "\r")));
            }
        }
    }

    private void Write(string s) => Post(new { t = "out", d = s });

    private void Ended(int code)
    {
        Flush();
        _cmd?.Cleanup();
        _model.LogEnd(_logId, code);
        _logId = null;
        _pty?.Dispose();
        _pty = null;
        ExitCode = code;
        State = Status.Disconnected;
        Write("\r\n\x1b[2m— sesi berakhir —\x1b[0m\r\n");
        Changed?.Invoke();
        _model.SessionStatusChanged();
    }

    public void Reconnect()
    {
        Kill();
        Post(new { t = "reset" });
        Start();
    }

    private void Kill()
    {
        var p = _pty;
        _pty = null;
        p?.Dispose();
        _cmd?.Cleanup();
        if (_logId != null) { _model.LogEnd(_logId, null); _logId = null; }
    }

    public void Terminate()
    {
        if (_terminated) return;
        _terminated = true;
        Kill();
        try { View.Close(); } catch { }
    }

    /// <summary>Perintah umum Linux/server untuk saran autocomplete.</summary>
    public static readonly string[] CommonCommands =
    [
        "ls -la", "ls -lah", "cd ..", "cd ~", "pwd", "clear", "exit", "history",
        "cat /etc/os-release", "uname -a", "uptime", "whoami", "hostname -I",
        "df -h", "du -sh *", "du -sh .[!.]* * | sort -h", "free -h", "top", "htop", "ps aux | grep ",
        "kill -9 ", "lsof -i :", "ss -tulpn", "netstat -tulpn", "ip a", "ping -c 4 ", "curl -I ", "wget ",
        "tail -f /var/log/syslog", "tail -n 100 -f ", "journalctl -u ", "journalctl -xe", "dmesg | tail",
        "systemctl status ", "systemctl restart ", "systemctl start ", "systemctl stop ", "systemctl enable ",
        "systemctl list-units --type=service --state=running", "service nginx reload",
        "sudo apt update && sudo apt upgrade -y", "sudo apt install ", "sudo yum install ", "sudo dnf install ",
        "sudo -i", "sudo su -", "chmod +x ", "chmod -R 755 ", "chown -R www-data:www-data ",
        "tar -czvf arsip.tar.gz ", "tar -xzvf ", "unzip ", "zip -r arsip.zip ",
        "find . -name \"*.log\"", "grep -rn \"\" .", "nano ", "vim ", "less ", "mkdir -p ", "rm -rf ", "cp -r ", "mv ",
        "scp ", "rsync -avz ", "ssh-keygen -t ed25519", "crontab -e", "crontab -l",
        "docker ps", "docker ps -a", "docker images", "docker logs -f ", "docker exec -it  bash", "docker compose up -d",
        "docker compose down", "docker compose logs -f", "docker compose ps", "docker system prune -af", "docker stats",
        "git status", "git pull", "git log --oneline -10", "git diff", "git checkout ", "git branch -a",
        "nginx -t", "sudo nginx -t && sudo systemctl reload nginx", "certbot renew --dry-run",
        "pm2 list", "pm2 logs", "pm2 restart all", "php artisan migrate", "php artisan cache:clear",
        "composer install", "npm install", "npm run build", "npm run dev",
        "mysql -u root -p", "psql -U postgres", "redis-cli", "ufw status", "sudo ufw allow ",
        "kubectl get pods -A", "kubectl get svc", "kubectl logs -f ", "kubectl describe pod ",
    ];
}
