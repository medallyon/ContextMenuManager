// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace ContextMenuManager.Core
{
    // Which right-click the preview shows.
    public enum ContextMenuPreviewTarget
    {
        Desktop,
        FolderBackground,
        Folder,
        File,
        Drive,
    }

    // Icon pixels as the capture helper or an icon file delivers them; the UI turns them into an image.
    // Either PNG file bytes, or premultiplied 32bpp top-down BGRA pixels of Width x Height.
    public sealed class MenuIcon
    {
        private MenuIcon(byte[] png, int width, int height, byte[] bgra)
        {
            Png = png;
            Width = width;
            Height = height;
            Bgra = bgra;
        }

        public byte[] Png { get; }

        public int Width { get; }

        public int Height { get; }

        public byte[] Bgra { get; }

        public static MenuIcon FromPng(byte[] png) => png == null ? null : new MenuIcon(png, 0, 0, null);

        // Null for pixel data that does not match the size.
        public static MenuIcon FromBgra(int width, int height, byte[] bgra) =>
            width <= 0 || height <= 0 || bgra.Length != width * height * 4 ? null : new MenuIcon(null, width, height, bgra);
    }

    // One row of the context menu captured by the MenuCapture helper process, linked to the entry
    // that adds it when one could be matched.
    public class ContextMenuPreviewItem
    {
        private MenuIcon _icon;

        public string Text { get; set; }

        // Right-aligned accelerator, e.g. "Ctrl+Z".
        public string Shortcut { get; set; }

        // Canonical verb the menu reports for the item; for a static verb, its registry key name.
        public string Verb { get; set; }

        // The item's own bitmap from the captured menu, else the entry's icon (which may load later).
        public MenuIcon Icon
        {
            get => _icon ?? Entry?.Icon;
            set => _icon = value;
        }

        public bool IsSeparator { get; set; }

        // Section caption, e.g. above the entries that are turned off.
        public bool IsHeader { get; set; }

        // Windows 11's "Show more options" row, which switches the preview to the classic menu.
        public bool IsShowMoreOptions { get; set; }

        public bool IsDisabled { get; set; }

        // The registry entry that adds this item; null for Explorer's own items and unmatched ones.
        public ContextMenuEntry Entry { get; set; }

        public List<ContextMenuPreviewItem> Children { get; } = new List<ContextMenuPreviewItem>();

        public bool HasChildren => Children.Count > 0;

        public bool IsItem => !IsSeparator && !IsHeader;

        public bool IsOff => Entry != null && !Entry.IsEnabled;

        public double TextOpacity => IsOff ? 0.45 : IsDisabled ? 0.6 : 1.0;

        public static ContextMenuPreviewItem Separator() => new ContextMenuPreviewItem { IsSeparator = true };
    }
}
