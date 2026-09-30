// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ContextMenuManager.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ContextMenuManager.CmdPal;

// The app's target picker: one page per kind of right-click, plus the flat list for searching.
internal sealed partial class TargetsPage : ListPage
{
    private static readonly (ContextMenuPreviewTarget Target, string Title)[] Targets =
    [
        (ContextMenuPreviewTarget.Desktop, "Desktop"),
        (ContextMenuPreviewTarget.FolderBackground, "Empty space in a folder"),
        (ContextMenuPreviewTarget.Folder, "A folder"),
        (ContextMenuPreviewTarget.File, "A file (.txt)"),
        (ContextMenuPreviewTarget.Drive, "A drive"),
    ];

    public TargetsPage()
    {
        Name = "Open";
        Title = "Context menu entries";
        Icon = Icons.App;
        PlaceholderText = "Right-click on";
    }

    // New menu pages on every visit, so each opens with a fresh capture after toggles or restarts.
    public override IListItem[] GetItems() =>
    [
        .. Targets.Select(t => new ListItem(new MenuPage(t.Target, t.Title)) { Title = t.Title, Subtitle = "Right-click on" }),
        new Separator(),
        new ListItem(new EntriesPage()) { Title = "All entries", Subtitle = "Every entry in one searchable list" },
    ];
}

// The real menu for one target, in Explorer's order, as ContextMenuPreview captures it for the app.
internal sealed partial class MenuPage : ListPage
{
    // English strings the app keeps in its Resources.resw.
    private static readonly Dictionary<string, string> Strings = new()
    {
        ["BuiltIn_View"] = "View",
        ["BuiltIn_SortBy"] = "Sort by",
        ["BuiltIn_GroupBy"] = "Group by",
        ["BuiltIn_Refresh"] = "Refresh",
        ["BuiltIn_Paste"] = "Paste",
        ["BuiltIn_PasteShortcut"] = "Paste shortcut",
        ["BuiltIn_Undo"] = "Undo",
        ["BuiltIn_ShowMoreOptions"] = "Show more options",
        ["PreviewTurnedOffHeader"] = "Turned off",
    };

    private readonly ContextMenuPreviewTarget _target;

    private IListItem[] _items = [];

    private bool _captureStarted;

    public MenuPage(ContextMenuPreviewTarget target, string title)
    {
        _target = target;
        Name = "Open";
        Title = title;
        Icon = Icons.App;
    }

    public override IListItem[] GetItems()
    {
        if (!_captureStarted)
        {
            _captureStarted = true;
            IsLoading = true;
            _ = CaptureAsync();
        }

        return _items;
    }

    private async Task CaptureAsync()
    {
        var rows = new List<MenuRow>();
        try
        {
            var entries = ContextMenuRegistry.Enumerate(isElevated: true);
            var menu = await new ContextMenuPreview(key => Strings.GetValueOrDefault(key, key))
                .CaptureAsync(_target, entries, ContextMenuRegistry.IsClassicMenuDefault());
            _items = Build(menu.Modern ?? menu.Classic, menu.Classic, rows);
        }
        catch (Exception ex)
        {
            string reason = ex is TimeoutException ? "A shell extension didn't respond within 10 seconds." : ex.Message;
            _items = [new ListItem(new NoOpCommand()) { Title = "Couldn't read this menu", Subtitle = reason }];
        }

        IsLoading = false;
        RaiseItemsChanged(_items.Length);

        // Icon extraction loads third-party binaries, so it runs after the menu is shown.
        foreach (var row in rows)
        {
            await Task.Run(row.LoadIcon);
        }
    }

    // Windows 11's "Show more options" opens the classic menu, like Explorer does.
    private static IListItem[] Build(List<ContextMenuPreviewItem> items, List<ContextMenuPreviewItem> classic, List<MenuRow> rows)
    {
        var result = new List<IListItem>();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                result.Add(new Separator());
            }
            else if (item.IsHeader)
            {
                // A titled separator replaces the plain one before the header.
                if (result.Count > 0 && result[^1] is Separator { Title: "" })
                {
                    result.RemoveAt(result.Count - 1);
                }

                result.Add(new Separator(item.Text));
            }
            else if (item.IsShowMoreOptions)
            {
                result.Add(new ListItem(new SubmenuPage(item.Text, Build(classic, classic, rows))) { Title = item.Text });
            }
            else
            {
                var submenu = item.HasChildren ? new SubmenuPage(item.Text, Build(item.Children, classic, rows)) : null;
                var row = new MenuRow(item, submenu);
                rows.Add(row);
                result.Add(row);
            }
        }

        return [.. result];
    }
}

internal sealed partial class SubmenuPage : ListPage
{
    private readonly IListItem[] _items;

    public SubmenuPage(string title, IListItem[] items)
    {
        _items = items;
        Name = "Open";
        Title = title;
    }

    public override IListItem[] GetItems() => _items;
}

// One captured menu item. Enter toggles the entry behind it, or opens the submenu when it has one;
// then the toggle moves to the More menu.
internal sealed partial class MenuRow : ListItem
{
    private readonly ContextMenuPreviewItem _item;

    private readonly ToggleEntryCommand? _toggle;

    public MenuRow(ContextMenuPreviewItem item, SubmenuPage? submenu)
    {
        _item = item;
        Title = item.Text;
        _toggle = item.Entry is { IsToggleable: true } entry ? new ToggleEntryCommand(entry, Refresh, confirmed: false) : null;

        var more = new List<IContextItem>();
        if (submenu != null && _toggle != null)
        {
            more.Add(new CommandContextItem(_toggle));
        }

        more.Add(new CommandContextItem(new RestartExplorerCommand()));
        Command = submenu != null ? submenu : _toggle != null ? _toggle : new NoOpCommand();
        MoreCommands = [.. more];
        Refresh();
    }

    public void Refresh()
    {
        Tags = Icons.OffTags(_item.Entry);
        _toggle?.Refresh();
    }

    public void LoadIcon()
    {
        try
        {
            // The item's own bitmap from the captured menu, else its entry's registered icon.
            var icon = _item.Icon ?? (_item.Entry is { } entry ? ContextMenuRegistry.LoadIcon(entry.IconSpec) : null);
            Icon = Icons.FromMenuIcon(icon) ?? Icon;
        }
        catch (Exception)
        {
            // A broken icon resource only costs the icon.
        }
    }
}
