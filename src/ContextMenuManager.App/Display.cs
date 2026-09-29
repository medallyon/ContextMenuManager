// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;

using ContextMenuManager.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ContextMenuManager.App
{
    // Localized text and images for Core's plain models. Used from x:Bind function bindings, so
    // everything here runs on the UI thread.
    public static class Display
    {
        private static readonly ResourceLoader Loader = new ResourceLoader();

        private static readonly ConditionalWeakTable<MenuIcon, ImageSource> Images = new ConditionalWeakTable<MenuIcon, ImageSource>();

        public static string GetString(string key) => Loader.GetString(key);

        public static string SideText(bool isOff, string shortcut) => isOff ? GetString("PreviewOff") : shortcut;

        public static string ScopeAndSource(ContextMenuEntry entry)
        {
            string scope = GetString(entry.Scope == ContextMenuEntryScope.CurrentUser ? "ScopeCurrentUser" : "ScopeAllUsers");
            string source = GetString(entry.Source == ContextMenuEntrySource.Classic ? "SourceClassic" : "SourceModern");
            string text = $"{scope} · {source}";
            if (entry.Roots.Count == 0)
            {
                return text;
            }

            return $"{text} · {string.Join(", ", entry.Roots.Select(RootName).Distinct())}";
        }

        public static ImageSource Image(MenuIcon icon) => icon == null ? null : Images.GetValue(icon, CreateImage);

        private static string RootName(string root) => root switch
        {
            "*" => GetString("RootFiles"),
            "Directory" => GetString("RootFolders"),
            "Directory\\Background" => GetString("RootBackground"),
            "AllFilesystemObjects" => GetString("RootFilesAndFolders"),
            "Drive" => GetString("RootDrives"),
            "DesktopBackground" => GetString("RootDesktop"),
            _ => root,
        };

        // Null for undecodable data.
        private static ImageSource CreateImage(MenuIcon icon)
        {
            try
            {
                if (icon.Png != null)
                {
                    // The stream is left to the GC on purpose: BitmapImage may still be reading it after SetSource returns.
                    var image = new BitmapImage { DecodePixelWidth = ContextMenuRegistry.IconPixelSize };
                    image.SetSource(new MemoryStream(icon.Png).AsRandomAccessStream());
                    return image;
                }

                // Premultiplied 32bpp top-down pixels are WriteableBitmap's own layout.
                var bitmap = new WriteableBitmap(icon.Width, icon.Height);
                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(icon.Bgra, 0, icon.Bgra.Length);
                }

                bitmap.Invalidate();
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
