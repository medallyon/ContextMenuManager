// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ContextMenuManager.MenuCapture
{
    // Builds the real Explorer context menu for a sample target and prints it as JSON:
    // {
    //   "items": [{ "text", "shortcut", "verb", "disabled", "separator", "icon": { "w", "h", "bgra" }, "children": [...] }],
    //   "handlers": { "<clsid>": ["top-level text", ...] },
    //   "commands": { "<clsid>": [{ "text", "iconSpec", "hidden", "disabled", "separator", "children": [...] }] }
    // }
    // "handlers" answers which extension added which item: every CLSID passed with --probe is loaded
    // on its own against the same sample and asked for its items. Explorer's merged menu keeps no
    // record of that.
    // "commands" covers Windows 11's packaged verbs (IExplorerCommand, passed with --commands), which
    // only the new menu shows: each is asked for what Explorer draws. A CLSID that fails to load is left out.
    //
    // Runs as its own process on purpose: building the menu loads every registered shell
    // extension, and a misbehaving third-party DLL must not be able to take the app down.
    // A fresh process also has no cached handlers, so the result reflects the registry right now.
    // PIDLs and COM pointers are left to process exit on purpose.
    internal static class Program
    {
        private const int MaxDepth = 4;
        private const uint FirstCommandId = 1;

        // The right-clicked object as a shell extension sees it in IShellExtInit.Initialize.
        private sealed class Sample
        {
            public IContextMenu Menu { get; init; }

            public IntPtr FolderPidl { get; init; }

            public IntPtr DataObject { get; init; }

            // File system path of the right-clicked object; the folder itself for a background.
            public string Path { get; set; }
        }

        private sealed class Command
        {
            public string Text { get; init; }

            public string Icon { get; init; }

            public bool IsSeparator { get; init; }

            public bool IsHidden { get; init; }

            public bool IsDisabled { get; init; }

            public List<Command> Children { get; } = new List<Command>();
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string target = null;
            string probe = null;
            string commands = null;
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--target": target = args[i + 1]; break;
                    case "--probe": probe = args[i + 1]; break;
                    case "--commands": commands = args[i + 1]; break;
                }
            }

            if (target == null)
            {
                Console.Error.WriteLine("Usage: --target desktop|background|folder|file|drive [--probe {clsid},{clsid}...] [--commands {clsid},{clsid}...]");
                return 2;
            }

            IntPtr owner = NativeMethods.CreateWindowEx(0, "Static", string.Empty, 0, 0, 0, 0, 0, NativeMethods.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Sample sample = CreateSample(target, owner);
            if (sample?.Menu == null)
            {
                Console.Error.WriteLine($"Could not create a context menu for '{target}'.");
                return 1;
            }

            IntPtr hmenu = NativeMethods.CreatePopupMenu();
            try
            {
                int hr = sample.Menu.QueryContextMenu(hmenu, 0, FirstCommandId, 0x7FFF, NativeMethods.CMF_NORMAL);
                if (hr < 0)
                {
                    Console.Error.WriteLine($"QueryContextMenu failed: 0x{hr:X8}");
                    return 1;
                }

                using var stdout = Console.OpenStandardOutput();
                using var writer = new Utf8JsonWriter(stdout);
                writer.WriteStartObject();
                writer.WritePropertyName("items");
                WriteMenu(writer, hmenu, sample.Menu, 0);
                writer.WriteStartObject("handlers");
                foreach (string clsid in (probe ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var texts = ProbeHandler(clsid, sample);
                    writer.WriteStartArray(clsid);
                    texts.ForEach(writer.WriteStringValue);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
                writer.WriteStartObject("commands");
                IntPtr shellItems = CreateItemArray(sample.Path);
                foreach (string clsid in (commands ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    Command command = LoadCommand(clsid, shellItems);
                    if (command != null)
                    {
                        writer.WriteStartArray(clsid);
                        WriteCommand(writer, command);
                        writer.WriteEndArray();
                    }
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.Flush();
                return 0;
            }
            finally
            {
                NativeMethods.DestroyMenu(hmenu);
            }
        }

        // Loads one handler the way Explorer does and lists the top-level items it adds.
        private static List<string> ProbeHandler(string clsid, Sample sample)
        {
            var texts = new List<string>();
            object handler = null;
            IntPtr hmenu = NativeMethods.CreatePopupMenu();
            try
            {
                handler = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid.Parse(clsid), throwOnError: true));
                if (handler is IShellExtInit init && init.Initialize(sample.FolderPidl, sample.DataObject, IntPtr.Zero) < 0)
                {
                    return texts;
                }

                if (handler is not IContextMenu menu || menu.QueryContextMenu(hmenu, 0, FirstCommandId, 0x7FFF, NativeMethods.CMF_NORMAL) < 0)
                {
                    return texts;
                }

                int count = NativeMethods.GetMenuItemCount(hmenu);
                for (uint i = 0; i < count; i++)
                {
                    if (ReadItem(hmenu, i, out var info, out string text, out _) && (info.fType & NativeMethods.MFT_SEPARATOR) == 0 && text.Length > 0)
                    {
                        texts.Add(text);
                    }
                }
            }
            catch (Exception ex)
            {
                // Not registered, wrong bitness, or refuses to run outside Explorer: the item stays unattributed.
                Console.Error.WriteLine($"Probe {clsid}: {ex.Message}");
            }
            finally
            {
                NativeMethods.DestroyMenu(hmenu);
                if (handler != null && Marshal.IsComObject(handler))
                {
                    Marshal.FinalReleaseComObject(handler);
                }
            }

            return texts;
        }

        private static Sample CreateSample(string target, IntPtr owner)
        {
            string sampleRoot = Path.Combine(Path.GetTempPath(), "ContextMenuManager", "Preview");
            string sampleFolder = Path.Combine(sampleRoot, "Sample folder");
            string sampleFile = Path.Combine(sampleRoot, "Sample file.txt");
            Directory.CreateDirectory(sampleFolder);
            if (!File.Exists(sampleFile))
            {
                File.WriteAllText(sampleFile, string.Empty);
            }

            string drive = Path.GetPathRoot(Environment.SystemDirectory);
            Sample sample = target switch
            {
                "desktop" => BackgroundMenu(null, owner),
                "background" => BackgroundMenu(sampleRoot, owner),
                "folder" => ItemMenu(sampleFolder, owner),
                "file" => ItemMenu(sampleFile, owner),
                "drive" => ItemMenu(drive, owner),
                _ => null,
            };

            if (sample != null)
            {
                sample.Path = target switch
                {
                    "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "background" => sampleRoot,
                    "folder" => sampleFolder,
                    "file" => sampleFile,
                    _ => drive,
                };
            }

            return sample;
        }

        // The selection an IExplorerCommand is asked about: the one right-clicked object.
        private static IntPtr CreateItemArray(string path)
        {
            Guid itemId = NativeMethods.IID_IShellItem;
            Guid arrayId = NativeMethods.IID_IShellItemArray;
            if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out IntPtr item) < 0)
            {
                return IntPtr.Zero;
            }

            return NativeMethods.SHCreateShellItemArrayFromShellItem(item, ref arrayId, out IntPtr array) < 0 ? IntPtr.Zero : array;
        }

        private static Command LoadCommand(string clsid, IntPtr shellItems)
        {
            try
            {
                var command = (IExplorerCommand)Activator.CreateInstance(Type.GetTypeFromCLSID(Guid.Parse(clsid), throwOnError: true));
                return ReadCommand(command, shellItems, 0);
            }
            catch (Exception ex)
            {
                // Not registered, or the package's COM server would not start: the entry keeps its package name.
                Console.Error.WriteLine($"Command {clsid}: {ex.Message}");
                return null;
            }
        }

        // What Explorer asks a command before drawing it. Hidden subcommands are dropped here; a hidden
        // top-level command is reported so the caller can tell it from one that failed to load.
        private static Command ReadCommand(IExplorerCommand command, IntPtr shellItems, int depth)
        {
            if (command.GetFlags(out uint flags) < 0)
            {
                flags = 0;
            }

            if ((flags & NativeMethods.ECF_ISSEPARATOR) != 0)
            {
                return new Command { IsSeparator = true };
            }

            if (command.GetState(shellItems, true, out uint state) < 0)
            {
                state = 0;
            }

            var result = new Command
            {
                Text = command.GetTitle(shellItems, out string title) >= 0 && title != null ? StripAccessKeys(title) : null,
                Icon = command.GetIcon(shellItems, out string icon) >= 0 ? icon : null,
                IsHidden = (state & NativeMethods.ECS_HIDDEN) != 0,
                IsDisabled = (state & NativeMethods.ECS_DISABLED) != 0,
            };

            if ((flags & NativeMethods.ECF_HASSUBCOMMANDS) != 0 && depth < MaxDepth && command.EnumSubCommands(out IEnumExplorerCommand subCommands) >= 0 && subCommands != null)
            {
                // Bounded, in case an enumerator never reports the end.
                for (int i = 0; i < 100 && subCommands.Next(1, out IExplorerCommand subCommand, out uint fetched) == 0 && fetched == 1; i++)
                {
                    try
                    {
                        Command child = ReadCommand(subCommand, shellItems, depth + 1);
                        if (!child.IsHidden)
                        {
                            result.Children.Add(child);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Subcommand {i}: {ex.Message}");
                    }
                }
            }

            return result;
        }

        private static void WriteCommand(Utf8JsonWriter writer, Command command)
        {
            writer.WriteStartObject();
            if (command.IsSeparator)
            {
                writer.WriteBoolean("separator", true);
                writer.WriteEndObject();
                return;
            }

            writer.WriteString("text", command.Text ?? string.Empty);
            if (!string.IsNullOrEmpty(command.Icon))
            {
                writer.WriteString("iconSpec", command.Icon);
            }

            if (command.IsHidden)
            {
                writer.WriteBoolean("hidden", true);
            }

            if (command.IsDisabled)
            {
                writer.WriteBoolean("disabled", true);
            }

            if (command.Children.Count > 0)
            {
                writer.WriteStartArray("children");
                command.Children.ForEach(child => WriteCommand(writer, child));
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        // Right-click on an item: the parent folder hands out the item's menu and data object.
        private static Sample ItemMenu(string path, IntPtr owner)
        {
            if (NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) < 0)
            {
                return null;
            }

            Guid shellFolderId = NativeMethods.IID_IShellFolder;
            Guid contextMenuId = NativeMethods.IID_IContextMenu;
            Guid dataObjectId = NativeMethods.IID_IDataObject;
            if (NativeMethods.SHBindToParent(pidl, ref shellFolderId, out IntPtr parentPtr, out IntPtr childPidl) < 0)
            {
                return null;
            }

            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            Marshal.Release(parentPtr);
            if (parent.GetUIObjectOf(owner, 1, new[] { childPidl }, ref contextMenuId, IntPtr.Zero, out IntPtr menuPtr) < 0)
            {
                return null;
            }

            _ = parent.GetUIObjectOf(owner, 1, new[] { childPidl }, ref dataObjectId, IntPtr.Zero, out IntPtr dataObject);
            return new Sample { Menu = ToContextMenu(menuPtr), DataObject = dataObject };
        }

        // Right-click on empty space: the folder's own view object. null folder = the desktop.
        private static Sample BackgroundMenu(string folderPath, IntPtr owner)
        {
            if (NativeMethods.SHGetDesktopFolder(out IntPtr desktopPtr) < 0)
            {
                return null;
            }

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
            Marshal.Release(desktopPtr);

            // The desktop's absolute PIDL is the empty one: a single zero terminator.
            IntPtr pidl = Marshal.AllocCoTaskMem(2);
            Marshal.WriteInt16(pidl, 0);
            if (folderPath != null)
            {
                if (NativeMethods.SHParseDisplayName(folderPath, IntPtr.Zero, out pidl, 0, out _) < 0)
                {
                    return null;
                }

                Guid shellFolderId = NativeMethods.IID_IShellFolder;
                if (folder.BindToObject(pidl, IntPtr.Zero, ref shellFolderId, out IntPtr folderPtr) < 0)
                {
                    return null;
                }

                folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
                Marshal.Release(folderPtr);
            }

            Guid contextMenuId = NativeMethods.IID_IContextMenu;
            return folder.CreateViewObject(owner, ref contextMenuId, out IntPtr menuPtr) < 0
                ? null
                : new Sample { Menu = ToContextMenu(menuPtr), FolderPidl = pidl };
        }

        private static IContextMenu ToContextMenu(IntPtr ptr)
        {
            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            return menu;
        }

        private static void WriteMenu(Utf8JsonWriter writer, IntPtr hmenu, IContextMenu menu, int depth)
        {
            writer.WriteStartArray();
            int count = NativeMethods.GetMenuItemCount(hmenu);
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    WriteItem(writer, hmenu, i, menu, depth);
                }
                catch (Exception ex)
                {
                    // One broken item must not lose the rest of the menu.
                    Console.Error.WriteLine($"Item {i}: {ex.Message}");
                }
            }

            writer.WriteEndArray();
        }

        // Reads one item; text comes back without its "&" access-key markers and split at the tab
        // Explorer puts before an accelerator.
        private static bool ReadItem(IntPtr hmenu, uint position, out NativeMethods.MENUITEMINFO info, out string text, out string shortcut)
        {
            info = new NativeMethods.MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
                fMask = NativeMethods.MIIM_FTYPE | NativeMethods.MIIM_STATE | NativeMethods.MIIM_ID | NativeMethods.MIIM_SUBMENU
                    | NativeMethods.MIIM_CHECKMARKS | NativeMethods.MIIM_BITMAP | NativeMethods.MIIM_STRING,
            };
            text = string.Empty;
            shortcut = null;

            // First call reports the text length, second one fills the buffer.
            if (!NativeMethods.GetMenuItemInfo(hmenu, position, true, ref info))
            {
                return false;
            }

            if ((info.fType & NativeMethods.MFT_SEPARATOR) != 0)
            {
                return true;
            }

            if (info.cch > 0)
            {
                info.cch++;
                info.dwTypeData = Marshal.AllocHGlobal((int)info.cch * 2);
                try
                {
                    if (NativeMethods.GetMenuItemInfo(hmenu, position, true, ref info))
                    {
                        text = Marshal.PtrToStringUni(info.dwTypeData) ?? string.Empty;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(info.dwTypeData);
                    info.dwTypeData = IntPtr.Zero;
                }
            }

            int tab = text.IndexOf('\t');
            if (tab >= 0)
            {
                shortcut = text.Substring(tab + 1);
                text = text.Substring(0, tab);
            }

            text = StripAccessKeys(text);
            return true;
        }

        private static string StripAccessKeys(string text) => text.Replace("&&", "\u0000").Replace("&", string.Empty).Replace("\u0000", "&");

        private static void WriteItem(Utf8JsonWriter writer, IntPtr hmenu, uint position, IContextMenu menu, int depth)
        {
            if (!ReadItem(hmenu, position, out var info, out string text, out string shortcut))
            {
                return;
            }

            if ((info.fType & NativeMethods.MFT_SEPARATOR) != 0)
            {
                writer.WriteStartObject();
                writer.WriteBoolean("separator", true);
                writer.WriteEndObject();
                return;
            }

            string verb = info.hSubMenu == IntPtr.Zero ? GetVerb(menu, info.wID) : null;

            writer.WriteStartObject();
            writer.WriteString("text", string.IsNullOrEmpty(text) ? verb ?? string.Empty : text);
            if (shortcut != null)
            {
                writer.WriteString("shortcut", shortcut);
            }

            if (verb != null)
            {
                writer.WriteString("verb", verb);
            }

            if ((info.fState & NativeMethods.MFS_DISABLED) != 0)
            {
                writer.WriteBoolean("disabled", true);
            }

            // Modern menus put the icon in hbmpItem; older handlers still use the unchecked-state bitmap.
            WriteIcon(writer, info.hbmpItem);
            if (info.hbmpItem == IntPtr.Zero)
            {
                WriteIcon(writer, info.hbmpUnchecked);
            }

            if (info.hSubMenu != IntPtr.Zero && depth < MaxDepth)
            {
                // Many handlers (Send to, Open with, TortoiseGit) fill submenus only when they open.
                if (menu is IContextMenu3 menu3)
                {
                    menu3.HandleMenuMsg2(NativeMethods.WM_INITMENUPOPUP, info.hSubMenu, new IntPtr(position), out _);
                }
                else if (menu is IContextMenu2 menu2)
                {
                    menu2.HandleMenuMsg(NativeMethods.WM_INITMENUPOPUP, info.hSubMenu, new IntPtr(position));
                }

                writer.WritePropertyName("children");
                WriteMenu(writer, info.hSubMenu, menu, depth + 1);
            }

            writer.WriteEndObject();
        }

        private static string GetVerb(IContextMenu menu, uint id)
        {
            if (id < FirstCommandId || id > 0x7FFF)
            {
                return null;
            }

            const int chars = 256;
            IntPtr buffer = Marshal.AllocHGlobal(chars * 2);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                return menu.GetCommandString(new UIntPtr(id - FirstCommandId), NativeMethods.GCS_VERBW, IntPtr.Zero, buffer, chars) >= 0
                    ? Marshal.PtrToStringUni(buffer) is { Length: > 0 } verb ? verb : null
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Writes a 32bpp top-down BGRA copy of a menu bitmap. HBMMENU_* system glyphs (small
        // sentinel values, and -1 for owner-drawn) carry no pixels and are skipped.
        private static void WriteIcon(Utf8JsonWriter writer, IntPtr bitmap)
        {
            if (bitmap == IntPtr.Zero || (long)bitmap is >= -1 and <= 11)
            {
                return;
            }

            if (NativeMethods.GetObject(bitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out NativeMethods.BITMAP bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0 || bm.bmWidth > 256 || bm.bmHeight > 256)
            {
                return;
            }

            int width = bm.bmWidth;
            int height = Math.Abs(bm.bmHeight);
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
            };

            byte[] pixels = new byte[width * height * 4];
            IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                if (NativeMethods.GetDIBits(hdc, bitmap, 0, (uint)height, pixels, ref header, NativeMethods.DIB_RGB_COLORS) == 0)
                {
                    return;
                }
            }
            finally
            {
                _ = NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
            }

            // Bitmaps without an alpha channel come back fully transparent; treat them as opaque.
            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length && !hasAlpha; i += 4)
            {
                hasAlpha = pixels[i] != 0;
            }

            if (!hasAlpha)
            {
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            writer.WriteStartObject("icon");
            writer.WriteNumber("w", width);
            writer.WriteNumber("h", height);
            writer.WriteBase64String("bgra", pixels);
            writer.WriteEndObject();
        }
    }
}
