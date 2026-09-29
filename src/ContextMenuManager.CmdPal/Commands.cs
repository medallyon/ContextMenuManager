// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using ContextMenuManager.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ContextMenuManager.CmdPal;

internal sealed partial class ToggleEntryCommand : InvokableCommand
{
    private readonly EntryItem _item;

    // True for the command behind the confirmation prompt, so it writes without asking again.
    private readonly bool _confirmed;

    public ToggleEntryCommand(EntryItem item, bool confirmed)
    {
        _item = item;
        _confirmed = confirmed;
        Name = item.Entry.IsEnabled ? "Turn off" : "Turn on";
    }

    public override ICommandResult Invoke()
    {
        var entry = _item.Entry;
        bool enable = !entry.IsEnabled;

        // Same extra step as the app: System32 handlers can break Explorer or security software.
        if (!enable && entry.IsLikelyWindowsOwned && !_confirmed)
        {
            return CommandResult.Confirm(new ConfirmationArgs
            {
                Title = "This looks like a built-in Windows component",
                Description = "Disabling it may affect File Explorer or security software. Continue only if you're sure.",
                PrimaryCommand = new ToggleEntryCommand(_item, confirmed: true) { Name = "Continue" },
                IsPrimaryCommandCritical = true,
            });
        }

        if (!ContextMenuRegistry.Toggle(entry, enable, ContextMenuRegistry.IsElevated))
        {
            return CommandResult.ShowToast(new ToastArgs { Message = $"Couldn't change \"{entry.DisplayName}\".", Result = CommandResult.KeepOpen() });
        }

        _item.Refresh();
        string state = enable ? "on" : "off";
        return CommandResult.ShowToast(new ToastArgs
        {
            Message = $"Turned {state} \"{entry.DisplayName}\". Restart File Explorer to apply.",
            Result = CommandResult.KeepOpen(),
        });
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

    private readonly bool _asAdmin;

    public OpenAppCommand(bool asAdmin)
    {
        _asAdmin = asAdmin;
        Name = asAdmin ? "Open app as administrator" : "Open Context Menu Manager";
        Icon = Icons.App;
    }

    // ponytail: finds the app only on PATH (winget's portable alias); a zip install elsewhere gets
    // the releases page. Add a settings path or App Paths lookup if that bites.
    public override ICommandResult Invoke()
    {
        if (ShellHelpers.FileExistInPath(AppExe, out string path))
        {
            ShellHelpers.OpenInShell(path, runAs: _asAdmin ? ShellHelpers.ShellRunAsType.Administrator : ShellHelpers.ShellRunAsType.None);
        }
        else
        {
            ShellHelpers.OpenInShell(ReleasesUrl);
        }

        return CommandResult.Dismiss();
    }
}
