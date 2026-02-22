# LazyDotIDE

A **very** primitive, Neanderthal-level console IDE for .NET — for when you're SSH'd into a remote server and just need to fix something quickly without spinning up a real editor.

> **Honest disclaimer:** This is a personal tool. It is not polished. It will not replace VS Code. It barely replaces `nano`. It exists because sometimes you're on a cloud server, something is broken, and you want a vaguely IDE-shaped thing rather than raw `vi`.

---

## What it does

- Browse your project's files and directories in a tree panel
- Open and edit files in a tabbed editor (with basic C# syntax highlighting)
- Run `dotnet build` and see the output
- Run `dotnet test` and see the output
- Basic LSP integration (completions via OmniSharp / roslyn-ls, if available)
- Git status in the status bar
- NuGet package browser dialog
- Close tabs when you're done

## What it doesn't do

- Pretty much everything a real IDE does
- Reliable multi-cursor editing
- Debugging
- Refactoring
- Work well if you sneeze near it

---

## Built on

[**SharpConsoleUI**](https://github.com/nickprotop/ConsoleEx) — a console windowing library that is fairly stable but also still getting polished. Both projects are works in progress. This is fine.

---

## Usage

```bash
# Run on a project directory
lazydotide /path/to/your/dotnet/project

# Or just run it in the current directory
lazydotide
```

### Install as a dotnet tool

```bash
dotnet tool install --global LazyDotIDE
```

Or from source (with a sibling `ConsoleEx/` directory for live SharpConsoleUI changes):

```bash
dotnet run --project /path/to/lazydotide -- /path/to/your/project
```

---

## Keyboard shortcuts

| Key | Action |
|-----|--------|
| `Alt+1..9` | Switch between open windows |
| `Ctrl+T` | Cycle focus |
| `Ctrl+S` | Save current file |
| `Ctrl+W` | Close current tab |
| `Ctrl+Q` / `Alt+F4` | Quit |
| `F5` | Build |

---

## Requirements

- .NET 9.0
- A terminal that supports ANSI escape codes (any modern SSH session should be fine)
- Low expectations
