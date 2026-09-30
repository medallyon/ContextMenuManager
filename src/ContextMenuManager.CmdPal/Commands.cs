// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.IO;

using ContextMenuManager.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ContextMenuManager.CmdPal;

internal sealed partial class ToggleEntryCommand : InvokableCommand
{
    private readonly ContextMenuEntry _entry;

    // Updates the row that shows the entry after a write.
    private readonly Action _changed;

    // True for the command behind the confirmation prompt, so it writes without asking again.
    private readonly bool _confirmed;

    public ToggleEntryCommand(ContextMenuEntry entry, Action changed, bool confirmed)
    {
        _entry = entry;
        _changed = changed;
        _confirmed = confirmed;
        Refresh();
    }

    public void Refresh() => Name = _entry.IsEnabled ? "Turn off" : "Turn on";

    public override ICommandResult Invoke()
    {
        var entry = _entry;
        bool enable = !entry.IsEnabled;

        // Same extra step as the app: System32 handlers can break Explorer or security software.
        if (!enable && entry.IsLikelyWindowsOwned && !_confirmed)
        {
            return CommandResult.Confirm(new ConfirmationArgs
            {
                Title = "This looks like a built-in Windows component",
                Description = "Disabling it may affect File Explorer or security software. Continue only if you're sure.",
                PrimaryCommand = new ToggleEntryCommand(entry, _changed, confirmed: true) { Name = "Continue" },
                IsPrimaryCommandCritical = true,
            });
        }

        if (!Write(entry, enable))
        {
            return CommandResult.ShowToast(new ToastArgs { Message = $"Couldn't change \"{entry.DisplayName}\".", Result = CommandResult.KeepOpen() });
        }

        entry.IsEnabled = enable;
        _changed();
        string state = enable ? "on" : "off";
        return CommandResult.ShowToast(new ToastArgs
        {
            Message = $"Turned {state} \"{entry.DisplayName}\". Restart File Explorer to apply.",
            Result = CommandResult.KeepOpen(),
        });
    }

    private static bool Write(ContextMenuEntry entry, bool enable)
    {
        bool isElevated = ContextMenuRegistry.IsElevated;
        return entry.Scope == ContextMenuEntryScope.AllUsers && !isElevated
            ? ElevatedRegistry.ApplyToLocalMachine(ContextMenuRegistry.GetToggleWrites(entry, enable))
            : ContextMenuRegistry.Toggle(entry, enable, isElevated);
    }
}

internal sealed partial class RestartExplorerCommand : InvokableCommand
{
    public RestartExplorerCommand()
    {
        Name = "Restart File Explorer";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        ContextMenuRegistry.RestartExplorer();
        return CommandResult.Dismiss();
    }
}

internal sealed partial class OpenAppCommand : InvokableCommand
{
    private const string AppExe = "ContextMenuManager.exe";

    private const string ReleasesUrl = "https://github.com/medallyon/ContextMenuManager/releases";

    public OpenAppCommand()
    {
        Name = "Open Context Menu Manager";
        Icon = Icons.App;
    }

    // The app registers the extension from its own CmdPal subfolder. A build registered by hand
    // has no app beside it, so fall back to PATH (winget's portable alias).
    public override ICommandResult Invoke()
    {
        string besideApp = Path.Combine(AppContext.BaseDirectory, "..", AppExe);
        if (File.Exists(besideApp))
        {
            ShellHelpers.OpenInShell(Path.GetFullPath(besideApp));
        }
        else if (ShellHelpers.FileExistInPath(AppExe, out string path))
        {
            ShellHelpers.OpenInShell(path);
        }
        else
        {
            ShellHelpers.OpenInShell(ReleasesUrl);
        }

        return CommandResult.Dismiss();
    }
}
