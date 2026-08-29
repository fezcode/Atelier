using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Atelier.Hoswl
{
    /// <summary>
    /// Minimal client for the Hisashi OS Window Layer (hoswl): one named pipe,
    /// one JSON object per line. Connects lazily on a background task, retries
    /// every <see cref="RetryInterval"/> while Hisashi is absent, re-sends the
    /// cached menu after a reconnect, and hands <c>click</c> ids to
    /// <see cref="OnClick"/> on the reader thread (the caller marshals to the UI).
    /// Protocol: Hisashi/docs/hoswl-protocol.md. No dependency on Hisashi itself.
    /// </summary>
    public sealed class HoswlClient : IDisposable
    {
        public const string DefaultPipeName = "hoswl";

        private static readonly UTF8Encoding Utf8 = new(false);

        private readonly string _pipeName;
        private readonly string _appId, _name, _version;
        private readonly object _lock = new();
        private NamedPipeClientStream? _pipe;
        private Channel<byte[]>? _out;      // per-connection outbound queue
        private Task? _writer;
        private string? _menuLine;
        private bool _enabled = true;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public HoswlClient(string appId, string name, string version, string pipeName = DefaultPipeName)
        {
            _appId = appId;
            _name = name;
            _version = version;
            _pipeName = pipeName;
        }

        /// <summary>Raised on a background thread with the clicked item id.</summary>
        public event Action<string>? OnClick;

        /// <summary>Raised on a background thread when the connection comes up (true) or drops (false).</summary>
        public event Action<bool>? ConnectionChanged;

        public bool IsConnected { get; private set; }
        public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);

        public void Start()
        {
            lock (_lock)
            {
                if (_cts != null) return;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _loop = Task.Run(() => LoopAsync(ct), ct);
            }
        }

        /// <summary>Send <c>bye</c>, close the pipe and stop reconnecting.</summary>
        public void Stop()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_lock)
            {
                cts = _cts;
                loop = _loop;
                _cts = null;
                _loop = null;
            }
            if (cts == null) return;
            Send("{\"t\":\"bye\"}\n");
            Channel<byte[]>? outq; Task? writer; NamedPipeClientStream? pipe;
            lock (_lock) { outq = _out; writer = _writer; pipe = _pipe; }
            outq?.Writer.TryComplete();
            try { writer?.Wait(500); } catch { }   // let "bye" leave before the handle goes
            cts.Cancel();
            try { pipe?.Dispose(); } catch { }
            try { loop?.Wait(1000); } catch { }
            cts.Dispose();
        }

        public void Dispose() => Stop();

        /// <summary>Replace the whole menu tree (the protocol's "menus" array). Cached for reconnects.</summary>
        public void SetMenusJson(string menusJsonArray)
        {
            var line = "{\"t\":\"menu\",\"menus\":" + menusJsonArray + "}\n";
            lock (_lock) _menuLine = line;
            Send(line);
        }

        /// <summary>The app's integration switch: false keeps the connection but Hisashi shows nothing for it.</summary>
        public void SetEnabled(bool on)
        {
            lock (_lock) _enabled = on;
            Send("{\"t\":\"enable\",\"on\":" + (on ? "true" : "false") + "}\n");
        }

        public void SetItem(string id, bool? enabled = null, bool? check = null)
        {
            var sb = new StringBuilder("{\"t\":\"set\",\"id\":").Append(JsonSerializer.Serialize(id));
            if (enabled != null) sb.Append(",\"enabled\":").Append(enabled.Value ? "true" : "false");
            if (check != null) sb.Append(",\"check\":").Append(check.Value ? "true" : "false");
            Send(sb.Append("}\n").ToString());
        }

        // Sends never touch the pipe on the caller's thread: lines go through a queue
        // drained by one async writer per connection. A synchronous PipeStream.Write on
        // an overlapped handle can stall when it is issued from the reader's own
        // completion path (e.g. inside an OnClick handler), and callers should never
        // block on Hisashi's reader anyway.
        private void Send(string line)
        {
            Channel<byte[]>? q;
            lock (_lock) q = _out;
            q?.Writer.TryWrite(Utf8.GetBytes(line));
        }


        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    // Short timeout: when Hisashi isn't running the pipe doesn't exist and
                    // this fails fast; we simply try again after RetryInterval.
                    await pipe.ConnectAsync(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { pipe.Dispose(); return; }
                catch (Exception)
                {
                    pipe.Dispose();
                    if (!await SleepAsync(ct).ConfigureAwait(false)) return;
                    continue;
                }

                var outq = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
                var writer = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var chunk in outq.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                            await pipe.WriteAsync(chunk, ct).ConfigureAwait(false);
                    }
                    catch (Exception) { try { pipe.Dispose(); } catch { } }   // reader notices, reconnects
                });
                lock (_lock) { _pipe = pipe; _out = outq; _writer = writer; IsConnected = true; }
                try
                {
                    Send("{\"t\":\"hello\",\"v\":1,\"app\":" + JsonSerializer.Serialize(_appId)
                         + ",\"name\":" + JsonSerializer.Serialize(_name)
                         + ",\"ver\":" + JsonSerializer.Serialize(_version)
                         + ",\"pid\":" + Environment.ProcessId + "}\n");
                    string? menu; bool enabled;
                    lock (_lock) { menu = _menuLine; enabled = _enabled; }
                    if (menu != null) Send(menu);
                    if (!enabled) Send("{\"t\":\"enable\",\"on\":false}\n");
                    try { ConnectionChanged?.Invoke(true); } catch { }

                    using var reader = new StreamReader(pipe, Utf8);
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line == null) break;
                        HandleLine(line);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally
                {
                    lock (_lock) { _pipe = null; _out = null; _writer = null; IsConnected = false; }
                    outq.Writer.TryComplete();
                    try { pipe.Dispose(); } catch { }
                    try { await writer.ConfigureAwait(false); } catch { }
                    try { ConnectionChanged?.Invoke(false); } catch { }
                }

                if (ct.IsCancellationRequested) return;
                if (!await SleepAsync(ct).ConfigureAwait(false)) return;
            }
        }

        private async Task<bool> SleepAsync(CancellationToken ct)
        {
            try { await Task.Delay(RetryInterval, ct).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) { return false; }
        }

        private void HandleLine(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (!root.TryGetProperty("t", out var t) || t.GetString() != "click") return;
                if (!root.TryGetProperty("id", out var id)) return;
                var s = id.GetString();
                if (!string.IsNullOrEmpty(s)) OnClick?.Invoke(s);
            }
            catch (Exception)
            {
                // Unknown or malformed lines are ignored by design (forward compatibility).
            }
        }
    }
}
