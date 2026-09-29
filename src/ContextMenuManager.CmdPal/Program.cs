// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.Threading;

using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace ContextMenuManager.CmdPal;

public static class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        // Command Palette starts the exe through the COM registration in Package.appxmanifest.
        if (args.Length == 0 || args[0] != "-RegisterProcessAsComServer")
        {
            Console.WriteLine("Context Menu Manager: this is a Command Palette extension, open it from Command Palette.");
            return;
        }

        ComServer server = new();
        ManualResetEvent disposedEvent = new(false);

        // One instance for the process lifetime; the process exits when Command Palette disposes it.
        ContextMenuManagerExtension extension = new(disposedEvent);
        server.RegisterClass<ContextMenuManagerExtension, IExtension>(() => extension);
        server.Start();

        disposedEvent.WaitOne();
        server.Stop();
        server.UnsafeDispose();
    }
}
