// Copyright (c) Tilman (Medallyon)
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using ContextMenuManager.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace ContextMenuManager.App
{
    // The window's state: the entries found in the registry and the captured menu for the picked
    // target. Captures are cached per target until the next refresh.
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly ContextMenuPreview _preview = new ContextMenuPreview(Display.GetString);

        // Tasks rather than results, so flipping back to a target that is still capturing reuses it.
        private readonly Dictionary<ContextMenuPreviewTarget, Task<CapturedMenu>> _captures = new();

        private List<ContextMenuEntry> _entries = new List<ContextMenuEntry>();
        private ContextMenuPreviewTarget _previewTarget = ContextMenuPreviewTarget.Desktop;
        private bool _needsExplorerRestart;
        private bool _isClassicMenuDefault;
        private bool _isDesktopShortcutInstalled;
        private bool _showClassicLayer;
        private bool _isCmdPalExtensionInstalled;
        private bool _isCmdPalBusy;
        private bool _cmdPalNeedsDeveloperMode;
        private string _cmdPalMessage;
        private InfoBarSeverity _cmdPalMessageSeverity;
        private bool _isCapturing;
        private string _captureError;
        private ContextMenuPreviewItem _selectedPreviewItem;

        public MainViewModel()
        {
            _isCmdPalExtensionInstalled = IsCmdPalBundled && CmdPalExtension.IsRegisteredFromHere;
            LoadEntries();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<ContextMenuPreviewItem> PreviewItems { get; } = new ObservableCollection<ContextMenuPreviewItem>();

        public bool IsElevated { get; } = ContextMenuRegistry.IsElevated;

        public bool IsNotElevated => !IsElevated;

        public bool IsWindows11 { get; } = Environment.OSVersion.Version.Build >= 22000;

        public bool NeedsExplorerRestart
        {
            get => _needsExplorerRestart;
            private set => Set(ref _needsExplorerRestart, value);
        }

        public int PreviewTargetIndex
        {
            get => (int)_previewTarget;
            set
            {
                if (value < 0 || value == (int)_previewTarget || !Enum.IsDefined(typeof(ContextMenuPreviewTarget), value))
                {
                    return;
                }

                _previewTarget = (ContextMenuPreviewTarget)value;
                OnPropertyChanged();
                SelectedPreviewItem = null;
                _ = LoadPreviewAsync();
            }
        }

        public bool IsCapturing
        {
            get => _isCapturing;
            private set => Set(ref _isCapturing, value);
        }

        public string CaptureError
        {
            get => _captureError;
            private set
            {
                if (Set(ref _captureError, value))
                {
                    OnPropertyChanged(nameof(HasCaptureError));
                }
            }
        }

        public bool HasCaptureError => !string.IsNullOrEmpty(CaptureError);

        public bool IsWindows11ContextMenu
        {
            get => !_isClassicMenuDefault;
            set
            {
                if (value != _isClassicMenuDefault)
                {
                    return;
                }

                if (!ContextMenuRegistry.SetClassicMenuDefault(!value))
                {
                    OnPropertyChanged();
                    return;
                }

                // Captures are built for one menu style, so rebuild them for the new one.
                _isClassicMenuDefault = !value;
                _showClassicLayer = false;
                _captures.Clear();
                SelectedPreviewItem = null;
                NeedsExplorerRestart = true;
                OnPropertyChanged();
                _ = LoadPreviewAsync();
            }
        }

        public bool IsDesktopShortcutInstalled
        {
            get => _isDesktopShortcutInstalled;
            set
            {
                if (value == _isDesktopShortcutInstalled)
                {
                    return;
                }

                if (!ContextMenuRegistry.SetDesktopShortcut(value, Display.GetString("DesktopShortcutText"), Environment.ProcessPath))
                {
                    OnPropertyChanged();
                    return;
                }

                // Reload so the verb shows up in the Desktop capture, keeping an earlier toggle's restart prompt.
                bool needsRestart = NeedsExplorerRestart;
                LoadEntries();
                NeedsExplorerRestart = needsRestart;
            }
        }

        // Only published builds carry the extension; a plain build of the app has no CmdPal folder.
        public bool IsCmdPalBundled { get; } = CmdPalExtension.IsBundled;

        public bool IsCmdPalExtensionInstalled
        {
            get => _isCmdPalExtensionInstalled;
            set
            {
                if (value == _isCmdPalExtensionInstalled || _isCmdPalBusy)
                {
                    return;
                }

                _ = SetCmdPalExtensionAsync(value);
            }
        }

        public bool IsCmdPalBusy => _isCmdPalBusy;

        public bool IsCmdPalIdle => !_isCmdPalBusy;

        public bool CmdPalNeedsDeveloperMode
        {
            get => _cmdPalNeedsDeveloperMode;
            private set => Set(ref _cmdPalNeedsDeveloperMode, value);
        }

        public string CmdPalMessage
        {
            get => _cmdPalMessage;
            private set
            {
                if (Set(ref _cmdPalMessage, value))
                {
                    OnPropertyChanged(nameof(HasCmdPalMessage));
                }
            }
        }

        public bool HasCmdPalMessage => !string.IsNullOrEmpty(CmdPalMessage);

        public InfoBarSeverity CmdPalMessageSeverity
        {
            get => _cmdPalMessageSeverity;
            private set => Set(ref _cmdPalMessageSeverity, value);
        }

        public bool CanGoBackToModernMenu => _showClassicLayer && !_isClassicMenuDefault;

        public ContextMenuPreviewItem SelectedPreviewItem
        {
            get => _selectedPreviewItem;
            set
            {
                if (!Set(ref _selectedPreviewItem, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(SelectedEntry));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
                OnPropertyChanged(nameof(HasSelectedEntry));
                OnPropertyChanged(nameof(SelectedDescription));
                OnPropertyChanged(nameof(SelectedLocation));
                OnPropertyChanged(nameof(IsSelectedExtension));
            }
        }

        public ContextMenuEntry SelectedEntry => _selectedPreviewItem?.Entry;

        public bool HasSelection => _selectedPreviewItem != null;

        public bool HasNoSelection => !HasSelection;

        public bool HasSelectedEntry => SelectedEntry != null;

        public string SelectedDescription => SelectedEntry is { } entry
            ? $"{entry.DisplayName} · {Display.ScopeAndSource(entry)}"
            : Display.GetString("DetailsUnmanaged");

        public string SelectedLocation
        {
            get
            {
                if (SelectedEntry is not { } entry)
                {
                    return null;
                }

                if (entry.Kind == ContextMenuEntryKind.BlockedClsid)
                {
                    return string.Join(Environment.NewLine, entry.Clsids.Prepend(entry.HandlerKeyName).Distinct(StringComparer.OrdinalIgnoreCase));
                }

                string hive = entry.Scope == ContextMenuEntryScope.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                return string.Join(Environment.NewLine, entry.KeyPaths.Select(p => $"{hive}\\Software\\Classes\\{p}"));
            }
        }

        // A shell extension can add several items; turning it off removes all of them.
        public bool IsSelectedExtension => SelectedEntry is { Source: ContextMenuEntrySource.Classic, Kind: not ContextMenuEntryKind.Verb };

        // Re-enumerates live: the registry is the only state.
        public void LoadEntries()
        {
            _entries = ContextMenuRegistry.Enumerate(IsElevated);
            NeedsExplorerRestart = false;

            // Every cached item points at an old entry object.
            _captures.Clear();
            _isClassicMenuDefault = ContextMenuRegistry.IsClassicMenuDefault();
            _isDesktopShortcutInstalled = ContextMenuRegistry.IsDesktopShortcutInstalled();
            OnPropertyChanged(nameof(IsWindows11ContextMenu));
            OnPropertyChanged(nameof(IsDesktopShortcutInstalled));
            SelectedPreviewItem = null;
            _ = LoadPreviewAsync();
            LoadIcons();
        }

        // The page owns the confirmation dialogs; this assumes the write is allowed.
        public bool ToggleEntry(ContextMenuEntry entry, bool enable)
        {
            if (!ContextMenuRegistry.Toggle(entry, enable, IsElevated))
            {
                return false;
            }

            // The cached menus stay: rows only re-read the entry's state, so no re-capture is needed.
            NeedsExplorerRestart = true;
            RenderPreview();
            return true;
        }

        public void RestartExplorer()
        {
            ContextMenuRegistry.RestartExplorer();
            NeedsExplorerRestart = false;
        }

        // Windows 11's "Show more options": swaps the preview to the classic menu, like Explorer does.
        public void ShowClassicLayer()
        {
            _showClassicLayer = true;
            RenderPreview();
        }

        public void BackToModernMenu()
        {
            _showClassicLayer = false;
            RenderPreview();
        }

        // Never throws: the setter discards the task.
        private async Task SetCmdPalExtensionAsync(bool install)
        {
            CmdPalMessage = null;
            CmdPalNeedsDeveloperMode = false;

            // Read on every attempt: the user may have just switched it on in Settings.
            if (install && !CmdPalExtension.IsDeveloperModeOn)
            {
                CmdPalNeedsDeveloperMode = true;
                ShowCmdPalMessage(Display.GetString("CmdPalDeveloperModeRequired"), InfoBarSeverity.Warning);
                OnPropertyChanged(nameof(IsCmdPalExtensionInstalled));
                return;
            }

            _isCmdPalBusy = true;
            OnPropertyChanged(nameof(IsCmdPalBusy));
            OnPropertyChanged(nameof(IsCmdPalIdle));
            try
            {
                if (install)
                {
                    await CmdPalExtension.RegisterAsync();
                    ShowCmdPalMessage(Display.GetString("CmdPalInstalled"), InfoBarSeverity.Success);
                }
                else
                {
                    await CmdPalExtension.RemoveAsync();
                }

                _isCmdPalExtensionInstalled = install;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ContextMenuManager: Command Palette extension change failed: {ex}");
                ShowCmdPalMessage(ex.Message, InfoBarSeverity.Error);
            }

            _isCmdPalBusy = false;
            OnPropertyChanged(nameof(IsCmdPalBusy));
            OnPropertyChanged(nameof(IsCmdPalIdle));
            OnPropertyChanged(nameof(IsCmdPalExtensionInstalled));
        }

        private void ShowCmdPalMessage(string message, InfoBarSeverity severity)
        {
            CmdPalMessageSeverity = severity;
            CmdPalMessage = message;
        }

        // Extraction touches the disk and third-party binaries, so it runs off the UI thread; the
        // result is handed back on the dispatcher because the UI observes Entry.Icon.
        private void LoadIcons()
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var pending = _entries.Where(e => !string.IsNullOrWhiteSpace(e.IconSpec)).ToList();
            Task.Run(() =>
            {
                foreach (var entry in pending)
                {
                    if (ContextMenuRegistry.LoadIcon(entry.IconSpec) is { } icon)
                    {
                        dispatcher.TryEnqueue(() => entry.Icon = icon);
                    }
                }

                // Rows without a captured bitmap fall back to the entry icon, read when rendered.
                dispatcher.TryEnqueue(RenderPreview);
            }).ContinueWith(t => Trace.WriteLine($"ContextMenuManager: icon loading failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        }

        // Re-adds the cached rows so their on/off look and late-loaded icons are read again.
        private void RenderPreview()
        {
            PreviewItems.Clear();
            if (_captures.TryGetValue(_previewTarget, out var task) && task.IsCompletedSuccessfully)
            {
                foreach (var item in task.Result.Modern == null || _showClassicLayer ? task.Result.Classic : task.Result.Modern)
                {
                    PreviewItems.Add(item);
                }
            }

            OnPropertyChanged(nameof(CanGoBackToModernMenu));
        }

        // Never throws: callers discard the task.
        private async Task LoadPreviewAsync()
        {
            var target = _previewTarget;
            CaptureError = null;
            if (!_captures.TryGetValue(target, out var task))
            {
                task = _preview.CaptureAsync(target, _entries, _isClassicMenuDefault);
                _captures[target] = task;
            }

            RenderPreview();
            IsCapturing = !task.IsCompleted;
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ContextMenuManager: menu capture failed: {ex}");
                if (_captures.TryGetValue(target, out var current) && current == task)
                {
                    // Not cached, so picking the target again retries.
                    _captures.Remove(target);
                    if (target == _previewTarget)
                    {
                        CaptureError = ex is TimeoutException ? Display.GetString("CaptureTimeout") : ex.Message;
                    }
                }
            }

            RenderPreview();
            IsCapturing = _captures.TryGetValue(_previewTarget, out var shown) && !shown.IsCompleted;
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
