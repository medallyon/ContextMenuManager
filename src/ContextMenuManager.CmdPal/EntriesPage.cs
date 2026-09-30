// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.Linq;
using System.Threading.Tasks;

using ContextMenuManager.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ContextMenuManager.CmdPal;

// Every entry in one flat list, for Command Palette's search box: the menu pages only search one level.
internal sealed partial class EntriesPage : ListPage
{
    private IListItem[]? _items;

    public EntriesPage()
    {
        Name = "Open";
        Title = "All entries";
        Icon = Icons.App;
        PlaceholderText = "Search context menu entries";
    }

    // Enumerated once per page instance: the registry is the only state, and this process is the
    // only writer while the page is open.
    public override IListItem[] GetItems() => _items ??= Load();

    private static IListItem[] Load()
    {
        // Enumerated as elevated so all-users entries stay toggleable: ToggleEntryCommand elevates per write.
        var items = ContextMenuRegistry.Enumerate(isElevated: true)
            .OrderBy(e => e.SortKey ?? e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new EntryItem(e))
            .ToArray();

        // Icon extraction loads third-party binaries, so it runs after the list is shown.
        _ = Task.Run(() =>
        {
            foreach (var item in items)
            {
                item.LoadIcon();
            }
        });

        return items;
    }
}

internal sealed partial class EntryItem : ListItem
{
    private readonly ToggleEntryCommand? _toggle;

    public EntryItem(ContextMenuEntry entry)
    {
        Entry = entry;
        Title = entry.DisplayName;
        Subtitle = Describe(entry);
        _toggle = entry.IsToggleable ? new ToggleEntryCommand(entry, Refresh, confirmed: false) : null;
        Command = _toggle ?? (ICommand)new NoOpCommand();
        MoreCommands = [new CommandContextItem(new RestartExplorerCommand())];
        Refresh();
    }

    public ContextMenuEntry Entry { get; }

    public void Refresh()
    {
        Tags = Icons.OffTags(Entry);
        _toggle?.Refresh();
    }

    public void LoadIcon()
    {
        try
        {
            Icon = Icons.FromMenuIcon(ContextMenuRegistry.LoadIcon(Entry.IconSpec)) ?? Icon;
        }
        catch (Exception)
        {
            // A broken icon resource only costs the icon.
        }
    }

    private static string Describe(ContextMenuEntry entry)
    {
        string scope = entry.Scope == ContextMenuEntryScope.CurrentUser ? "Current user" : "All users";
        string source = entry.Source == ContextMenuEntrySource.Classic ? "Classic" : "Modern";
        string text = $"{scope} · {source}";
        return entry.Roots.Count == 0 ? text : $"{text} · {string.Join(", ", entry.Roots.Select(RootName).Distinct())}";
    }

    private static string RootName(string root) => root switch
    {
        "*" => "Files",
        "Directory" => "Folders",
        "Directory\\Background" => "Folder background",
        "AllFilesystemObjects" => "Files and folders",
        "Drive" => "Drives",
        "DesktopBackground" => "Desktop",
        _ => root,
    };
}
