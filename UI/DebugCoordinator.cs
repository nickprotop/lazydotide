using System.Collections.Concurrent;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using TreeNode = SharpConsoleUI.Controls.TreeNode;
using Color = SharpConsoleUI.Color;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

internal class DebugCoordinator : IAsyncDisposable
{
    private static readonly Color BreakpointLineBg = Color.FromInt32(52);   // dark red-ish
    private static readonly Color StoppedLineBg = Color.FromInt32(58);      // dark yellow-ish

    private static void Log(string msg) => DiagnosticLog.Write("debug", msg);

    private readonly AppContext _ctx;

    private DapClient? _client;
    private DebugSessionState _state = DebugSessionState.Idle;
    private int _stoppedThreadId;
    private int _stoppedFrameId;

    // Breakpoint state: filePath → set of 0-based line indices
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new();

    // DAP detection
    private DapServer? _detectedServer;
    private bool _detectionDone;
    private bool _notifiedMissingDebugger;

    // Debug UI tabs (created on first debug start)
    private TreeControl? _variablesTree;
    private ListControl? _callStackList;
    private ScrollablePanelControl? _debugConsole;
    private MarkupControl? _debugConsoleMarkup;
    private readonly List<string> _debugConsoleLines = new();
    private ToolbarControl? _debugToolbar;

    // Variable node metadata for lazy expansion
    private record VariableNodeTag(int VariablesReference, bool ChildrenLoaded, string Name = "", string Value = "", string? Type = null);

    // Events
    public event Action? StateChanged;

    // Properties
    public DebugSessionState State => _state;
    public bool DapDetectionDone => _detectionDone;
    public string? DetectedDapExe => _detectedServer?.Exe;

    public DebugCoordinator(AppContext ctx)
    {
        _ctx = ctx;
    }

    // ── Detection ──

    public void DetectDap()
    {
        Task.Run(() =>
        {
            _detectedServer = DapDetector.Find();
            _detectionDone = true;
            Log($"DAP detection: {(_detectedServer != null ? _detectedServer.Exe : "not found")}");
            _ctx.PendingUiActions.Enqueue(() => StateChanged?.Invoke());
        });
    }

    public bool HasDebugger => _detectedServer != null;

    public void ReDetectDap()
    {
        _detectedServer = DapDetector.Find();
        _detectionDone = true;
        _notifiedMissingDebugger = false;
        Log($"DAP re-detection: {(_detectedServer != null ? _detectedServer.Exe : "not found")}");
        StateChanged?.Invoke();
    }

    // ── Breakpoint Management ──

    public void ToggleBreakpoint(string filePath, int line0Based)
    {
        if (!_breakpoints.TryGetValue(filePath, out var lines))
        {
            lines = new HashSet<int>();
            _breakpoints[filePath] = lines;
        }

        if (!lines.Remove(line0Based))
            lines.Add(line0Based);

        // Update gutter
        var gutter = _ctx.EditorManager.GetBpGutterByPath(filePath);
        if (gutter != null)
        {
            if (lines.Contains(line0Based))
                gutter.SetBreakpoint(line0Based);
            else
                gutter.ClearBreakpoint(line0Based);
        }

        // Update line highlight
        var editor = _ctx.EditorManager.GetEditorByPath(filePath);
        if (editor != null)
        {
            if (lines.Contains(line0Based))
                editor.SetLineHighlight(line0Based, BreakpointLineBg);
            else
                editor.SetLineHighlight(line0Based, null);
        }

        // Sync to DAP if session active
        if (_client != null && _state != DebugSessionState.Idle)
            _ = SyncBreakpointsToDapAsync(filePath);
    }

    public void ToggleBreakpointAtCursor()
    {
        var filePath = _ctx.EditorManager.CurrentFilePath;
        var editor = _ctx.EditorManager.CurrentEditor;
        if (filePath == null || editor == null) return;
        ToggleBreakpoint(filePath, editor.CurrentLine - 1); // CurrentLine is 1-based
    }

