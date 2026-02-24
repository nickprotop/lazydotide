namespace DotNetIDE;

// LSP 2.0 minimal types (zero-based positions)
public record LspPosition(int Line, int Character);
public record LspRange(LspPosition Start, LspPosition End);
public record LspLocation(string Uri, LspRange Range);

public record LspDiagnostic(
    LspRange Range,
    string Message,
    int Severity,   // 1=Error, 2=Warning, 3=Information, 4=Hint
    string? Code);

public record CompletionItem(
    string Label,
    string? Detail,
    string? InsertText,
    int Kind);   // 1=Text, 2=Method, 3=Function, 4=Constructor, 5=Field, 6=Variable, 7=Class, ...

public record HoverResult(string Contents);

public record TextEdit(LspRange Range, string NewText);

public record SignatureHelp(
    List<SignatureInfo> Signatures,
    int ActiveSignature,
    int ActiveParameter);

public record SignatureInfo(
    string Label,
    string? Documentation,
    List<ParameterInfo> Parameters);

public record ParameterInfo(string Label, string? Documentation);

// Rename / Code Action / Document Symbol types
public record WorkspaceEdit(Dictionary<string, List<TextEdit>>? Changes);

public record CodeAction(
    string Title,
    string? Kind,
    WorkspaceEdit? Edit,
    List<LspDiagnostic>? Diagnostics);

public record DocumentSymbol(
    string Name,
    int Kind,          // SymbolKind: 5=Class, 6=Method, 7=Property, 8=Field, ...
    LspRange Range,
    LspRange SelectionRange,
    List<DocumentSymbol>? Children);

public record PrepareRenameResult(LspRange Range, string Placeholder);

// JSON-RPC types
public record JsonRpcRequest(string jsonrpc, int id, string method, object? @params);
public record JsonRpcNotification(string jsonrpc, string method, object? @params);
public record JsonRpcResponse(string jsonrpc, int id, object? result, object? error);
