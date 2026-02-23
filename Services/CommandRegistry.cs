namespace DotNetIDE;

public class IdeCommand
{
    public string   Id         { get; set; } = "";
    public string   Label      { get; set; } = "";
    public string   Category   { get; set; } = "";
    public string?  Keybinding { get; set; }
    public string   Icon       { get; set; } = "  ";
    public Action   Execute    { get; set; } = () => {};
    public int      Priority   { get; set; } = 50;
}

public class CommandRegistry
{
    private readonly List<IdeCommand> _commands = new();

    public void Register(IdeCommand command) => _commands.Add(command);

    public IReadOnlyList<IdeCommand> All => _commands;

    public List<IdeCommand> Search(string query)
    {
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _commands.ToList()
            : _commands.Where(c =>
                c.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (c.Keybinding != null &&
                 c.Keybinding.Contains(query, StringComparison.OrdinalIgnoreCase))
            ).ToList();

        return filtered
            .OrderByDescending(c =>
                !string.IsNullOrWhiteSpace(query) &&
                c.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(c => c.Priority)
            .ToList();
    }
}