    public void RegisterGutter(string filePath)
    {
        var gutter = _ctx.EditorManager.GetBpGutterByPath(filePath);
        var editor = _ctx.EditorManager.GetEditorByPath(filePath);
        if (gutter == null) return;

        // Apply existing breakpoints to newly opened gutter
        if (_breakpoints.TryGetValue(filePath, out var lines))
        {
            foreach (var line in lines)
            {
                gutter.SetBreakpoint(line);
                editor?.SetLineHighlight(line, BreakpointLineBg);
            }
        }

        // Apply stopped line if applicable
        if (_state == DebugSessionState.Paused && _stoppedFilePath == filePath && _stoppedLine >= 0)
        {
            gutter.SetStoppedLine(_stoppedLine);
            editor?.SetLineHighlight(_stoppedLine, StoppedLineBg);
        }
    }

    public void UnregisterGutter(string filePath)
    {
        // Nothing to clean up — the gutter/editor are being disposed with the tab
    }

    // Workspace persistence
    public void LoadBreakpoints(WorkspaceState state, WorkspaceService workspaceService)
    {
        _breakpoints.Clear();
        foreach (var bp in state.Breakpoints)
        {
            var absPath = workspaceService.ToAbsolutePath(bp.Path);
            if (!_breakpoints.TryGetValue(absPath, out var lines))
            {
                lines = new HashSet<int>();
                _breakpoints[absPath] = lines;
            }
            lines.Add(bp.Line - 1); // workspace stores 1-based
        }
    }

    public void SaveBreakpoints(WorkspaceState state, WorkspaceService workspaceService)
    {
        state.Breakpoints.Clear();
        foreach (var (filePath, lines) in _breakpoints)
        {
            foreach (var line in lines.OrderBy(l => l))
            {
                state.Breakpoints.Add(new WorkspaceBreakpoint
                {
                    Path = workspaceService.ToRelativePath(filePath),
                    Line = line + 1 // store as 1-based
                });
            }
        }
    }

    // ── Session Lifecycle ──

    private string? _stoppedFilePath;
    private int _stoppedLine = -1;

    public async Task StartDebuggingAsync(BuildService buildService, ConcurrentQueue<string> buildLines, CancellationToken ct, LaunchProfileEntry? launchProfile = null)
    {
        if (_state != DebugSessionState.Idle) return;
        if (_detectedServer == null) return;

        // Build first
        var target = _ctx.ProjectService.FindBuildTarget();
        if (target == null)
        {
            Log("No build target found");
            return;
        }

        _ctx.PendingUiActions.Enqueue(() =>
        {
            _ctx.OutputPanel.ClearBuildOutput();
            _ctx.OutputPanel.SwitchToBuildTab();
        });

        var buildResult = await buildService.BuildAsync(target, line => buildLines.Enqueue(line), ct);
        if (!buildResult.Success)
        {
            Log("Build failed, aborting debug");
            _ctx.PendingUiActions.Enqueue(() => _ctx.OutputPanel.PopulateProblems(buildResult.Diagnostics));
            return;
        }

        // Find the DLL to launch
        var runTarget = _ctx.ProjectService.FindRunTarget() ?? target;
        var program = GetDllPath(runTarget);
        if (program == null)
        {
            Log($"Could not determine DLL path for {runTarget}");
            return;
        }

        var cwd = launchProfile?.WorkingDirectory ?? Path.GetDirectoryName(runTarget);

        // Build launch profile for DAP
        var profile = new LaunchProfile(
            Name: Path.GetFileNameWithoutExtension(runTarget),
            Program: program,
            Cwd: cwd,
            Args: launchProfile?.Args,
            Env: launchProfile?.Env?.ToDictionary(kv => kv.Key, kv => kv.Value)
        );

        // Start DAP client (launch mode — attach mode doesn't support breakpoints in netcoredbg)
        _client = new DapClient();
        _client.Stopped += OnStopped;
        _client.Continued += OnContinued;
        _client.Terminated += OnTerminated;
        _client.Exited += OnExited;
        _client.OutputReceived += OnOutput;
        _client.Initialized += OnInitialized;

        if (!await _client.StartAsync(_detectedServer))
        {
            Log("Failed to start DAP process");
            _client = null;
            return;
        }

        if (!await _client.InitializeAsync())
        {
            Log("DAP initialize failed");
            await _client.DisposeAsync();
            _client = null;
            return;
        }

        if (!await _client.LaunchAsync(profile))
        {
            Log("DAP launch failed");
            await _client.DisposeAsync();
            _client = null;
            return;
        }

        // Send breakpoints after launch but before configurationDone
        await SendAllBreakpointsAsync();
        await _client.SendConfigurationDone();

        SetState(DebugSessionState.Running);
        _ctx.PendingUiActions.Enqueue(() =>
        {
            EnsureDebugTabs();
            EnsureDebugToolbar();
            UpdateToolbarState();
            AppendDebugConsole("[green]Debug session started[/]");
        });
    }

