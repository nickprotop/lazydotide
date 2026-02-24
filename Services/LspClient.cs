using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetIDE;

public class LspClient : IAsyncDisposable
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "lazydotide-lsp.log");
    private static readonly object LogLock = new();

    private static void Log(string msg)
    {
        try { lock (LogLock) File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    private Process? _process;
    private StreamWriter? _stdin;
    private int _nextId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly ConcurrentDictionary<string, int> _docVersions = new();
    private Thread? _readThread;
    private volatile bool _disposed;
    private CancellationTokenSource _cts = new();

    // Debounce timer for DidChange
    private Timer? _changeDebounce;
    private string? _pendingChangePath;
    private string? _pendingChangeContent;

    public event EventHandler<(string Uri, List<LspDiagnostic> Diags)>? DiagnosticsReceived;

    public async Task<bool> StartAsync(LspServer server, string workspacePath)
    {
        try
        {
            var allArgs = new List<string>(server.BaseArgs);
            var psi = new ProcessStartInfo(server.Exe, allArgs)
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

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "LSP-read" };
            _readThread.Start();

            await SendInitializeAsync(workspacePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task SendInitializeAsync(string workspacePath)
    {
        var rootUri = PathToUri(workspacePath);
        var initParams = new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    publishDiagnostics = new { relatedInformation = false },
                    completion = new { completionItem = new { snippetSupport = false } },
                    hover = new { contentFormat = new[] { "plaintext" } },
                    definition = new { },
                    implementation = new { },
                    references = new { },
                    rename = new { prepareSupport = true },
                    codeAction = new { },
                    documentSymbol = new { },
                    signatureHelp = new { triggerCharacters = new[] { "(", "," } },
                    formatting = new { }
                },
                workspace = new { didChangeConfiguration = new { dynamicRegistration = false } }
            }
        };

        Log($"Sending initialize, workspace: {workspacePath}");
        var result = await SendRequestAsync("initialize", initParams, timeout: 15000);
        Log($"Initialize response: {(result.HasValue ? result.Value.ValueKind.ToString() : "null/timeout")}");
        SendNotification("initialized", new { });
    }

    public Task DidOpenAsync(string filePath, string content)
    {
        _docVersions[filePath] = 1;
        Log($"DidOpen: {filePath}");
        SendNotification("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri = PathToUri(filePath),
                languageId = GetLanguageId(filePath),
                version = 1,
                text = content
            }
        });
        return Task.CompletedTask;
    }

    public Task DidChangeAsync(string filePath, string content)
    {
        _pendingChangePath = filePath;
        _pendingChangeContent = content;
        _changeDebounce?.Dispose();
        _changeDebounce = new Timer(_ =>
        {
            var path = _pendingChangePath;
            var text = _pendingChangeContent;
            if (path != null && text != null)
                _ = SendDidChangeInternalAsync(path, text);
        }, null, 500, Timeout.Infinite);
        return Task.CompletedTask;
    }

    private Task SendDidChangeInternalAsync(string filePath, string content)
    {
        var version = _docVersions.AddOrUpdate(filePath, 2, (_, v) => v + 1);
        SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri = PathToUri(filePath), version },
            contentChanges = new[] { new { text = content } }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Immediately sends any pending debounced DidChange notification.
    /// Call this before requesting completions/hover to ensure the LSP has the latest content.
    /// </summary>
    public Task FlushPendingChangeAsync()
    {
        _changeDebounce?.Dispose();
        _changeDebounce = null;
        if (_pendingChangePath != null && _pendingChangeContent != null)
            return SendDidChangeInternalAsync(_pendingChangePath, _pendingChangeContent);
        return Task.CompletedTask;
    }

    public Task DidSaveAsync(string filePath)
    {
        SendNotification("textDocument/didSave", new
        {
            textDocument = new { uri = PathToUri(filePath) }
        });
        return Task.CompletedTask;
    }

    public Task DidCloseAsync(string filePath)
    {
        _docVersions.TryRemove(filePath, out _);
        SendNotification("textDocument/didClose", new
        {
            textDocument = new { uri = PathToUri(filePath) }
        });
        return Task.CompletedTask;
    }

    public async Task<HoverResult?> HoverAsync(string filePath, int line, int character)
    {
        try
        {
            var result = await SendRequestAsync("textDocument/hover", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            });

            if (result == null || result.Value.ValueKind == JsonValueKind.Null) return null;

            // Extract contents
            if (result.Value.TryGetProperty("contents", out var contents))
            {
                string text = contents.ValueKind switch
                {
                    JsonValueKind.String => contents.GetString() ?? "",
                    JsonValueKind.Object when contents.TryGetProperty("value", out var v) => v.GetString() ?? "",
                    JsonValueKind.Array => string.Join("\n", contents.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String
                            ? e.GetString()
                            : e.TryGetProperty("value", out var v) ? v.GetString() : null)
                        .Where(s => s != null)),
                    _ => ""
                };
                return new HoverResult(text);
            }
        }
        catch { }
        return null;
    }

    public async Task<List<CompletionItem>> CompletionAsync(string filePath, int line, int character)
    {
        var result = new List<CompletionItem>();
        try
        {
            Log($"Completion request: {filePath} {line}:{character}");
            var response = await SendRequestAsync("textDocument/completion", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            }, timeout: 20000);
            Log($"Completion response kind: {(response.HasValue ? response.Value.ValueKind.ToString() : "null")}");

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return result;

            JsonElement items;
            if (response.Value.ValueKind == JsonValueKind.Object &&
                response.Value.TryGetProperty("items", out items))
            {
                // CompletionList { isIncomplete, items: [...] }
            }
            else if (response.Value.ValueKind == JsonValueKind.Array)
            {
                items = response.Value;
            }
            else
            {
                return result;
            }

            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
                    var insertText = item.TryGetProperty("insertText", out var it) ? it.GetString() : null;
                    var kind = item.TryGetProperty("kind", out var k) ? k.GetInt32() : 1;
                    result.Add(new CompletionItem(label, detail, insertText, kind));
                }
            }
            Log($"Completion items returned: {result.Count}");
        }
        catch (Exception ex) { Log($"Completion exception: {ex.Message}"); }
        return result;
    }

    public async Task<List<LspLocation>> DefinitionAsync(string filePath, int line, int character)
    {
        var result = new List<LspLocation>();
        try
        {
            var response = await SendRequestAsync("textDocument/definition", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            });

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return result;

            // Result may be Location | Location[]
            if (response.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var loc in response.Value.EnumerateArray())
                    ParseLocation(loc, result);
            }
            else if (response.Value.ValueKind == JsonValueKind.Object)
            {
                ParseLocation(response.Value, result);
            }
        }
        catch { }
        return result;
    }

    private static void ParseLocation(JsonElement loc, List<LspLocation> result)
    {
        if (!loc.TryGetProperty("uri", out var uriEl)) return;
        var uri = uriEl.GetString() ?? "";
        var range = ParseRange(loc);
        result.Add(new LspLocation(uri, range));
    }

    public async Task<SignatureHelp?> SignatureHelpAsync(string filePath, int line, int character)
    {
        try
        {
            var response = await SendRequestAsync("textDocument/signatureHelp", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            });

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return null;
            if (response.Value.ValueKind != JsonValueKind.Object) return null;

            var activeSignature = response.Value.TryGetProperty("activeSignature", out var as_) ? as_.GetInt32() : 0;
            var activeParameter = response.Value.TryGetProperty("activeParameter", out var ap) ? ap.GetInt32() : 0;

            var signatures = new List<SignatureInfo>();
            if (response.Value.TryGetProperty("signatures", out var sigsEl))
            {
                foreach (var sig in sigsEl.EnumerateArray())
                {
                    var label = sig.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    var doc = sig.TryGetProperty("documentation", out var d)
                        ? d.ValueKind == JsonValueKind.String ? d.GetString()
                          : d.TryGetProperty("value", out var dv) ? dv.GetString() : null
                        : null;

                    var parameters = new List<ParameterInfo>();
                    if (sig.TryGetProperty("parameters", out var paramsEl))
                    {
                        foreach (var p in paramsEl.EnumerateArray())
                        {
                            var pLabel = p.TryGetProperty("label", out var pl) ? pl.GetString() ?? "" : "";
                            var pDoc = p.TryGetProperty("documentation", out var pd)
                                ? pd.ValueKind == JsonValueKind.String ? pd.GetString()
                                  : pd.TryGetProperty("value", out var pdv) ? pdv.GetString() : null
                                : null;
                            parameters.Add(new ParameterInfo(pLabel, pDoc));
                        }
                    }
                    signatures.Add(new SignatureInfo(label, doc, parameters));
                }
            }

            if (signatures.Count == 0) return null;
            return new SignatureHelp(signatures, activeSignature, activeParameter);
        }
        catch { }
        return null;
    }

    public async Task<List<TextEdit>> FormattingAsync(string filePath, int tabSize = 4, bool insertSpaces = true)
    {
        var result = new List<TextEdit>();
        try
        {
            var response = await SendRequestAsync("textDocument/formatting", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                options = new { tabSize, insertSpaces }
            }, timeout: 10000);

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return result;

            if (response.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var edit in response.Value.EnumerateArray())
                {
                    var range = ParseRange(edit);
                    var newText = edit.TryGetProperty("newText", out var nt) ? nt.GetString() ?? "" : "";
                    result.Add(new TextEdit(range, newText));
                }
            }
        }
        catch { }
        return result;
    }

    public async Task<List<LspLocation>> ReferencesAsync(string filePath, int line, int character, bool includeDeclaration = true)
    {
        var result = new List<LspLocation>();
        try
        {
            var response = await SendRequestAsync("textDocument/references", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character },
                context = new { includeDeclaration }
            }, timeout: 15000);

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return result;

            if (response.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var loc in response.Value.EnumerateArray())
                    ParseLocation(loc, result);
            }
        }
        catch { }
        return result;
    }

    public async Task<List<LspLocation>> ImplementationAsync(string filePath, int line, int character)
    {
        var result = new List<LspLocation>();
        try
        {
            var response = await SendRequestAsync("textDocument/implementation", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            });

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return result;

            if (response.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var loc in response.Value.EnumerateArray())
                    ParseLocation(loc, result);
            }
            else if (response.Value.ValueKind == JsonValueKind.Object)
            {
                ParseLocation(response.Value, result);
            }
        }
        catch { }
        return result;
    }

    public async Task<PrepareRenameResult?> PrepareRenameAsync(string filePath, int line, int character)
    {
        try
        {
            var response = await SendRequestAsync("textDocument/prepareRename", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            });

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return null;

            // Response may be { range, placeholder } or just a Range
            if (response.Value.TryGetProperty("placeholder", out var ph))
            {
                var range = ParseRange(response.Value);
                return new PrepareRenameResult(range, ph.GetString() ?? "");
            }
            else if (response.Value.TryGetProperty("start", out _))
            {
                // It's a plain Range — extract the text from the range
                var range = new LspRange(
                    ParsePosition(response.Value, "start"),
                    ParsePosition(response.Value, "end"));
                return new PrepareRenameResult(range, "");
            }
            else
            {
                var range = ParseRange(response.Value);
                return new PrepareRenameResult(range, "");
            }
        }
        catch { }
        return null;
    }

    public async Task<WorkspaceEdit?> RenameAsync(string filePath, int line, int character, string newName)
    {
        try
        {
            var response = await SendRequestAsync("textDocument/rename", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character },
                newName
            }, timeout: 10000);

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return null;

            return ParseWorkspaceEdit(response.Value);
        }
        catch { }
        return null;
    }

    public async Task<List<CodeAction>> CodeActionAsync(string filePath, int startLine, int startChar, int endLine, int endChar, List<LspDiagnostic>? diagnostics = null)
    {
        var result = new List<CodeAction>();
        try
        {
            var diagParams = diagnostics?.Select(d => new
            {
                range = new
                {
                    start = new { line = d.Range.Start.Line, character = d.Range.Start.Character },
                    end = new { line = d.Range.End.Line, character = d.Range.End.Character }
                },
                message = d.Message,
                severity = d.Severity,
                code = d.Code
            }).ToArray() ?? Array.Empty<object>();

            var response = await SendRequestAsync("textDocument/codeAction", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                range = new
                {
                    start = new { line = startLine, character = startChar },
                    end = new { line = endLine, character = endChar }
                },
                context = new { diagnostics = diagParams }
            }, timeout: 10000);

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return result;

            if (response.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in response.Value.EnumerateArray())
                {
                    var title = action.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var kind = action.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    WorkspaceEdit? edit = null;
                    if (action.TryGetProperty("edit", out var editEl))
                        edit = ParseWorkspaceEdit(editEl);
                    result.Add(new CodeAction(title, kind, edit, null));
                }
            }
        }
        catch { }
        return result;
    }

    public async Task<List<DocumentSymbol>> DocumentSymbolAsync(string filePath)
    {
        var result = new List<DocumentSymbol>();
        try
        {
            var response = await SendRequestAsync("textDocument/documentSymbol", new
            {
                textDocument = new { uri = PathToUri(filePath) }
            }, timeout: 10000);

            if (response == null || response.Value.ValueKind == JsonValueKind.Null) return result;

            if (response.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var sym in response.Value.EnumerateArray())
                    result.Add(ParseDocumentSymbol(sym));
            }
        }
        catch { }
        return result;
    }

    private static DocumentSymbol ParseDocumentSymbol(JsonElement el)
    {
        var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var kind = el.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
        var range = ParseRange(el);
        var selectionRange = el.TryGetProperty("selectionRange", out var sr)
            ? new LspRange(ParsePosition(sr, "start"), ParsePosition(sr, "end"))
            : range;

        List<DocumentSymbol>? children = null;
        if (el.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
        {
            children = new List<DocumentSymbol>();
            foreach (var child in ch.EnumerateArray())
                children.Add(ParseDocumentSymbol(child));
        }

        return new DocumentSymbol(name, kind, range, selectionRange, children);
    }

    private static WorkspaceEdit? ParseWorkspaceEdit(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var changes = new Dictionary<string, List<TextEdit>>();

        if (el.TryGetProperty("changes", out var changesEl) && changesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in changesEl.EnumerateObject())
            {
                var edits = new List<TextEdit>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var edit in prop.Value.EnumerateArray())
                    {
                        var range = ParseRange(edit);
                        var newText = edit.TryGetProperty("newText", out var nt) ? nt.GetString() ?? "" : "";
                        edits.Add(new TextEdit(range, newText));
                    }
                }
                changes[prop.Name] = edits;
            }
        }

        return new WorkspaceEdit(changes.Count > 0 ? changes : null);
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        try
        {
            _changeDebounce?.Dispose();
            await SendRequestAsync("shutdown", null, timeout: 2000);
            SendNotification("exit", null);
        }
        catch { }
    }

    private async Task<JsonElement?> SendRequestAsync(string method, object? @params, int timeout = 5000)
    {
        if (_stdin == null || _disposed) return null;

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        SendRaw(request);

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetResult(null));

        return await tcs.Task;
    }

    private void SendNotification(string method, object? @params)
    {
        if (_stdin == null || _disposed) return;
        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });
        SendRaw(msg);
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
            catch { }
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
                // Read headers
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
        catch { }
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

            // Check if it's a response (has "id" and "result"/"error")
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            {
                var id = idEl.GetInt32();
                if (_pending.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("result", out var result))
                        tcs.TrySetResult(result.Clone());
                    else
                        tcs.TrySetResult(null);
                }
                return;
            }

            // Notification
            if (root.TryGetProperty("method", out var methodEl))
            {
                var method = methodEl.GetString();
                if (method == "textDocument/publishDiagnostics")
                {
                    HandlePublishDiagnostics(root);
                }
            }
        }
        catch { }
    }

    private void HandlePublishDiagnostics(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("params", out var @params)) return;
            if (!@params.TryGetProperty("uri", out var uriEl)) return;
            var uri = uriEl.GetString() ?? "";

            var diags = new List<LspDiagnostic>();
            if (@params.TryGetProperty("diagnostics", out var diagsEl))
            {
                foreach (var d in diagsEl.EnumerateArray())
                {
                    var range = ParseRange(d);
                    var message = d.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    var severity = d.TryGetProperty("severity", out var s) ? s.GetInt32() : 1;
                    var code = d.TryGetProperty("code", out var c) ? c.ToString() : null;
                    diags.Add(new LspDiagnostic(range, message, severity, code));
                }
            }
            DiagnosticsReceived?.Invoke(this, (uri, diags));
        }
        catch { }
    }

    private static LspRange ParseRange(JsonElement element)
    {
        if (!element.TryGetProperty("range", out var range))
            return new LspRange(new LspPosition(0, 0), new LspPosition(0, 0));

        var start = ParsePosition(range, "start");
        var end = ParsePosition(range, "end");
        return new LspRange(start, end);
    }

    private static LspPosition ParsePosition(JsonElement range, string key)
    {
        if (!range.TryGetProperty(key, out var pos)) return new LspPosition(0, 0);
        var line = pos.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
        var ch = pos.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
        return new LspPosition(line, ch);
    }

    private static string PathToUri(string path)
    {
        path = Path.GetFullPath(path).Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        return "file://" + Uri.EscapeDataString(path).Replace("%2F", "/");
    }

    public static string UriToPath(string uri)
    {
        if (uri.StartsWith("file://"))
            return Uri.UnescapeDataString(uri["file://".Length..]);
        return uri;
    }

    private static string GetLanguageId(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".fs" => "fsharp",
            ".vb" => "vb",
            ".json" => "json",
            ".xml" => "xml",
            ".md" => "markdown",
            _ => "plaintext"
        };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _changeDebounce?.Dispose();
        await ShutdownAsync();
        _cts.Cancel();
        try { _process?.Kill(); } catch { }
        _process?.Dispose();
    }
}
