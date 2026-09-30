// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.Linq;
using System.Threading.Tasks;

using ContextMenuManager.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage.Streams;

namespace ContextMenuManager.CmdPal;

internal static class Icons
{
    public static IconInfo App { get; } = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
}

// Every entry the app lists, filtered by Command Palette's own search box.
internal sealed partial class EntriesPage : ListPage
{
    private IListItem[]? _items;

    public EntriesPage()
    {
        Name = "Open";
        Title = "Context menu entries";
        Icon = Icons.App;
        PlaceholderText = "Search context menu entries";
    }

    // Enumerated once per page instance: the registry is the only state, and this process is the
    // only writer while the page is open.
    public override IListItem[] GetItems() => _items ??= Load();

    private static IListItem[] Load()
    {
        bool isElevated = ContextMenuRegistry.IsElevated;
        var items = ContextMenuRegistry.Enumerate(isElevated)
            .OrderBy(e => e.SortKey ?? e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new EntryItem(e, isElevated))
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
    private static readonly Tag OffTag = new("Off");

    public EntryItem(ContextMenuEntry entry, bool isElevated)
    {
        Entry = entry;
        Title = entry.DisplayName;
        Subtitle = Describe(entry);
        Command = entry switch
        {
            { IsToggleable: true } => new ToggleEntryCommand(this, confirmed: false),

            // Not toggleable here only because the extension runs unelevated.
            { Scope: ContextMenuEntryScope.AllUsers } when !isElevated => new OpenAppCommand(asAdmin: true),
            _ => new NoOpCommand(),
        };
        MoreCommands = [new CommandContextItem(new RestartExplorerCommand())];
        Refresh();
    }

    public ContextMenuEntry Entry { get; }

    public void Refresh()
    {
        Tags = Entry.IsEnabled ? [] : [OffTag];
        if (Command is ToggleEntryCommand toggle)
        {
            toggle.Name = Entry.IsEnabled ? "Turn off" : "Turn on";
        }
    }

    public void LoadIcon()
    {
        try
        {
            if (ContextMenuRegistry.LoadIcon(Entry.IconSpec)?.Png is { } png)
            {
                // Command Palette reads the stream from its own process, which a wrapped MemoryStream can't serve.
                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream);
                writer.WriteBytes(png);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                stream.Seek(0);
                Icon = IconInfo.FromStream(stream);
            }
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
