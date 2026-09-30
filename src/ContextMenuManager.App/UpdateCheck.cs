// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Win32;

namespace ContextMenuManager.App
{
    public static class UpdateCheck
    {
        public const string ReleasesUrl = "https://github.com/medallyon/ContextMenuManager/releases/latest";

        private const string LatestReleaseApiUrl = "https://api.github.com/repos/medallyon/ContextMenuManager/releases/latest";

        private const string SettingsKey = "Software\\ContextMenuManager";

        // Stored as "disabled" so a missing value means on, without a first-run write.
        private const string DisabledValue = "UpdateCheckDisabled";

        private static readonly HttpClient Http = CreateHttpClient();

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(SettingsKey, writable: false);
                    return key?.GetValue(DisabledValue) is not int disabled || disabled == 0;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
                key.SetValue(DisabledValue, enabled ? 0 : 1, RegistryValueKind.DWord);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ContextMenuManager: failed to change the update check setting: {ex.Message}");
                return false;
            }
        }

        // Null when the response has no parseable tag. Drafts and prereleases are never "latest".
        public static async Task<Version> GetLatestVersionAsync()
        {
            using var stream = await Http.GetStreamAsync(LatestReleaseApiUrl);
            using var json = await JsonDocument.ParseAsync(stream);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("tag_name", out var tag)
                || tag.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return Version.TryParse(tag.GetString().TrimStart('v'), out var version) ? version : null;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // GitHub's API rejects requests without one.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ContextMenuManager");
            return client;
        }
    }
}
