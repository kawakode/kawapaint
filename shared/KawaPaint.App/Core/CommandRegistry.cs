// KawaPaint — the catalogue of everything the app can be asked to do.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;

namespace KawaPaint.App.Core;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, AppCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppCommand> _ordered = new();

    /// <summary>User gesture overrides, resolved from settings and refreshed by <see cref="ReloadBindings"/>.</summary>
    private Dictionary<string, KeyGesture> _bindings = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public IReadOnlyList<AppCommand> All => _ordered;

    public AppCommand Register(AppCommand command)
    {
        if (_commands.TryGetValue(command.Id, out var existing)) _ordered.Remove(existing);
        _commands[command.Id] = command;
        _ordered.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
        return command;
    }

    public AppCommand Register(string id, string label, Action execute, string category = "General",
                               string? iconName = null, KeyGesture? gesture = null,
                               Func<bool>? canExecute = null, bool suppressInTextInput = false)
        => Register(new AppCommand(id, label, execute)
        {
            Category = category,
            IconName = iconName,
            DefaultGesture = gesture,
            CanExecute = canExecute,
            SuppressInTextInput = suppressInTextInput
        });

    public AppCommand? Find(string id) => _commands.GetValueOrDefault(id);

    public IEnumerable<IGrouping<string, AppCommand>> ByCategory()
        => _ordered.GroupBy(c => c.Category);

    public bool Execute(string id)
    {
        var command = Find(id);
        if (command is null || !command.IsEnabled) return false;
        command.Execute();
        return true;
    }

    /// <summary>The gesture actually in force for a command: the user's override, else the default.</summary>
    public KeyGesture? GestureFor(AppCommand command)
        => _bindings.TryGetValue(command.Id, out var custom) ? custom : command.DefaultGesture;

    /// <summary>Re-reads gesture overrides from settings. Call after the keyboard settings change.</summary>
    public void ReloadBindings(WorkspaceSettings workspace)
    {
        var parsed = new Dictionary<string, KeyGesture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, text) in workspace.KeyBindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            try { parsed[id] = KeyGesture.Parse(text); }
            catch { /* a hand-edited settings file should not break startup */ }
        }
        _bindings = parsed;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Dispatches a key press to the first command whose gesture matches. Returns true when the
    /// event was consumed. Avalonia's MenuItem.InputGesture only renders the shortcut text, so
    /// every accelerator in the app arrives here.
    /// </summary>
    public bool HandleKey(KeyEventArgs e, bool isTextInputFocused)
    {
        foreach (var command in _ordered)
        {
            if (isTextInputFocused && command.SuppressInTextInput) continue;

            var gesture = GestureFor(command);
            if (gesture is null || !gesture.Matches(e)) continue;
            if (!command.IsEnabled) return false;

            command.Execute();
            return true;
        }
        return false;
    }

    /// <summary>Builds a menu item bound to a command, including its current shortcut text.</summary>
    public MenuItem CreateMenuItem(string id)
    {
        var command = Find(id) ?? throw new ArgumentException($"Unknown command '{id}'.", nameof(id));
        var item = new MenuItem { Header = command.Label, InputGesture = GestureFor(command) };
        item.Click += (_, _) => command.Execute();
        return item;
    }
}
