// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Win32;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace ContextMenuManager.App
{
    // The Command Palette extension ships unpackaged in the app's CmdPal folder and is registered
    // from there, like Add-AppxPackage -Register. An unsigned loose package needs Developer Mode.
    internal static class CmdPalExtension
    {
        private const string PackageName = "Medallyon.ContextMenuManager.CmdPal";
        private const string PackagePublisher = "CN=Medallyon";

        private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "CmdPal");

        private static string ManifestPath => Path.Combine(Folder, "AppxManifest.xml");

        public static bool IsBundled => File.Exists(ManifestPath);

        public static bool IsDeveloperModeOn
        {
            get
            {
                try
                {
                    return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", 0) is int value && value != 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // A registration from another folder (a moved or older copy of the app) counts as not installed,
        // so turning the toggle on re-points it here.
        public static bool IsRegisteredFromHere
        {
            get
            {
                try
                {
                    string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder));
                    return FindRegistered().Any(p => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.InstalledPath)), folder, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static async Task RegisterAsync()
        {
            // Registering over a package from another folder fails, so drop it first.
            await RemoveAsync();
            var options = new RegisterPackageOptions { DeveloperMode = true, ForceAppShutdown = true };
            await RunAsync(new PackageManager().RegisterPackageByUriAsync(new Uri(ManifestPath), options));
        }

        public static async Task RemoveAsync()
        {
            foreach (var package in FindRegistered())
            {
                await RunAsync(new PackageManager().RemovePackageAsync(package.Id.FullName));
            }
        }

        private static Windows.ApplicationModel.Package[] FindRegistered() =>
            new PackageManager().FindPackagesForUser(string.Empty, PackageName, PackagePublisher).ToArray();

        // The awaited exception only carries an HRESULT; ErrorText says what went wrong.
        private static async Task RunAsync(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation)
        {
            try
            {
                await operation;
            }
            catch (Exception) when (operation.Status == AsyncStatus.Error)
            {
                throw new InvalidOperationException(operation.GetResults().ErrorText);
            }
        }
    }
}
