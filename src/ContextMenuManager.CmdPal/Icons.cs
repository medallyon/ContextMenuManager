// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.IO;

using ContextMenuManager.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ContextMenuManager.CmdPal;

// Icons go to Command Palette as in-memory streams it reads from its own process: a wrapped
// MemoryStream can't serve that, and a file path from the package didn't show.
internal static class Icons
{
    private static readonly byte[] AppPng = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "StoreLogo.png"));

    private static readonly Tag OffTag = new("Off");

    // A new stream per use: every item that shows the icon reads its own copy.
    public static IconInfo App => FromPng(AppPng);

    public static ITag[] OffTags(ContextMenuEntry? entry) => entry is { IsEnabled: false } ? [OffTag] : [];

    public static IconInfo FromPng(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(png);
        writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        return IconInfo.FromStream(stream);
    }

    // Blocks on the encoder: call off the COM thread.
    public static IconInfo? FromMenuIcon(MenuIcon? icon)
    {
        if (icon == null)
        {
            return null;
        }

        if (icon.Png != null)
        {
            return FromPng(icon.Png);
        }

        var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)icon.Width, (uint)icon.Height, 96, 96, icon.Bgra);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        return IconInfo.FromStream(stream);
    }
}
