// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ContextMenuManager.App
{
    // Replaces the XAML-generated Main (DISABLE_XAML_GENERATED_MAIN) so a second launch, such as the
    // desktop menu item, hands off to the open window instead of opening another one.
    public static class Program
    {
        public const string InstanceKey = "main";

        [STAThread]
        private static void Main()
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (RedirectToRunningInstance())
            {
                return;
            }

            Application.Start(p =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
        }

        private static bool RedirectToRunningInstance()
        {
            var owner = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (owner.IsCurrent)
            {
                return false;
            }

            // Windows only lets the owner take focus if this process, which has it, passes it on.
            AllowSetForegroundWindow(owner.ProcessId);

            // Off the STA thread: blocking it on the redirect can deadlock.
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => owner.RedirectActivationToAsync(activation).AsTask()).Wait();
            return true;
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);
    }
}
