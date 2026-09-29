// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using ContextMenuManager.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using WinRT.Interop;

namespace ContextMenuManager.App
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Title = Display.GetString("AppTitle/Text");
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarText);
            AppWindow.Resize(new SizeInt32(1200, 900));
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

            // Below this the menu and the target picker no longer fit side by side with their labels.
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
                presenter.PreferredMinimumWidth = (int)(MinWindowWidth * scale);
                presenter.PreferredMinimumHeight = (int)(MinWindowHeight * scale);
            }
        }

        private const double MaxContentWidth = 1100;

        private const int MinWindowWidth = 720;

        private const int MinWindowHeight = 560;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private MainViewModel ViewModel { get; } = new MainViewModel();

        // A fixed width, never wider than the viewport, so no child's desired width can push the content past the window edge.
        private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var margin = ContentPanel.Margin;
            ContentPanel.Width = Math.Max(0, Math.Min(MaxContentWidth, e.NewSize.Width - margin.Left - margin.Right));
        }

        // Intercepts the details toggle instead of a plain two-way binding, so HKLM (all-users) writes
        // and likely-Windows-owned entries can be confirmed before anything is written to the registry.
        private async void DetailsToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            // Selecting another item re-binds IsOn to that entry's state, which lands here as a no-op below.
            if (sender is not ToggleSwitch toggle || !toggle.IsLoaded || ViewModel.SelectedEntry is not { } entry)
            {
                return;
            }

            bool requestedState = toggle.IsOn;
            if (requestedState == entry.IsEnabled)
            {
                return;
            }

            if (!await ConfirmIfNeededAsync(entry) || !ViewModel.ToggleEntry(entry, requestedState))
            {
                toggle.IsOn = entry.IsEnabled;
            }
        }

        // Selects the row for the details pane, and opens its submenu the way Explorer would: to the right of the row.
        private void PreviewItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ContextMenuPreviewItem item } button)
            {
                return;
            }

            if (item.IsShowMoreOptions)
            {
                ViewModel.ShowClassicLayer();
                return;
            }

            ViewModel.SelectedPreviewItem = item;
            if (!item.HasChildren)
            {
                return;
            }

            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
            AddPreviewMenuItems(flyout.Items, item.Children);
            flyout.ShowAt(button);
        }

        private void AddPreviewMenuItems(IList<MenuFlyoutItemBase> target, IEnumerable<ContextMenuPreviewItem> items)
        {
            foreach (var child in items)
            {
                if (child.IsSeparator)
                {
                    target.Add(new MenuFlyoutSeparator());
                    continue;
                }

                IconElement icon = Display.Image(child.Icon) is { } image ? new ImageIcon { Source = image } : null;
                if (child.HasChildren)
                {
                    var sub = new MenuFlyoutSubItem { Text = child.Text, Icon = icon };
                    AddPreviewMenuItems(sub.Items, child.Children);
                    target.Add(sub);
                }
                else
                {
                    var menuItem = new MenuFlyoutItem { Text = child.Text, Icon = icon, KeyboardAcceleratorTextOverride = Display.SideText(child.IsOff, child.Shortcut) ?? string.Empty, IsEnabled = !child.IsDisabled };
                    menuItem.Click += (_, _) => ViewModel.SelectedPreviewItem = child;
                    target.Add(menuItem);
                }
            }
        }

        // All-users entries need an elevated process; Windows asks for consent, then this window closes.
        private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
            }
            catch (Win32Exception)
            {
                // Consent prompt dismissed: keep running unelevated.
                return;
            }

            Close();
        }

        private async Task<bool> ConfirmIfNeededAsync(ContextMenuEntry entry)
        {
            // Blast-radius confirmation: an all-users write affects every account on the machine.
            if (entry.Scope == ContextMenuEntryScope.AllUsers && !await ShowConfirmDialogAsync("ConfirmAllUsers"))
            {
                return false;
            }

            // Extra confirmation (not a hard block) for entries whose handler DLL resolves under
            // System32/SysWOW64 - these are usually built-in Windows or security-software handlers.
            return !entry.IsLikelyWindowsOwned || await ShowConfirmDialogAsync("ConfirmWindowsOwned");
        }

        private async Task<bool> ShowConfirmDialogAsync(string resourcePrefix)
        {
            var dialog = new ContentDialog
            {
                Title = Display.GetString(resourcePrefix + "_Title"),
                Content = Display.GetString(resourcePrefix + "_Content"),
                PrimaryButtonText = Display.GetString(resourcePrefix + "_PrimaryButton"),
                CloseButtonText = Display.GetString(resourcePrefix + "_CloseButton"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
    }
}
