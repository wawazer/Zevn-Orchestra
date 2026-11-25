using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Menerima baris log dari core (thread Rust) dan meneruskannya ke model.</summary>
internal sealed class LogBridge(Action<string> onLine, Action<int> onEnd) : LogSink
{
    public void OnLine(string line) => onLine(line);
    public void OnEnd(int status) => onEnd(status);
}

/// <summary>
/// Satu sesi log service di server: koneksi, mulai/hentikan perintah, dan buffer
/// keluaran yang dibatasi supaya UI tidak tumbuh tanpa batas.
/// </summary>
internal sealed class LogModel
{
    public enum Status { Idle, Connecting, Running, Ended, Failed }

    private const int MaxLines = 2000;
    private const int FlushMs = 120;

    private readonly AppModel _model;
    private readonly DispatcherQueue _dq;
    private readonly object _gate = new();
    private readonly List<string> _lines = [];

    private LogStream? _stream;
    private LogBridge? _bridge;
    private string _text = "";
    private bool _flushQueued;
    private int _runId;

    public Status State { get; private set; } = Status.Idle;
    public string? Error { get; private set; }
    public int? ExitCode { get; private set; }

    /// <summary>Dipanggil di thread UI saat status atau isi log berubah.</summary>
    public event Action? Changed;

    public LogModel(AppModel model)
    {
        _model = model;
        _dq = DispatcherQueue.GetForCurrentThread();
    }

    public bool Busy => State is Status.Connecting or Status.Running;

    /// <summary>Seluruh isi buffer (baris dipisah newline).</summary>
    public string Text { get { lock (_gate) return _text; } }

    /// <summary>Salinan baris buffer — untuk mewarnai, mencari, dan mengekspor di UI.</summary>
    public string[] Lines { get { lock (_gate) return [.. _lines]; } }

    public async Task StartAsync(SshTarget target, string command)
    {
        if (Busy) return;
        _runId++;
        var run = _runId;
        Error = null;
        ExitCode = null;
        State = Status.Connecting;
        Changed?.Invoke();

        CloseStream();
        try
        {
            var stream = await LogStream.Connect(target);
            if (run != _runId)
            {
                await stream.Close();
                return;
            }
            _stream = stream;
            Clear();
            // Baris dari sesi lama bisa masih datang setelah stop/ganti project/kunci
            // vault; hanya terima yang berasal dari sesi (run) yang sedang aktif.
            _bridge = new LogBridge(
                line =>
                {
                    if (run == Volatile.Read(ref _runId)) Append(line);
                },
                status => _dq.TryEnqueue(() =>
                {
                    // Kegagalan kanal sudah dilaporkan lewat catch; jangan timpa "Gagal".
                    if (run != Volatile.Read(ref _runId) || Error != null) return;
                    ExitCode = status;
                    State = Status.Ended;
                    Changed?.Invoke();
                }));
            State = Status.Running;
            Changed?.Invoke();
            await stream.Start(command, _bridge);
            if (run == _runId) { State = Status.Ended; Changed?.Invoke(); }
        }
        catch (Exception e)
        {
            if (run == _runId)
            {
                Error = Errors.Text(e);
                State = Status.Failed;
                Changed?.Invoke();
            }
        }
        finally
        {
            if (run == _runId) _bridge = null;
        }
    }

    public async Task StopAsync()
    {
        var stream = _stream;
        _runId++;
        _bridge = null;
        State = Status.Idle;
        Changed?.Invoke();
        if (stream == null) return;
        try { await stream.Stop(); } catch { }
        try { await stream.Close(); } catch { }
        if (ReferenceEquals(_stream, stream)) _stream = null;
    }

    /// <summary>Buang isi buffer, mulai dari kosong lagi.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _text = "";
        }
        Changed?.Invoke();
    }

    /// <summary>Dipakai saat vault dikunci: hentikan sesi tanpa menyentuh UI.</summary>
    public void Close()
    {
        _runId++;
        _bridge = null;
        var stream = _stream;
        _stream = null;
        State = Status.Idle;
        Error = null;
        ExitCode = null;
        if (stream != null) _ = stream.Close();
        lock (_gate)
        {
            _lines.Clear();
            _text = "";
        }
    }

    private void CloseStream()
    {
        var stream = _stream;
        _stream = null;
        if (stream != null) _ = stream.Close();
    }

    /// <summary>Dipanggil dari thread Rust untuk setiap baris.</summary>
    private void Append(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
        if (_flushQueued) return;
        _flushQueued = true;
        _ = Task.Delay(FlushMs).ContinueWith(_ =>
        {
            string text;
            lock (_gate)
            {
                text = string.Join('\n', _lines);
                _flushQueued = false;
            }
            _dq.TryEnqueue(() =>
            {
                _text = text;
                Changed?.Invoke();
            });
        });
    }
}
