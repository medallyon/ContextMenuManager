// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using Microsoft.CommandPalette.Extensions;

namespace ContextMenuManager.CmdPal;

// Must match the com:Class and CreateInstance ids in Package.appxmanifest.
[Guid("36011C40-913C-4903-B2BF-4C9ACAED91CB")]
public sealed partial class ContextMenuManagerExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposedEvent;

    private readonly CommandsProvider _provider = new();

    public ContextMenuManagerExtension(ManualResetEvent disposedEvent)
    {
        _disposedEvent = disposedEvent;
    }

    public object? GetProvider(ProviderType providerType) => providerType == ProviderType.Commands ? _provider : null;

    public void Dispose() => _disposedEvent.Set();
}
