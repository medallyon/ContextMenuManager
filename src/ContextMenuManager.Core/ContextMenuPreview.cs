// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ContextMenuManager.Core
{
    // Modern is Windows 11's first menu; null when the classic menu is the default.
    public sealed record CapturedMenu(List<ContextMenuPreviewItem> Modern, List<ContextMenuPreviewItem> Classic);

    // The real menu Windows builds for each kind of right-click, captured by the MenuCapture helper
    // process, with every item linked back to the entry that adds it so it can be turned off from
    // the menu itself.
    public sealed class ContextMenuPreview
    {
        // Explorer's folder view adds these itself at display time, so the captured menu lacks them.
        // By string key suffix; a trailing '>' marks a submenu, "-" a separator.
        private static readonly Dictionary<ContextMenuPreviewTarget, string[]> ViewItems = new()
        {
            [ContextMenuPreviewTarget.Desktop] = new[] { "View>", "SortBy>", "Refresh", "-", "Paste", "PasteShortcut", "Undo" },
            [ContextMenuPreviewTarget.FolderBackground] = new[] { "View>", "SortBy>", "GroupBy>", "Refresh", "-", "Paste", "PasteShortcut", "Undo" },
        };

        // Registry roots Explorer merges for each target. The sample file is a .txt, matching the capture helper.
        private static readonly Dictionary<ContextMenuPreviewTarget, string[]> TargetRoots = new()
        {
            [ContextMenuPreviewTarget.Desktop] = new[] { "DesktopBackground", "Directory\\Background" },
            [ContextMenuPreviewTarget.FolderBackground] = new[] { "Directory\\Background" },
            [ContextMenuPreviewTarget.Folder] = new[] { "Directory", "AllFilesystemObjects" },
            [ContextMenuPreviewTarget.File] = new[] { "*", "AllFilesystemObjects", ".txt" },
            [ContextMenuPreviewTarget.Drive] = new[] { "Drive", "AllFilesystemObjects" },
        };

        private const string CaptureHelperExe = "ContextMenuManager.MenuCapture.exe";

        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

        // Localized text for the rows Explorer draws itself, by key: "BuiltIn_<name>" and "PreviewTurnedOffHeader".
        private readonly Func<string, string> _getString;

        public ContextMenuPreview(Func<string, string> getString)
        {
            _getString = getString;
        }

        // The capture misses what Explorer adds itself at display time - the view's own View/Sort
        // by/Refresh block on backgrounds, and Windows 11 packaged verbs - so those are added here.
        // Throws TimeoutException when a shell extension hangs the helper.
        public async Task<CapturedMenu> CaptureAsync(ContextMenuPreviewTarget target, IEnumerable<ContextMenuEntry> entries, bool isClassicMenuDefault)
        {
            var targetEntries = entries.Where(e => IsForTarget(e, target)).ToList();

            // Enabled shell extensions, probed one by one so their items can be traced back to them.
            var extensions = new Dictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in targetEntries.Where(e => e.IsEnabled && e.Parent == null && e.Source == ContextMenuEntrySource.Classic && e.Kind != ContextMenuEntryKind.Verb))
            {
                string clsid = entry.Kind == ContextMenuEntryKind.HandlerValue ? entry.OriginalClsidValue : entry.Clsids.FirstOrDefault();
                if (Guid.TryParse(clsid, out Guid guid))
                {
                    extensions.TryAdd(guid.ToString("B"), entry);
                }
            }

            var packages = targetEntries.Where(e => e.Source == ContextMenuEntrySource.Modern).OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            string json = await RunCaptureHelperAsync(target, extensions.Keys, packages.SelectMany(e => e.Clsids).Distinct(StringComparer.OrdinalIgnoreCase));
            using var document = JsonDocument.Parse(json);
            var classic = ParseCapturedItems(document.RootElement.GetProperty("items"));

            var extensionByText = new Dictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var handler in document.RootElement.GetProperty("handlers").EnumerateObject())
            {
                foreach (var text in handler.Value.EnumerateArray())
                {
                    extensionByText.TryAdd(text.GetString() ?? string.Empty, extensions[handler.Name]);
                }
            }

            var verbs = targetEntries.Where(e => e.Kind == ContextMenuEntryKind.Verb).ToList();
            foreach (var item in classic.Where(i => i.IsItem))
            {
                item.Entry = MatchEntry(item, verbs, extensionByText);
                LinkChildren(item, verbs);
            }

            var commands = document.RootElement.TryGetProperty("commands", out var commandsElement) ? commandsElement : default;
            var packagedOn = new List<ContextMenuPreviewItem>();
            var packagedOff = new List<ContextMenuPreviewItem>();
            foreach (var entry in packages)
            {
                var rows = PackagedRows(entry, commands);
                (entry.IsEnabled ? packagedOn : packagedOff).AddRange(rows);
            }

            // Windows 11's own menu, before anything is added to the classic list below.
            var modern = classic.Where(IsInModernMenu).ToList();

            // Turned-off entries aren't in the real menu at all; list them after it so they can come back.
            var shown = Flatten(classic).Select(i => i.Entry).Where(e => e != null).ToHashSet();
            var off = targetEntries
                .Where(e => !e.IsEnabled && e.Source == ContextMenuEntrySource.Classic && !shown.Contains(e))
                .OrderBy(e => e.SortKey ?? e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new ContextMenuPreviewItem { Text = e.DisplayName, Entry = e })
                .ToList();
            var modernOff = off.Where(IsInModernMenu).Concat(packagedOff).ToList();

            if (ViewItems.TryGetValue(target, out var viewKeys))
            {
                classic = WithViewItems(classic, viewKeys);
                modern = WithViewItems(modern, viewKeys);
            }

            if (isClassicMenuDefault)
            {
                int firstSeparator = classic.FindIndex(i => i.IsSeparator);
                classic.InsertRange(firstSeparator < 0 ? classic.Count : firstSeparator, packagedOn);
                AddTurnedOff(classic, off.Concat(packagedOff));
                return new CapturedMenu(null, TidySeparators(classic));
            }

            AddTurnedOff(classic, off);

            // Packaged verbs sit in their own group at the bottom, above "Show more options".
            modern.Add(ContextMenuPreviewItem.Separator());
            modern.AddRange(packagedOn);
            modern.Add(ContextMenuPreviewItem.Separator());
            modern.Add(new ContextMenuPreviewItem { Text = _getString("BuiltIn_ShowMoreOptions"), IsShowMoreOptions = true });
            AddTurnedOff(modern, modernOff);
            return new CapturedMenu(TidySeparators(modern), TidySeparators(classic));
        }

        // A package's rows as its commands describe themselves. A command that hides itself for this
        // target is left out like in Explorer; one that could not be asked falls back to the package name.
        // A turned-off package always gets a row, so it can be turned back on.
        private static List<ContextMenuPreviewItem> PackagedRows(ContextMenuEntry entry, JsonElement commands)
        {
            var rows = new List<ContextMenuPreviewItem>();
            bool answered = false;
            foreach (var clsid in entry.Clsids)
            {
                if (commands.ValueKind == JsonValueKind.Object && commands.TryGetProperty(clsid, out var command))
                {
                    answered = true;
                    rows.AddRange(ParseCapturedItems(command).Where(i => i.IsItem));
                }
            }

            if (rows.Count == 0 && (!answered || !entry.IsEnabled))
            {
                rows.Add(new ContextMenuPreviewItem { Text = entry.DisplayName });
            }

            foreach (var row in Flatten(rows))
            {
                row.Entry = entry;
            }

            return rows;
        }

        // Windows 11's new menu keeps Explorer's own items and Windows' own static verbs; other verbs
        // and every shell extension are only behind "Show more options".
        // ponytail: judged per item from what the registry says; Explorer's own rule is not public.
        private static bool IsInModernMenu(ContextMenuPreviewItem item) => item.Entry switch
        {
            null => true,
            { Kind: ContextMenuEntryKind.Verb } verb => verb.IsLikelyWindowsOwned,
            _ => false,
        };

        private List<ContextMenuPreviewItem> WithViewItems(List<ContextMenuPreviewItem> items, string[] viewKeys)
        {
            var viewItems = viewKeys.Select(BuiltIn).ToList();
            var viewTexts = viewItems.Where(i => !i.IsSeparator).Select(i => i.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return viewItems
                .Append(ContextMenuPreviewItem.Separator())
                .Concat(items.Where(i => !(i.IsItem && i.Entry == null && viewTexts.Contains(i.Text))))
                .ToList();
        }

        private void AddTurnedOff(List<ContextMenuPreviewItem> items, IEnumerable<ContextMenuPreviewItem> off)
        {
            var list = off.ToList();
            if (list.Count > 0)
            {
                items.Add(ContextMenuPreviewItem.Separator());
                items.Add(new ContextMenuPreviewItem { IsHeader = true, Text = _getString("PreviewTurnedOffHeader") });
                items.AddRange(list);
            }
        }

        // Static verbs report their key name as the verb; extensions are known from the probe; a
        // cascading verb has no verb string, so its label is the last resort.
        private static ContextMenuEntry MatchEntry(ContextMenuPreviewItem item, List<ContextMenuEntry> verbs, Dictionary<string, ContextMenuEntry> extensionByText)
        {
            var topVerbs = verbs.Where(e => e.Parent == null);
            return (item.Verb != null ? topVerbs.FirstOrDefault(e => string.Equals(e.HandlerKeyName, item.Verb, StringComparison.OrdinalIgnoreCase)) : null)
                ?? extensionByText.GetValueOrDefault(item.Text)
                ?? topVerbs.FirstOrDefault(e => string.Equals(e.DisplayName, item.Text, StringComparison.OrdinalIgnoreCase));
        }

        // A cascading verb's items are entries of their own; an extension's submenu belongs to the extension.
        private static void LinkChildren(ContextMenuPreviewItem parent, List<ContextMenuEntry> verbs)
        {
            foreach (var child in parent.Children.Where(c => c.IsItem))
            {
                child.Entry = parent.Entry is { Kind: ContextMenuEntryKind.Verb } verb
                    ? verbs.FirstOrDefault(e => e.Parent == verb && string.Equals(e.DisplayName, $"{verb.DisplayName} > {child.Text}", StringComparison.OrdinalIgnoreCase))
                    : parent.Entry;
                LinkChildren(child, verbs);
            }
        }

        private static IEnumerable<ContextMenuPreviewItem> Flatten(IEnumerable<ContextMenuPreviewItem> items) =>
            items.SelectMany(i => Flatten(i.Children).Prepend(i));

        // Registered for the target; submenu items go by their top-level parent.
        private static bool IsForTarget(ContextMenuEntry entry, ContextMenuPreviewTarget target)
        {
            while (entry.Parent != null)
            {
                entry = entry.Parent;
            }

            var roots = TargetRoots[target];
            return entry.Roots.Any(r => roots.Contains(r, StringComparer.OrdinalIgnoreCase))
                || entry.ItemTypes.Any(t => roots.Contains(t, StringComparer.OrdinalIgnoreCase));
        }

        private static async Task<string> RunCaptureHelperAsync(ContextMenuPreviewTarget target, IEnumerable<string> probeClsids, IEnumerable<string> commandClsids)
        {
            string argument = target switch
            {
                ContextMenuPreviewTarget.Desktop => "desktop",
                ContextMenuPreviewTarget.FolderBackground => "background",
                ContextMenuPreviewTarget.Folder => "folder",
                ContextMenuPreviewTarget.File => "file",
                _ => "drive",
            };

            string probe = string.Join(",", probeClsids);
            string commands = string.Join(",", commandClsids);
            string arguments = $"--target {argument}"
                + (probe.Length > 0 ? $" --probe {probe}" : string.Empty)
                + (commands.Length > 0 ? $" --commands {commands}" : string.Empty);
            var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, CaptureHelperExe), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {CaptureHelperExe}.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();

            // A shell extension can hang (network drives, licensing prompts); never wait on it forever.
            using var timeout = new CancellationTokenSource(CaptureTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException();
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException((await error).Trim());
            }

            return await output;
        }

        private static List<ContextMenuPreviewItem> ParseCapturedItems(JsonElement array)
        {
            var items = new List<ContextMenuPreviewItem>();
            foreach (var element in array.EnumerateArray())
            {
                if (element.TryGetProperty("separator", out _))
                {
                    items.Add(ContextMenuPreviewItem.Separator());
                    continue;
                }

                // A packaged command that hides itself for this target.
                if (element.TryGetProperty("hidden", out _))
                {
                    continue;
                }

                var item = new ContextMenuPreviewItem
                {
                    Text = element.TryGetProperty("text", out var text) ? text.GetString() : string.Empty,
                    Shortcut = element.TryGetProperty("shortcut", out var shortcut) ? shortcut.GetString() : null,
                    Verb = element.TryGetProperty("verb", out var verb) ? verb.GetString() : null,
                    IsDisabled = element.TryGetProperty("disabled", out _),
                };

                if (element.TryGetProperty("icon", out var icon))
                {
                    item.Icon = MenuIcon.FromBgra(icon.GetProperty("w").GetInt32(), icon.GetProperty("h").GetInt32(), icon.GetProperty("bgra").GetBytesFromBase64());
                }
                else if (element.TryGetProperty("iconSpec", out var iconSpec))
                {
                    // Packaged commands name an icon file instead of drawing a bitmap.
                    item.Icon = ContextMenuRegistry.LoadIcon(iconSpec.GetString());
                }

                if (element.TryGetProperty("children", out var children))
                {
                    item.Children.AddRange(ParseCapturedItems(children));
                }

                items.Add(item);
            }

            return items;
        }

        private ContextMenuPreviewItem BuiltIn(string key)
        {
            if (key == "-")
            {
                return ContextMenuPreviewItem.Separator();
            }

            var item = new ContextMenuPreviewItem { Text = _getString("BuiltIn_" + key.TrimEnd('>')) };
            if (key.EndsWith('>'))
            {
                // Placeholder so the row shows a chevron; Explorer fills these at runtime.
                item.Children.Add(new ContextMenuPreviewItem { Text = "…", IsDisabled = true });
            }

            return item;
        }

        // Drops leading, trailing and doubled separators left behind by empty sections.
        private static List<ContextMenuPreviewItem> TidySeparators(IEnumerable<ContextMenuPreviewItem> items)
        {
            var result = new List<ContextMenuPreviewItem>();
            foreach (var item in items)
            {
                if (item.IsSeparator && (result.Count == 0 || result[^1].IsSeparator))
                {
                    continue;
                }

                result.Add(item);
            }

            if (result.Count > 0 && result[^1].IsSeparator)
            {
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }
    }
}
