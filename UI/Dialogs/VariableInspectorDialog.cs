using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using TreeNode = SharpConsoleUI.Controls.TreeNode;
using Color = SharpConsoleUI.Color;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public class VariableInspectorDialog : DialogBase<bool>
{
    private readonly string _name;
    private readonly string _value;
    private readonly string? _type;
    private readonly int _variablesReference;
    private readonly Func<int, Task<List<DapVariable>>> _fetchChildren;

    private TreeControl? _membersTree;

    private VariableInspectorDialog(string name, string value, string? type, int variablesReference,
        Func<int, Task<List<DapVariable>>> fetchChildren)
    {
        _name = name;
        _value = value;
        _type = type;
        _variablesReference = variablesReference;
        _fetchChildren = fetchChildren;
    }

    public static Task<bool> ShowAsync(string name, string value, string? type, int variablesReference,
        Func<int, Task<List<DapVariable>>> fetchChildren, ConsoleWindowSystem ws)
        => new VariableInspectorDialog(name, value, type, variablesReference, fetchChildren).ShowAsync(ws);

    protected override string GetTitle() => "Variable Inspector";
    protected override (int width, int height) GetSize() => (60, 20);
    protected override bool GetResizable() => true;
    protected override bool GetMovable() => true;

    protected override void BuildContent()
    {
        // Name
        Modal.AddControl(Controls.Markup()
            .AddLine($"[bold]Name:[/]  [{ColorScheme.PrimaryMarkup}]{MarkupParser.Escape(_name)}[/]")
            .WithMargin(1, 1, 0, 0)
            .Build());

        // Type (if available)
        if (_type != null)
        {
            Modal.AddControl(Controls.Markup()
                .AddLine($"[bold]Type:[/]  [dim]{MarkupParser.Escape(_type)}[/]")
                .WithMargin(1, 0, 0, 0)
                .Build());
        }

        // Value section header
        Modal.AddControl(Controls.RuleBuilder()
            .WithTitle("Value")
            .WithColor(ColorScheme.RuleColor)
            .WithMargin(1, 1, 0, 0)
            .Build());

        // Scrollable value panel
        var valueMarkup = new MarkupControl(new List<string> { MarkupParser.Escape(_value) })
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        var valuePanel = new ScrollablePanelControl
        {
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = _variablesReference > 0 ? VerticalAlignment.Top : VerticalAlignment.Fill,
            Height = _variablesReference > 0 ? 4 : null
        };
        valuePanel.Margin = new Margin(1, 0, 1, 0);
        valuePanel.AddControl(valueMarkup);
        Modal.AddControl(valuePanel);

        // Members section (only if expandable)
        if (_variablesReference > 0)
        {
            Modal.AddControl(Controls.RuleBuilder()
                .WithTitle("Members")
                .WithColor(ColorScheme.RuleColor)
                .WithMargin(1, 0, 0, 0)
                .Build());

            _membersTree = new TreeControl
            {
                Guide = TreeGuide.Line,
                HighlightBackgroundColor = Color.SteelBlue,
                HighlightForegroundColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Fill
            };
            _membersTree.NodeExpandCollapse += OnMemberNodeExpandCollapse;
            _membersTree.MouseDoubleClick += OnMemberNodeDoubleClick;

            var membersPanel = new ScrollablePanelControl
            {
                ShowScrollbar = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Fill
            };
            membersPanel.Margin = new Margin(1, 0, 1, 0);
            membersPanel.AddControl(_membersTree);
            Modal.AddControl(membersPanel);

            // Load initial children
            LoadChildren(_variablesReference, null);
        }

        // Bottom status bar
        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        var hint = _variablesReference > 0
            ? $"[{ColorScheme.MutedMarkup}]DblClick/Ctrl+Enter:Inspect  Esc:Close[/]"
            : $"[{ColorScheme.MutedMarkup}]Esc:Close[/]";
        Modal.AddControl(Controls.Markup()
            .AddLine(hint)
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    private void LoadChildren(int variablesReference, TreeNode? parentNode)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var children = await _fetchChildren(variablesReference);

                if (parentNode != null)
                    parentNode.ClearChildren();

                foreach (var v in children)
                {
                    var node = parentNode != null
                        ? parentNode.AddChild(FormatVariable(v))
                        : _membersTree!.AddRootNode(FormatVariable(v));
                    node.Tag = new MemberTag(v.VariablesReference, false, v.Name, v.Value, v.Type);
                    if (v.VariablesReference > 0)
                    {
                        node.AddChild("[dim]Loading...[/]");
                        node.IsExpanded = false;
                    }
                }

                if (parentNode?.Tag is MemberTag oldTag)
                    parentNode.Tag = oldTag with { ChildrenLoaded = true };

            }
            catch { }
        });
    }

    private void OnMemberNodeExpandCollapse(object? sender, TreeNodeEventArgs args)
    {
        var node = args.Node;
        if (node?.Tag is not MemberTag tag) return;
        if (tag.ChildrenLoaded || tag.VariablesReference <= 0) return;
        if (!node.IsExpanded) return;

        LoadChildren(tag.VariablesReference, node);
    }

    private void OnMemberNodeDoubleClick(object? sender, MouseEventArgs e)
    {
        var node = _membersTree?.SelectedNode;
        if (node?.Tag is not MemberTag tag) return;

        // Undo expand/collapse toggle that the tree already applied
        if (tag.VariablesReference > 0)
            node.IsExpanded = !node.IsExpanded;

        OpenNestedInspector(tag);
    }

    private void OpenNestedInspector(MemberTag tag)
    {
        _ = ShowAsync(tag.Name, tag.Value, tag.Type, tag.VariablesReference, _fetchChildren, WindowSystem);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo is { Key: ConsoleKey.Enter, Modifiers: ConsoleModifiers.Control }
            && _membersTree?.SelectedNode?.Tag is MemberTag tag)
        {
            OpenNestedInspector(tag);
            e.Handled = true;
        }
        else if (e.KeyInfo.Key == ConsoleKey.Escape)
        {
            CloseWithResult(false);
            e.Handled = true;
        }
    }

    private static string FormatVariable(DapVariable v)
    {
        var escapedName = MarkupParser.Escape(v.Name);
        var escapedValue = MarkupParser.Escape(v.Value);

        if (v.VariablesReference > 0)
        {
            var typeStr = v.Type != null ? $" [dim]({MarkupParser.Escape(v.Type)})[/]" : "";
            var preview = escapedValue.Length > 30 ? escapedValue[..30] + "..." : escapedValue;
            return $"[cyan1]{escapedName}[/]{typeStr} = {preview}";
        }
        else
        {
            var typeStr = v.Type != null ? $" [dim]({MarkupParser.Escape(v.Type)})[/]" : "";
            return $"[cyan1]{escapedName}[/] = {escapedValue}{typeStr}";
        }
    }

    private record MemberTag(int VariablesReference, bool ChildrenLoaded, string Name, string Value, string? Type);
}
