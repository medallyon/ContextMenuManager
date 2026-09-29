// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ContextMenuManager.Core
{
    // Reads and writes the registry state behind Explorer's context menu. The registry IS the
    // persisted state - no settings file, no cache to keep in sync - so callers re-enumerate live.
    public static class ContextMenuRegistry
    {
        // Roots this tool inspects for "...\shellex\ContextMenuHandlers\<Name>" subkeys.
        // v1 deliberately stops here - the per-extension SystemFileAssociations\<ext>\... tree is
        // a much bigger (and much lower-value) surface, skipped for now.
        private static readonly string[] ContextMenuRoots =
        {
            "*",
            "Directory",
            "Directory\\Background",
            "AllFilesystemObjects",
            "Drive",
            "DesktopBackground",
        };

        private const string DisabledValuePrefix = "disabled_";

        private const string LegacyDisableValue = "LegacyDisable";

        private const string BlockedKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked";

        // Present when the user restored the Windows 10 menu as the default right-click menu.
        private const string ClassicMenuRestoreKey = "Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\\InprocServer32";

        // Nested submenus deeper than this are not walked.
        private const int MaxSubmenuDepth = 3;

        // Joins SortKey segments; sorts below every printable character so children stay under their parent.
        private const char SortKeySeparator = '\u0001';

        // Shown at 16 epx; 32 px source keeps icons sharp at 200% scaling.
        public const int IconPixelSize = 32;

        private static readonly ConcurrentDictionary<string, byte[]> IconCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // PowerToys' New+ hides/shows Explorer's built-in "New" submenu through this same key, so
        // editing it here would fight that module. Everything else (including Windows' own handlers)
        // is toggleable; System32/SysWOW64 handlers get an extra confirmation instead of a hard block.
        private static readonly HashSet<string> BuiltInHandlerDenylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "New",
        };

        public static bool IsElevated
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        // Every classic handler and verb under both hives, then the packaged entries. All-users
        // entries need elevation to write, so they are marked untoggleable when not elevated.
        public static List<ContextMenuEntry> Enumerate(bool isElevated)
        {
            var entries = EnumerateClassicEntries(RegistryHive.CurrentUser)
                .Concat(EnumerateClassicEntries(RegistryHive.LocalMachine))
                .Concat(EnumerateVerbEntries(RegistryHive.CurrentUser))
                .Concat(EnumerateVerbEntries(RegistryHive.LocalMachine))
                .Concat(EnumerateModernEntries())
                .ToList();

            if (!isElevated)
            {
                foreach (var entry in entries.Where(e => e.Scope == ContextMenuEntryScope.AllUsers))
                {
                    entry.IsToggleable = false;
                }
            }

            return entries;
        }

        // Writes the toggle with the mechanism matching entry.Kind (see ContextMenuEntryKind), for every
        // root the entry covers. Never deletes a handler or verb key. Callers own the confirmation dialogs.
        public static bool Toggle(ContextMenuEntry entry, bool enable, bool isElevated)
        {
            if (entry == null || !entry.IsToggleable)
            {
                return false;
            }

            if (entry.Scope == ContextMenuEntryScope.AllUsers && !isElevated)
            {
                return false;
            }

            RegistryKey baseKey = entry.Scope == ContextMenuEntryScope.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

            try
            {
                if (entry.Kind == ContextMenuEntryKind.BlockedClsid)
                {
                    using var blockedKey = baseKey.CreateSubKey(BlockedKeyPath, writable: true);
                    foreach (var clsid in entry.Clsids)
                    {
                        if (enable)
                        {
                            blockedKey.DeleteValue(clsid, throwOnMissingValue: false);
                        }
                        else
                        {
                            blockedKey.SetValue(clsid, entry.DisplayName ?? string.Empty, RegistryValueKind.String);
                        }
                    }
                }
                else
                {
                    foreach (var path in entry.KeyPaths)
                    {
                        using var key = baseKey.OpenSubKey($"Software\\Classes\\{path}", writable: true);
                        if (key == null)
                        {
                            continue;
                        }

                        if (entry.Kind == ContextMenuEntryKind.HandlerValue)
                        {
                            key.SetValue(null, enable ? entry.OriginalClsidValue : DisabledValuePrefix + entry.OriginalClsidValue, RegistryValueKind.String);
                        }
                        else if (enable)
                        {
                            key.DeleteValue(LegacyDisableValue, throwOnMissingValue: false);
                        }
                        else
                        {
                            key.SetValue(LegacyDisableValue, string.Empty, RegistryValueKind.String);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ContextMenuManager: failed to toggle '{entry.HandlerKeyName}': {ex.Message}");
                return false;
            }

            entry.IsEnabled = enable;
            return true;
        }

        public static void RestartExplorer()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("explorer"))
                {
                    proc.Kill();
                }

                Process.Start("explorer.exe");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ContextMenuManager: failed to restart Explorer: {ex.Message}");
            }
        }

        public static bool IsClassicMenuDefault()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ClassicMenuRestoreKey, writable: false);
                return key != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Per-user switch Explorer reads at startup: an empty InprocServer32 under this CLSID makes the
        // classic menu the default right-click menu. Takes effect after an Explorer restart.
        public static bool SetClassicMenuDefault(bool classic)
        {
            try
            {
                if (classic)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(ClassicMenuRestoreKey, writable: true);
                    key.SetValue(null, string.Empty, RegistryValueKind.String);
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuRestoreKey.Substring(0, ClassicMenuRestoreKey.LastIndexOf('\\')), throwOnMissingSubKey: false);
                }

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ContextMenuManager: failed to switch the default context menu: {ex.Message}");
                return false;
            }
        }

        // Touches the disk and third-party binaries: call off the UI thread. Cached by spec across refreshes.
        public static MenuIcon LoadIcon(string spec) => string.IsNullOrWhiteSpace(spec) ? null : MenuIcon.FromPng(IconCache.GetOrAdd(spec, ReadIconPng));

        private static byte[] ReadIconPng(string spec)
        {
            try
            {
                // Package logos are image files; everything else is a registry icon location.
                return spec.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(spec)
                    ? File.ReadAllBytes(spec)
                    : ShellIconHelper.ExtractPng(spec, IconPixelSize);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetString(RegistryKey key, string name) => key?.GetValue(name) is string value ? value : null;

        // One entry per distinct handler, however many roots it is registered under - the same handler
        // under "*", "Directory" and "Drive" is one item in Explorer's menu, and disabling it in only
        // some roots is rarely what anyone wants.
        private static IEnumerable<ContextMenuEntry> EnumerateClassicEntries(RegistryHive hive)
        {
            var results = new Dictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
            RegistryKey baseKey = hive == RegistryHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
            ContextMenuEntryScope scope = hive == RegistryHive.CurrentUser ? ContextMenuEntryScope.CurrentUser : ContextMenuEntryScope.AllUsers;
            HashSet<string> blocked = ReadBlockedClsids(baseKey);

            foreach (var root in ContextMenuRoots)
            {
                string handlersPath = $"Software\\Classes\\{root}\\shellex\\ContextMenuHandlers";

                RegistryKey handlersKey;
                try
                {
                    handlersKey = baseKey.OpenSubKey(handlersPath, writable: false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (handlersKey == null)
                {
                    continue;
                }

                using (handlersKey)
                {
                    foreach (var handlerName in handlersKey.GetSubKeyNames())
                    {
                        RegistryKey handlerKey;
                        try
                        {
                            handlerKey = handlersKey.OpenSubKey(handlerName, writable: false);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (handlerKey == null)
                        {
                            continue;
                        }

                        using (handlerKey)
                        {
                            string rawValue = GetString(handlerKey, null);
                            string keyPath = $"{root}\\shellex\\ContextMenuHandlers\\{handlerName}";

                            // Explorer also accepts the CLSID as the key name itself (default value then
                            // just a label, or empty). There is no CLSID value to prefix, so these are
                            // disabled through the Blocked list instead.
                            if (Guid.TryParse(handlerName, out Guid keyGuid))
                            {
                                string blockedClsid = keyGuid.ToString("B");
                                string groupKey = $"blocked|{blockedClsid}";
                                if (!results.TryGetValue(groupKey, out var blockedEntry))
                                {
                                    ResolveClsid(blockedClsid, out string clsidName, out bool clsidWindowsOwned, out string clsidIcon);
                                    blockedEntry = new ContextMenuEntry
                                    {
                                        IconSpec = clsidIcon,
                                        DisplayName = FirstNonEmpty(clsidName, rawValue, handlerName),
                                        HandlerKeyName = handlerName,
                                        Scope = scope,
                                        Source = ContextMenuEntrySource.Classic,
                                        Kind = ContextMenuEntryKind.BlockedClsid,
                                        IsEnabled = !blocked.Contains(blockedClsid),
                                        IsLikelyWindowsOwned = clsidWindowsOwned,
                                    };
                                    blockedEntry.Clsids.Add(blockedClsid);
                                    results.Add(groupKey, blockedEntry);
                                }

                                blockedEntry.Roots.Add(root);
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(rawValue))
                            {
                                continue;
                            }

                            bool disabled = rawValue.StartsWith(DisabledValuePrefix, StringComparison.OrdinalIgnoreCase);
                            string clsid = disabled ? rawValue.Substring(DisabledValuePrefix.Length) : rawValue;
                            string handlerGroupKey = $"handler|{handlerName}|{clsid}";

                            if (!results.TryGetValue(handlerGroupKey, out var entry))
                            {
                                ResolveClsid(clsid, out string displayName, out bool isWindowsOwned, out string iconSpec);
                                entry = new ContextMenuEntry
                                {
                                    IconSpec = iconSpec,
                                    DisplayName = FirstNonEmpty(displayName, handlerName),
                                    HandlerKeyName = handlerName,
                                    Scope = scope,
                                    Source = ContextMenuEntrySource.Classic,
                                    Kind = ContextMenuEntryKind.HandlerValue,
                                    OriginalClsidValue = clsid,
                                    IsToggleable = !BuiltInHandlerDenylist.Contains(handlerName),
                                    IsLikelyWindowsOwned = isWindowsOwned,
                                };
                                results.Add(handlerGroupKey, entry);
                            }

                            // Shown as disabled only when every root is disabled; toggling rewrites all of them.
                            entry.IsEnabled |= !disabled;
                            entry.KeyPaths.Add(keyPath);
                            entry.Roots.Add(root);
                        }
                    }
                }
            }

            return results.Values;
        }

        // Static "shell\<verb>" entries (e.g. "Open with Visual Studio", "PowerShell 7"), plus the
        // items of their cascading submenus declared through ExtendedSubCommandsKey.
        private static IEnumerable<ContextMenuEntry> EnumerateVerbEntries(RegistryHive hive)
        {
            var results = new Dictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
            RegistryKey baseKey = hive == RegistryHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
            ContextMenuEntryScope scope = hive == RegistryHive.CurrentUser ? ContextMenuEntryScope.CurrentUser : ContextMenuEntryScope.AllUsers;

            foreach (var root in ContextMenuRoots)
            {
                AddVerbs(baseKey, scope, root, $"{root}\\shell", parent: null, depth: 0, results);
            }

            return results.Values;
        }

        private static void AddVerbs(RegistryKey baseKey, ContextMenuEntryScope scope, string root, string shellPath, ContextMenuEntry parent, int depth, Dictionary<string, ContextMenuEntry> results)
        {
            RegistryKey shellKey;
            try
            {
                shellKey = baseKey.OpenSubKey($"Software\\Classes\\{shellPath}", writable: false);
            }
            catch (Exception)
            {
                return;
            }

            if (shellKey == null)
            {
                return;
            }

            using (shellKey)
            {
                foreach (var verbName in shellKey.GetSubKeyNames())
                {
                    try
                    {
                        using var verbKey = shellKey.OpenSubKey(verbName, writable: false);

                        // Hidden from the menu by design - nothing for the user to toggle.
                        if (verbKey == null || verbKey.GetValue("ProgrammaticAccessOnly") != null)
                        {
                            continue;
                        }

                        string label = ResolveVerbLabel(verbKey, verbName);

                        // Top-level verbs group by key name across roots; submenu items by parent, since
                        // grouped parents share one ExtendedSubCommandsKey and must not list children twice.
                        string groupKey = parent == null ? $"verb|{verbName}" : $"{parent.SortKey}{SortKeySeparator}{verbName}";
                        bool isNew = !results.TryGetValue(groupKey, out var entry);
                        if (isNew)
                        {
                            entry = new ContextMenuEntry
                            {
                                DisplayName = parent == null ? label : $"{parent.DisplayName} > {label}",
                                HandlerKeyName = verbName,
                                Scope = scope,
                                Source = ContextMenuEntrySource.Classic,
                                Kind = ContextMenuEntryKind.Verb,
                                SortKey = parent == null ? label : $"{parent.SortKey}{SortKeySeparator}{label}",
                                IconSpec = GetString(verbKey, "Icon"),
                                Parent = parent,
                                Position = GetString(verbKey, "Position"),
                                IsLikelyWindowsOwned = parent?.IsLikelyWindowsOwned ?? IsWindowsOwnedVerb(verbKey),
                            };
                            results.Add(groupKey, entry);
                        }

                        string verbPath = $"{shellPath}\\{verbName}";
                        if (!entry.KeyPaths.Contains(verbPath, StringComparer.OrdinalIgnoreCase))
                        {
                            entry.KeyPaths.Add(verbPath);
                            entry.IsEnabled |= verbKey.GetValue(LegacyDisableValue) == null;
                        }

                        // Submenu items sit under their parent row, which already lists the roots.
                        if (parent == null && !entry.Roots.Contains(root))
                        {
                            entry.Roots.Add(root);
                        }

                        string subCommandsKey = GetString(verbKey, "ExtendedSubCommandsKey");
                        if (isNew && depth < MaxSubmenuDepth && !string.IsNullOrWhiteSpace(subCommandsKey))
                        {
                            AddVerbs(baseKey, scope, root, $"{subCommandsKey.Trim('\\')}\\shell", entry, depth + 1, results);
                        }
                    }
                    catch (Exception)
                    {
                        // One unreadable verb must not hide the rest.
                        continue;
                    }
                }
            }
        }

        // Explorer's label precedence: MUIVerb, then the default value, then the key name. Either
        // can be an indirect "@dll,-id" string, and "&" marks the access key.
        private static string ResolveVerbLabel(RegistryKey verbKey, string verbName)
        {
            string raw = FirstNonEmpty(GetString(verbKey, "MUIVerb"), GetString(verbKey, null), verbName);
            if (raw.StartsWith('@'))
            {
                var buffer = new StringBuilder(512);
                if (NativeMethods.SHLoadIndirectString(raw, buffer, buffer.Capacity, IntPtr.Zero) == 0)
                {
                    raw = buffer.ToString();
                }
            }

            return raw.Replace("&&", "\u0000").Replace("&", string.Empty).Replace("\u0000", "&");
        }

        // Windows 11's new menu shows Windows' own static verbs (Display settings, Personalize) and
        // leaves everyone else's to "Show more options". The registry has no owner flag, so a verb
        // counts as Windows' own when every file it names - label, icon, command, handler DLL - is
        // under the Windows directory, and it names at least one.
        private static bool IsWindowsOwnedVerb(RegistryKey verbKey)
        {
            var files = new List<string>
            {
                GetString(verbKey, "MUIVerb"),
                GetString(verbKey, null),
                GetString(verbKey, "Icon"),
                ClsidServerPath(GetString(verbKey, "ExplorerCommandHandler")),
            };

            using (var commandKey = verbKey.OpenSubKey("command", writable: false))
            {
                files.Add(GetString(commandKey, null));
                files.Add(ClsidServerPath(GetString(commandKey, "DelegateExecute")));
            }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
            var paths = files.Select(FilePart).Where(p => p != null).ToList();
            return paths.Count > 0 && paths.All(p => p.StartsWith(windows, StringComparison.OrdinalIgnoreCase));
        }

        // The file in a label ("@dll,-id"), icon ("file,index") or command line; null for plain text.
        private static string FilePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string text = Environment.ExpandEnvironmentVariables(value.Trim().TrimStart('@'));
            string file = text.StartsWith('"')
                ? text.Substring(1, Math.Max(text.IndexOf('"', 1) - 1, 0))
                : text.Split(',', ' ')[0];
            return Path.IsPathFullyQualified(file) ? file : null;
        }

        private static string ClsidServerPath(string clsid)
        {
            if (string.IsNullOrWhiteSpace(clsid))
            {
                return null;
            }

            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}\\InprocServer32", writable: false)
                    ?? Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}\\LocalServer32", writable: false);
                return GetString(key, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static HashSet<string> ReadBlockedClsids(RegistryKey baseKey)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = baseKey.OpenSubKey(BlockedKeyPath, writable: false);
                if (key != null)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        if (Guid.TryParse(name, out Guid guid))
                        {
                            result.Add(guid.ToString("B"));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable Blocked list - treat everything as enabled.
            }

            return result;
        }

        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // CLSID lookup uses HKEY_CLASSES_ROOT, the OS's own merged view of HKCU+HKLM Software\Classes,
        // since a handler found under either hive can point at a CLSID registered in either.
        // iconSpec: the CLSID's DefaultIcon, else the handler DLL itself. The items a handler actually
        // adds draw their own bitmaps at runtime, so this is only the closest static stand-in.
        private static void ResolveClsid(string clsid, out string displayName, out bool isWindowsOwned, out string iconSpec)
        {
            displayName = null;
            isWindowsOwned = false;
            iconSpec = null;

            if (string.IsNullOrWhiteSpace(clsid))
            {
                return;
            }

            try
            {
                using var clsidKey = Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}", writable: false);
                if (clsidKey == null)
                {
                    return;
                }

                displayName = GetString(clsidKey, null);

                using (var defaultIconKey = clsidKey.OpenSubKey("DefaultIcon", writable: false))
                {
                    iconSpec = GetString(defaultIconKey, null);
                }

                using var inprocKey = clsidKey.OpenSubKey("InprocServer32", writable: false);
                string dllPath = GetString(inprocKey, null);
                if (string.IsNullOrWhiteSpace(dllPath))
                {
                    return;
                }

                string expandedPath = Environment.ExpandEnvironmentVariables(dllPath.Trim('"'));
                iconSpec ??= expandedPath;

                string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                isWindowsOwned = expandedPath.StartsWith(systemRoot + "\\System32", StringComparison.OrdinalIgnoreCase)
                    || expandedPath.StartsWith(systemRoot + "\\SysWOW64", StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    try
                    {
                        displayName = FileVersionInfo.GetVersionInfo(expandedPath).FileDescription;
                    }
                    catch (Exception)
                    {
                        // Missing/inaccessible DLL - fall back to the handler key name at the call site.
                    }
                }
            }
            catch (Exception)
            {
                // Malformed/inaccessible CLSID entry - leave displayName null, caller falls back to the handler key name.
            }
        }

        // Windows 11 packaged (sparse-package) context-menu entries, one per package. Their verbs are
        // IExplorerCommand CLSIDs declared in the manifest; Explorer skips any CLSID on the per-user
        // "Shell Extensions\Blocked" list, which is how these are toggled.
        private static IEnumerable<ContextMenuEntry> EnumerateModernEntries()
        {
            var results = new List<ContextMenuEntry>();
            HashSet<string> blocked = ReadBlockedClsids(Registry.CurrentUser);

            try
            {
                var packageManager = new PackageManager();
                foreach (Package package in packageManager.FindPackagesForUser(string.Empty))
                {
                    // One bad/corrupted package registration must not abort enumeration of the rest -
                    // every property access below (IsFramework, InstalledPath, DisplayName, Id) can
                    // throw for an individual package.
                    try
                    {
                        if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
                        {
                            continue;
                        }

                        string manifestPath = Path.Combine(package.InstalledPath, "AppxManifest.xml");
                        if (!File.Exists(manifestPath))
                        {
                            continue;
                        }

                        XDocument manifest = XDocument.Load(manifestPath);
                        var extensions = manifest.Descendants()
                            .Where(el => el.Name.LocalName == "Extension" && (string)el.Attribute("Category") == "windows.fileExplorerContextMenus")
                            .ToList();
                        var clsids = extensions
                            .SelectMany(el => el.Descendants().Where(v => v.Name.LocalName == "Verb"))
                            .Select(v => Guid.TryParse((string)v.Attribute("Clsid"), out Guid guid) ? guid.ToString("B") : null)
                            .Where(c => c != null)
                            .Distinct()
                            .ToList();

                        if (clsids.Count == 0)
                        {
                            continue;
                        }

                        var entry = new ContextMenuEntry
                        {
                            DisplayName = package.DisplayName,
                            HandlerKeyName = package.Id.FamilyName,
                            Scope = ContextMenuEntryScope.CurrentUser,
                            Source = ContextMenuEntrySource.Modern,
                            Kind = ContextMenuEntryKind.BlockedClsid,
                            IsEnabled = !clsids.Any(blocked.Contains),
                            IconSpec = package.Logo?.LocalPath,
                        };
                        entry.Clsids.AddRange(clsids);
                        entry.ItemTypes.AddRange(extensions
                            .SelectMany(el => el.Descendants().Where(t => t.Name.LocalName == "ItemType"))
                            .Select(t => (string)t.Attribute("Type"))
                            .Where(t => !string.IsNullOrEmpty(t))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                        results.Add(entry);
                    }
                    catch (Exception)
                    {
                        // Skip this one package, keep enumerating the rest.
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ContextMenuManager: failed to enumerate modern context menu packages: {ex.Message}");
            }

            return results;
        }
    }
}
