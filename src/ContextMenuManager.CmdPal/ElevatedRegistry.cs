// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using ContextMenuManager.Core;

namespace ContextMenuManager.CmdPal;

// Writes HKLM through an elevated Windows PowerShell, one UAC prompt per call. The extension can't
// elevate itself: Command Palette starts it as an unelevated COM server.
internal static class ElevatedRegistry
{
    // False when the user cancels the UAC prompt or any write fails.
    public static bool ApplyToLocalMachine(IReadOnlyList<RegistryWrite> writes)
    {
        var script = new StringBuilder("$ErrorActionPreference = 'Stop'\n$root = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')\n");
        foreach (var write in writes)
        {
            string open = write.CreateKey ? $"$root.CreateSubKey({Quote(write.KeyPath)})" : $"$root.OpenSubKey({Quote(write.KeyPath)}, $true)";
            string change = write.Value == null
                ? $"$key.DeleteValue({Quote(write.Name)}, $false)"
                : $"$key.SetValue({Quote(write.Name)}, {Quote(write.Value)}, 'String')";
            script.Append($"$key = {open}\nif ($key) {{ {change}; $key.Dispose() }}\n");
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            // EncodedCommand is UTF-16 base64, so no value needs command-line escaping.
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString())),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // UAC prompt cancelled.
            return false;
        }
    }

    // PowerShell single-quoted string. PowerShell also ends a string at typographic single quotes,
    // which display names contain ("VLC media player’s"), so those are doubled too.
    // Null is the key's default value, which .NET names "".
    private static string Quote(string? value)
    {
        var quoted = new StringBuilder("'");
        foreach (char c in value ?? string.Empty)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                quoted.Append(c);
            }
        }

        return quoted.Append('\'').ToString();
    }
}
