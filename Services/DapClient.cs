using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DotNetIDE;

public class DapClient : IAsyncDisposable
{
    private static class Timeouts
    {
        public const int Default = 5000;
        public const int Initialize = 10000;
        public const int Launch = 15000;
        public const int Disconnect = 3000;
    }

    private static void Log(string msg) => DiagnosticLog.Write("dap", msg);

    private Process? _process;
    private StreamWriter? _stdin;
    private int _nextSeq = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private Thread? _readThread;
    private volatile bool _disposed;

    // Events
    public event EventHandler<DapStoppedEventArgs>? Stopped;
    public event EventHandler? Continued;
    public event EventHandler? Terminated;
    public event EventHandler<(string Category, string Text)>? OutputReceived;
    public event EventHandler? Initialized;
    public event EventHandler<int>? Exited;

    public async Task<bool> StartAsync(DapServer server)
    {
        try
        {
            var psi = new ProcessStartInfo(server.Exe, server.Args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _process = Process.Start(psi);
            if (_process == null) return false;

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = false,
                NewLine = "\r\n"
            };

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "DAP-read" };
            _readThread.Start();

            // Drain stderr
            var stderrThread = new Thread(() =>
            {
                try
                {
                    var stderr = _process!.StandardError;
                    while (!_disposed && stderr.ReadLine() is not null) { }
                }
                catch (Exception ex) { Log($"DAP-stderr drain: {ex.Message}"); }
            }) { IsBackground = true, Name = "DAP-stderr" };
            stderrThread.Start();

            return true;
        }
        catch (Exception ex)
        {
            Log($"StartAsync: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> InitializeAsync()
    {
        var result = await SendRequestAsync("initialize", new
        {
            clientID = "lazydotide",
            clientName = "LazyDotIDE",
            adapterID = "coreclr",
            pathFormat = "path",
            linesStartAt1 = true,
            columnsStartAt1 = true,
            supportsRunInTerminalRequest = false
        }, Timeouts.Initialize);

        return result.HasValue;
    }

    public Task SendConfigurationDone()
    {
        return SendRequestAsync("configurationDone", new { });
    }

    public async Task<bool> LaunchAsync(LaunchProfile profile)
    {
        var args = new Dictionary<string, object?>
        {
            ["name"] = profile.Name,
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["program"] = profile.Program,
            ["stopAtEntry"] = false
        };

        if (profile.Cwd != null) args["cwd"] = profile.Cwd;
        if (profile.Args is { Length: > 0 }) args["args"] = profile.Args;
        if (profile.Env is { Count: > 0 }) args["env"] = profile.Env;

        var result = await SendRequestAsync("launch", args, Timeouts.Launch);
        return result.HasValue;
    }

    public async Task<List<DapBreakpoint>> SetBreakpointsAsync(string filePath, List<SourceBreakpoint> breakpoints)
    {
        var result = new List<DapBreakpoint>();
        try
        {
            var response = await SendRequestAsync("setBreakpoints", new
            {
                source = new { path = filePath },
                breakpoints = breakpoints.Select(b => new { line = b.Line }).ToArray(),
                sourceModified = false
            });

            if (response.HasValue && response.Value.TryGetProperty("breakpoints", out var bps) &&
                bps.ValueKind == JsonValueKind.Array)
            {
                foreach (var bp in bps.EnumerateArray())
                {
                    var line = bp.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    var verified = bp.TryGetProperty("verified", out var v) && v.GetBoolean();
                    var message = bp.TryGetProperty("message", out var m) ? m.GetString() : null;
                    result.Add(new DapBreakpoint(line, verified, message));
                }
            }
        }
        catch (Exception ex) { Log($"SetBreakpointsAsync: {ex.Message}"); }
        return result;
    }

    public Task ContinueAsync(int threadId)
        => SendRequestAsync("continue", new { threadId });

    public Task NextAsync(int threadId)
        => SendRequestAsync("next", new { threadId });

    public Task StepInAsync(int threadId)
        => SendRequestAsync("stepIn", new { threadId });

    public Task StepOutAsync(int threadId)
        => SendRequestAsync("stepOut", new { threadId });

    public Task PauseAsync(int threadId)
        => SendRequestAsync("pause", new { threadId });

    public async Task DisconnectAsync()
    {
        try
        {
            await SendRequestAsync("disconnect", new { terminateDebuggee = true }, Timeouts.Disconnect);
        }
        catch (Exception ex) { Log($"DisconnectAsync: {ex.Message}"); }
    }

    public async Task<List<DapThread>> ThreadsAsync()
    {
        var result = new List<DapThread>();
        try
        {
            var response = await SendRequestAsync("threads", new { });
            if (response.HasValue && response.Value.TryGetProperty("threads", out var threads) &&
                threads.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in threads.EnumerateArray())
                {
                    var id = t.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    result.Add(new DapThread(id, name));
                }
            }
        }
        catch (Exception ex) { Log($"ThreadsAsync: {ex.Message}"); }
        return result;
    }

    public async Task<List<DapStackFrame>> StackTraceAsync(int threadId)
    {
        var result = new List<DapStackFrame>();
        try
        {
            var response = await SendRequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 50 });
            if (response.HasValue && response.Value.TryGetProperty("stackFrames", out var frames) &&
                frames.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in frames.EnumerateArray())
                {
                    var id = f.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var line = f.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    var col = f.TryGetProperty("column", out var c) ? c.GetInt32() : 0;

                    DapSource? source = null;
                    if (f.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object)
                    {
                        var sName = s.TryGetProperty("name", out var sn) ? sn.GetString() : null;
                        var sPath = s.TryGetProperty("path", out var sp) ? sp.GetString() : null;
                        source = new DapSource(sName, sPath);
                    }

                    result.Add(new DapStackFrame(id, name, source, line, col));
                }
            }
        }
        catch (Exception ex) { Log($"StackTraceAsync: {ex.Message}"); }
        return result;
    }

    public async Task<List<DapScope>> ScopesAsync(int frameId)
    {
        var result = new List<DapScope>();
        try
        {
            var response = await SendRequestAsync("scopes", new { frameId });
            if (response.HasValue && response.Value.TryGetProperty("scopes", out var scopes) &&
                scopes.ValueKind == JsonValueKind.Array)
            {
                foreach (var sc in scopes.EnumerateArray())
                {
                    var name = sc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var varRef = sc.TryGetProperty("variablesReference", out var v) ? v.GetInt32() : 0;
                    var expensive = sc.TryGetProperty("expensive", out var e) && e.GetBoolean();
                    result.Add(new DapScope(name, varRef, expensive));
                }
            }
        }
        catch (Exception ex) { Log($"ScopesAsync: {ex.Message}"); }
        return result;
    }

    public async Task<List<DapVariable>> VariablesAsync(int variablesReference)
    {
        var result = new List<DapVariable>();
        try
        {
            var response = await SendRequestAsync("variables", new { variablesReference });
            if (response.HasValue && response.Value.TryGetProperty("variables", out var vars) &&
                vars.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vars.EnumerateArray())
                {
                    var name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var value = v.TryGetProperty("value", out var val) ? val.GetString() ?? "" : "";
                    var type = v.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var varRef = v.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0;
                    result.Add(new DapVariable(name, value, type, varRef));
                }
            }
        }
        catch (Exception ex) { Log($"VariablesAsync: {ex.Message}"); }
        return result;
    }

    // ── Wire protocol ──

    private async Task<JsonElement?> SendRequestAsync(string command, object? arguments, int timeout = Timeouts.Default)
    {
        if (_stdin == null || _disposed) return null;

        var seq = Interlocked.Increment(ref _nextSeq);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;

        var request = JsonSerializer.Serialize(new
        {
            seq,
            type = "request",
            command,
            arguments
        });
        SendRaw(request);

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetResult(null));

        return await tcs.Task;
    }

    private void SendRaw(string json)
    {
        if (_stdin == null) return;
        lock (_stdin)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                _stdin.Write($"Content-Length: {bytes.Length}\r\n\r\n");
                _stdin.Write(json);
                _stdin.Flush();
            }
            catch (Exception ex) { Log($"SendRaw: {ex.Message}"); }
        }
    }

    private void ReadLoop()
    {
        if (_process == null) return;
        var reader = _process.StandardOutput.BaseStream;

        try
        {
            while (!_disposed)
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var line = ReadLine(reader);
                    if (line == null) return;
                    if (line.Length == 0) break;
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                if (!headers.TryGetValue("Content-Length", out var lenStr) ||
                    !int.TryParse(lenStr, out var len)) continue;

                var buf = new byte[len];
                int read = 0;
                while (read < len)
                {
                    var n = reader.Read(buf, read, len - read);
                    if (n == 0) return;
                    read += n;
                }

                var json = Encoding.UTF8.GetString(buf);
                ProcessMessage(json);
            }
        }
        catch (Exception ex) { Log($"ReadLoop: {ex.Message}"); }
    }

    private static string? ReadLine(Stream stream)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1) return null;
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            switch (type)
            {
                case "response":
                    HandleResponse(root);
                    break;
                case "event":
                    HandleEvent(root);
                    break;
            }
        }
        catch (Exception ex) { Log($"ProcessMessage: {ex.Message}"); }
    }

    private void HandleResponse(JsonElement root)
    {
        if (!root.TryGetProperty("request_seq", out var reqSeqEl)) return;
        var reqSeq = reqSeqEl.GetInt32();

        if (_pending.TryRemove(reqSeq, out var tcs))
        {
            if (root.TryGetProperty("success", out var successEl) && successEl.GetBoolean() &&
                root.TryGetProperty("body", out var body))
                tcs.TrySetResult(body.Clone());
            else
                tcs.TrySetResult(null);
        }
    }

    private void HandleEvent(JsonElement root)
    {
        var eventName = root.TryGetProperty("event", out var e) ? e.GetString() : null;
        if (eventName == null) return;

        var body = root.TryGetProperty("body", out var b) ? b : default;

        switch (eventName)
        {
            case "initialized":
                Initialized?.Invoke(this, EventArgs.Empty);
                break;

            case "stopped":
                var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                var threadId = body.TryGetProperty("threadId", out var tid) ? tid.GetInt32() : 0;
                var desc = body.TryGetProperty("description", out var d) ? d.GetString() : null;
                Stopped?.Invoke(this, new DapStoppedEventArgs(reason, threadId, desc));
                break;

            case "continued":
                Continued?.Invoke(this, EventArgs.Empty);
                break;

            case "terminated":
                Terminated?.Invoke(this, EventArgs.Empty);
                break;

            case "exited":
                var exitCode = body.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : 0;
                Exited?.Invoke(this, exitCode);
                break;

            case "output":
                var category = body.TryGetProperty("category", out var cat) ? cat.GetString() ?? "console" : "console";
                var text = body.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                OutputReceived?.Invoke(this, (category, text));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        try { _process?.Kill(); } catch { }
        _process?.Dispose();
    }
}