    public async Task ContinueAsync()
    {
        if (_client == null || _state != DebugSessionState.Paused) return;
        await _client.ContinueAsync(_stoppedThreadId);
    }

    public async Task StepOverAsync()
    {
        if (_client == null || _state != DebugSessionState.Paused) return;
        await _client.NextAsync(_stoppedThreadId);
    }

    public async Task StepIntoAsync()
    {
        if (_client == null || _state != DebugSessionState.Paused) return;
        await _client.StepInAsync(_stoppedThreadId);
    }

    public async Task StepOutAsync()
    {
        if (_client == null || _state != DebugSessionState.Paused) return;
        await _client.StepOutAsync(_stoppedThreadId);
    }

    public async Task PauseAsync()
    {
        if (_client == null || _state != DebugSessionState.Running) return;
        await _client.PauseAsync(_stoppedThreadId);
    }

    public async Task StopDebuggingAsync()
    {
        if (_client == null) return;
        await _client.DisconnectAsync();
        await CleanupSessionAsync();
    }

    // ── DAP Event Handlers ──

    private void OnInitialized(object? sender, EventArgs e) { }

    private void OnStopped(object? sender, DapStoppedEventArgs e)
    {
        _stoppedThreadId = e.ThreadId;
        SetState(DebugSessionState.Paused);

        _ctx.PendingUiActions.Enqueue(() =>
        {
            UpdateToolbarState();
            AppendDebugConsole($"[yellow]Paused: {MarkupParser.Escape(e.Reason)}[/]" +
                (e.Description != null ? $" — {MarkupParser.Escape(e.Description)}" : ""));
        });

        // Fetch stack trace and variables
        _ = Task.Run(async () =>
        {
            try
            {
                var frames = await _client!.StackTraceAsync(e.ThreadId);
                if (frames.Count > 0)
                {
                    _stoppedFrameId = frames[0].Id;
                    var topFrame = frames[0];

                    // Navigate to stopped location
                    if (topFrame.Source?.Path != null)
                    {
                        _ctx.PendingUiActions.Enqueue(() =>
                        {
                            ClearStoppedIndicators();
                            _stoppedFilePath = topFrame.Source.Path;
                            _stoppedLine = topFrame.Line - 1; // DAP is 1-based

                            _ctx.EditorManager.OpenFile(topFrame.Source.Path);
                            _ctx.EditorManager.GoToLine(topFrame.Line);

                            // Set stopped indicators
                            var gutter = _ctx.EditorManager.GetBpGutterByPath(topFrame.Source.Path);
                            gutter?.SetStoppedLine(_stoppedLine);

                            var editor = _ctx.EditorManager.GetEditorByPath(topFrame.Source.Path);
                            editor?.SetLineHighlight(_stoppedLine, StoppedLineBg);
                        });
                    }

                    // Update call stack
                    _ctx.PendingUiActions.Enqueue(() => UpdateCallStack(frames));

                    // Fetch scopes & variables for top frame
                    await FetchAndDisplayVariables(topFrame.Id);
                }
            }
            catch (Exception ex) { Log($"OnStopped fetch: {ex.Message}"); }
        });
    }

    private void OnContinued(object? sender, EventArgs e)
    {
        SetState(DebugSessionState.Running);
        _ctx.PendingUiActions.Enqueue(() =>
        {
            ClearStoppedIndicators();
            UpdateToolbarState();
        });
    }

    private void OnTerminated(object? sender, EventArgs e)
    {
        _ctx.PendingUiActions.Enqueue(() =>
        {
            AppendDebugConsole("[dim]Debug session terminated[/]");
            _ = CleanupSessionAsync();
        });
    }

    private void OnExited(object? sender, int exitCode)
    {
        _ctx.PendingUiActions.Enqueue(() =>
        {
            AppendDebugConsole($"[dim]Process exited with code {exitCode}[/]");
            _ = CleanupSessionAsync();
        });
    }

