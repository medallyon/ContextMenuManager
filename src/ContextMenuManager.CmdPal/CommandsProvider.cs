// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ContextMenuManager.CmdPal;

public sealed partial class CommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public CommandsProvider()
    {
        Id = "ContextMenuManager";
        DisplayName = "Context Menu Manager";
        Icon = Icons.App;
        _commands =
        [
            new CommandItem(new TargetsPage())
            {
                Title = "Context menu entries",
                Subtitle = "Turn Explorer right-click menu entries on and off",
            },
            new CommandItem(new RestartExplorerCommand()) { Subtitle = "Apply context menu changes" },
            new CommandItem(new OpenAppCommand()) { Subtitle = "Full app with the live menu preview" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;
}