    private void OnOutput(object? sender, (string Category, string Text) e)
    {
        var escaped = MarkupParser.Escape(e.Text.TrimEnd('\n', '\r'));
        if (string.IsNullOrEmpty(escaped)) return;

        var formatted = e.Category switch
        {
            "stderr" => $"[red]{escaped}[/]",
            "stdout" or "console" => escaped,
            _ => $"[dim]{escaped}[/]"
        };

        _ctx.PendingUiActions.Enqueue(() => AppendDebugConsole(formatted));
    }

    // ── Internal Helpers ──

    private void SetState(DebugSessionState newState)
    {
        _state = newState;
        _ctx.PendingUiActions.Enqueue(() => StateChanged?.Invoke());
    }

    private async Task CleanupSessionAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        ClearStoppedIndicators();
        _state = DebugSessionState.Idle;

        _ctx.PendingUiActions.Enqueue(() =>
        {
            UpdateToolbarState();
            StateChanged?.Invoke();
        });
    }

    private void ClearStoppedIndicators()
    {
        if (_stoppedFilePath != null)
        {
            var gutter = _ctx.EditorManager.GetBpGutterByPath(_stoppedFilePath);
            gutter?.ClearStoppedLine();

            var editor = _ctx.EditorManager.GetEditorByPath(_stoppedFilePath);
            if (editor != null && _stoppedLine >= 0)
            {
                // Restore breakpoint highlight if there's a breakpoint on this line, otherwise clear
                if (_breakpoints.TryGetValue(_stoppedFilePath, out var bps) && bps.Contains(_stoppedLine))
                    editor.SetLineHighlight(_stoppedLine, BreakpointLineBg);
                else
                    editor.SetLineHighlight(_stoppedLine, null);
            }
        }
        _stoppedFilePath = null;
        _stoppedLine = -1;
    }

    private async Task SendAllBreakpointsAsync()
    {
        if (_client == null) return;
        foreach (var (filePath, lines) in _breakpoints)
        {
            if (lines.Count == 0) continue;
            var bps = lines.Select(l => new SourceBreakpoint(l + 1)).ToList(); // DAP is 1-based
            await _client.SetBreakpointsAsync(filePath, bps);
        }
    }

    private async Task SyncBreakpointsToDapAsync(string filePath)
    {
        if (_client == null) return;
        var lines = _breakpoints.TryGetValue(filePath, out var set) ? set : new HashSet<int>();
        var bps = lines.Select(l => new SourceBreakpoint(l + 1)).ToList();
        await _client.SetBreakpointsAsync(filePath, bps);
    }

    private static string? GetDllPath(string csprojPath)
    {
        var dir = Path.GetDirectoryName(csprojPath);
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        if (dir == null || name == null) return null;

        string? bestPath = null;
        DateTime bestTime = default;

        // Try Debug then Release
        foreach (var config in new[] { "Debug", "Release" })
        {
            var binDir = Path.Combine(dir, "bin", config);
            if (!Directory.Exists(binDir)) continue;

            try
            {
                // Look in TFM subdirectories and RID subdirectories (e.g. net10.0/linux-x64/)
                foreach (var tfmDir in Directory.GetDirectories(binDir))
                {
                    CheckDll(Path.Combine(tfmDir, name + ".dll"), ref bestPath, ref bestTime);

                    foreach (var ridDir in Directory.GetDirectories(tfmDir))
                        CheckDll(Path.Combine(ridDir, name + ".dll"), ref bestPath, ref bestTime);
                }
            }
            catch { }
        }
        return bestPath;

        static void CheckDll(string path, ref string? best, ref DateTime bestTime)
        {
            if (!File.Exists(path)) return;
            var time = File.GetLastWriteTimeUtc(path);
            if (best == null || time > bestTime)
            {
                best = path;
                bestTime = time;
            }
        }
    }

    // ── Debug UI Tabs ──

    private void EnsureDebugTabs()
    {
        EnsureVariablesTab();
        EnsureCallStackTab();
        EnsureDebugConsoleTab();
    }

    public void ShowVariablesTab()
    {
        EnsureVariablesTab();
        _ctx.SidePanel.TabControl.SwitchToTab("Variables");
    }

    public void ShowCallStackTab()
    {
        EnsureCallStackTab();
        _ctx.SidePanel.TabControl.SwitchToTab("Call Stack");
    }

    public void ShowDebugConsoleTab()
    {
        EnsureDebugConsoleTab();
        _ctx.OutputPanel.TabControl.SwitchToTab("Debug Console");
    }

    private void EnsureVariablesTab()
    {
        if (_ctx.SidePanel.TabControl.HasTab("Variables")) return;

        _variablesTree = new TreeControl
        {
            Guide = TreeGuide.Line,
            HighlightBackgroundColor = Color.SteelBlue,
            HighlightForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _variablesTree.NodeExpandCollapse += OnVariableNodeExpandCollapse;
        _variablesTree.MouseDoubleClick += OnVariableNodeDoubleClick;
        _ctx.MainWindow.KeyPressed += OnVariablesTreeKeyPressed;
        var varsPanel = new ScrollablePanelControl
        {
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        varsPanel.AddControl(_variablesTree);
        _ctx.SidePanel.TabControl.AddTab("Variables", varsPanel, isClosable: true);
    }

    private void EnsureCallStackTab()
    {
        if (_ctx.SidePanel.TabControl.HasTab("Call Stack")) return;

        _callStackList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HoverHighlightsItems = true
        };
        _callStackList.ItemActivated += (_, item) =>
        {
            if (item?.Tag is DapStackFrame frame)
            {
                if (frame.Source?.Path != null)
                {
                    _ctx.EditorManager.OpenFile(frame.Source.Path);
                    _ctx.EditorManager.GoToLine(frame.Line);
                }
                if (_state == DebugSessionState.Paused && frame.Id != _stoppedFrameId)
                {
                    _stoppedFrameId = frame.Id;
                    _ = Task.Run(async () =>
                    {
                        try { await FetchAndDisplayVariables(frame.Id); }
                        catch (Exception ex) { Log($"Frame switch variables: {ex.Message}"); }
                    });
                }
            }
        };
        var stackPanel = new ScrollablePanelControl
        {
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        stackPanel.AddControl(_callStackList);
        _ctx.SidePanel.TabControl.AddTab("Call Stack", stackPanel, isClosable: true);
    }

    private void EnsureDebugConsoleTab()
    {
        if (_ctx.OutputPanel.TabControl.HasTab("Debug Console")) return;

        _debugConsoleMarkup = new MarkupControl(new List<string>(_debugConsoleLines))
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        _debugConsole = new ScrollablePanelControl
        {
            AutoScroll = true,
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _debugConsole.AddControl(_debugConsoleMarkup);
        _ctx.OutputPanel.TabControl.AddTab("Debug Console", _debugConsole, isClosable: true);
    }

    private void EnsureDebugToolbar()
    {
        if (_debugToolbar != null) return;

        _debugToolbar = ToolbarControl.Create()
            .AddButton("▶ Continue", (_, _) => _ = ContinueAsync())
            .AddButton("⏸ Pause", (_, _) => _ = PauseAsync())
            .AddButton("⏭ Step Over", (_, _) => _ = StepOverAsync())
            .AddButton("⏬ Step Into", (_, _) => _ = StepIntoAsync())
            .AddButton("⏫ Step Out", (_, _) => _ = StepOutAsync())
            .AddButton("⏹ Stop", (_, _) => _ = StopDebuggingAsync())
            .WithSpacing(1)
            .WithWrap(true)
            .StickyTop()
            .Build();

        _ctx.MainWindow.InsertControl(3, _debugToolbar); // After the existing toolbar
    }

    private void UpdateToolbarState()
    {
        if (_debugToolbar == null) return;
        _debugToolbar.Visible = _state != DebugSessionState.Idle;
    }

    private async Task FetchAndDisplayVariables(int frameId)
    {
        var scopes = await _client!.ScopesAsync(frameId);
        var allVars = new List<(DapScope Scope, List<DapVariable> Variables)>();
        foreach (var scope in scopes)
        {
            if (scope.Expensive) continue;
            var vars = await _client!.VariablesAsync(scope.VariablesReference);
            allVars.Add((scope, vars));
        }
        _ctx.PendingUiActions.Enqueue(() => UpdateVariables(allVars));
    }

    private static string FormatVariableNode(DapVariable v)
    {
        var escapedName = MarkupParser.Escape(v.Name);
        var escapedValue = MarkupParser.Escape(v.Value);

        if (v.VariablesReference > 0)
        {
            // Expandable object: name (Type) = shortPreview
            var typeStr = v.Type != null ? $" [dim]({MarkupParser.Escape(v.Type)})[/]" : "";
            var preview = escapedValue.Length > 20 ? escapedValue[..20] + "..." : escapedValue;
            return $"[cyan1]{escapedName}[/]{typeStr} = {preview}";
        }
        else
        {
            // Leaf: name = truncatedValue (Type)
            var typeStr = v.Type != null ? $" [dim]({MarkupParser.Escape(v.Type)})[/]" : "";
            var display = escapedValue.Length > 30 ? escapedValue[..30] + "..." : escapedValue;
            return $"[cyan1]{escapedName}[/] = {display}{typeStr}";
        }
    }

    private void UpdateVariables(List<(DapScope Scope, List<DapVariable> Variables)> scopeData)
    {
        if (_variablesTree == null) return;

        // Collect previously expanded variable name paths (e.g. "Locals/myObj", "Locals/myObj/Items")
        var expandedPaths = new HashSet<string>();
        foreach (var root in _variablesTree.RootNodes)
            CollectExpandedVarPaths(root.Children, root.Text, expandedPaths);

        _variablesTree.Clear();

        // Nodes that need async child expansion to restore previous state
        var toExpand = new List<(TreeNode Node, int VarRef, string Path)>();

        foreach (var (scope, vars) in scopeData)
        {
            var scopeNode = _variablesTree.AddRootNode($"[bold]{MarkupParser.Escape(scope.Name)}[/]");
            var scopePath = scopeNode.Text;
            foreach (var v in vars)
            {
                var child = scopeNode.AddChild(FormatVariableNode(v));
                child.Tag = new VariableNodeTag(v.VariablesReference, false, v.Name, v.Value, v.Type);
                if (v.VariablesReference > 0)
                {
                    if (expandedPaths.Contains($"{scopePath}/{v.Name}"))
                        toExpand.Add((child, v.VariablesReference, $"{scopePath}/{v.Name}"));
                    else
                    {
                        child.AddChild("[dim]Loading...[/]");
                        child.IsExpanded = false;
                    }
                }
            }
        }

        // Re-expand previously expanded nodes
        if (toExpand.Count > 0)
            _ = RestoreExpandedVariables(toExpand, expandedPaths);
    }

    private static void CollectExpandedVarPaths(IEnumerable<TreeNode> nodes, string parentPath, HashSet<string> paths)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is VariableNodeTag { ChildrenLoaded: true } && node.IsExpanded)
            {
                var name = ExtractVariableName(node);
                var path = $"{parentPath}/{name}";
                paths.Add(path);
                CollectExpandedVarPaths(node.Children, path, paths);
            }
        }
    }

    private static string ExtractVariableName(TreeNode node)
    {
        // Prefer the Name stored in the tag
        if (node.Tag is VariableNodeTag { Name: { Length: > 0 } name })
            return name;

        // Fallback: parse between [cyan1] and [/]
        var text = node.Text;
        const string start = "[cyan1]";
        const string end = "[/]";
        var i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return text;
        i += start.Length;
        var j = text.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? text[i..] : text[i..j];
    }

    private async Task RestoreExpandedVariables(List<(TreeNode Node, int VarRef, string Path)> toExpand, HashSet<string> expandedPaths)
    {
        try
        {
            // Fetch all children in parallel on background thread
            var fetched = new List<(TreeNode Node, int VarRef, string Path, List<DapVariable> Children)>();
            foreach (var (node, varRef, path) in toExpand)
            {
                var children = await _client!.VariablesAsync(varRef);
                fetched.Add((node, varRef, path, children));
            }

            // Apply to tree and collect next level — all synchronously via UI queue
            var nextLevel = new List<(TreeNode Node, int VarRef, string Path)>();
            var tcs = new TaskCompletionSource();
            _ctx.PendingUiActions.Enqueue(() =>
            {
                foreach (var (node, varRef, path, children) in fetched)
                {
                    foreach (var v in children)
                    {
                        var child = node.AddChild(FormatVariableNode(v));
                        child.Tag = new VariableNodeTag(v.VariablesReference, false, v.Name, v.Value, v.Type);
                        if (v.VariablesReference > 0)
                        {
                            if (expandedPaths.Contains($"{path}/{v.Name}"))
                                nextLevel.Add((child, v.VariablesReference, $"{path}/{v.Name}"));
                            else
                            {
                                child.AddChild("[dim]Loading...[/]");
                                child.IsExpanded = false;
                            }
                        }
                    }
                    node.Tag = node.Tag is VariableNodeTag nt
                        ? nt with { VariablesReference = varRef, ChildrenLoaded = true }
                        : new VariableNodeTag(varRef, true);
                    node.IsExpanded = true;
                }
                tcs.SetResult();
            });

            await tcs.Task;

            if (nextLevel.Count > 0)
                await RestoreExpandedVariables(nextLevel, expandedPaths);
        }
        catch (Exception ex) { Log($"RestoreExpandedVariables: {ex.Message}"); }
    }

    private void OnVariableNodeDoubleClick(object? sender, MouseEventArgs e)
    {
        var node = _variablesTree?.SelectedNode;
        if (node?.Tag is not VariableNodeTag tag) return;

        // For expandable nodes, the tree already toggled expand/collapse — undo it
        if (tag.VariablesReference > 0)
            node.IsExpanded = !node.IsExpanded;

        OpenVariableInspector(tag);
    }

    private void OnVariablesTreeKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        // Enter on variables tree opens inspector (tree ignores Enter with Ctrl modifier,
        // so we catch Ctrl+Enter here; plain Enter is consumed by the tree for expand/collapse)
        if (e.KeyInfo is { Key: ConsoleKey.Enter, Modifiers: ConsoleModifiers.Control }
            && _variablesTree != null && _variablesTree.HasFocus
            && _variablesTree.SelectedNode?.Tag is VariableNodeTag tag)
        {
            OpenVariableInspector(tag);
            e.Handled = true;
        }
    }

    private void OpenVariableInspector(VariableNodeTag tag)
    {
        Func<int, Task<List<DapVariable>>> fetchChildren = varRef =>
            _client != null ? _client.VariablesAsync(varRef) : Task.FromResult(new List<DapVariable>());

        _ = VariableInspectorDialog.ShowAsync(tag.Name, tag.Value, tag.Type, tag.VariablesReference, fetchChildren, _ctx.WindowSystem);
    }

    private void OnVariableNodeExpandCollapse(object? sender, TreeNodeEventArgs args)
    {
        var node = args.Node;
        if (node?.Tag is not VariableNodeTag tag) return;

        // Already loaded or not expandable — let TreeControl handle normally
        if (tag.ChildrenLoaded || tag.VariablesReference <= 0) return;

        // Only act on expand (not collapse)
        if (!node.IsExpanded) return;

        var varRef = tag.VariablesReference;
        _ = Task.Run(async () =>
        {
            try
            {
                var children = await _client!.VariablesAsync(varRef);
                _ctx.PendingUiActions.Enqueue(() =>
                {
                    // Clear placeholder and add real children
                    node.ClearChildren();

                    foreach (var v in children)
                    {
                        var child = node.AddChild(FormatVariableNode(v));
                        child.Tag = new VariableNodeTag(v.VariablesReference, false, v.Name, v.Value, v.Type);
                        if (v.VariablesReference > 0)
                        {
                            child.AddChild("[dim]Loading...[/]");
                            child.IsExpanded = false;
                        }
                    }
                    node.Tag = tag with { ChildrenLoaded = true };

                    // Force tree to pick up the new children
                });
            }
            catch (Exception ex) { Log($"Variable expand: {ex.Message}"); }
        });
    }

    private void UpdateCallStack(List<DapStackFrame> frames)
    {
        if (_callStackList == null) return;
        _callStackList.ClearItems();
        foreach (var f in frames)
        {
            var location = f.Source?.Name != null ? $"{f.Source.Name}:{f.Line}" : "";
            var item = new ListItem($"{MarkupParser.Escape(f.Name)}  [dim]{MarkupParser.Escape(location)}[/]")
            {
                Tag = f
            };
            _callStackList.AddItem(item);
        }
    }

    private void AppendDebugConsole(string markup)
    {
        _debugConsoleLines.Add(markup);
        _debugConsoleMarkup?.SetContent(new List<string>(_debugConsoleLines));
    }

    // ── F5 Logic (called from IdeApp) ──

    public bool ShouldFallbackToRun()
    {
        return _detectedServer == null;
    }

    public bool ShowMissingDebuggerNotification()
    {
        if (_notifiedMissingDebugger) return false;
        _notifiedMissingDebugger = true;
        return true;
    }

    // ── Dispose ──

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
