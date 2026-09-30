using System;
using System.Runtime.InteropServices;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT.Interop;

namespace ContextMenuManager.App
{
    public partial class App : Application
    {
        private Window _window;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();

            // Raised off the UI thread when a later launch redirects here (see Program).
            AppInstance.GetCurrent().Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(BringToFront);
        }

        // Window.Activate doesn't take the foreground from a visible window; SetForegroundWindow can,
        // using the permission Program grants.
        private void BringToFront()
        {
            if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }

            SetForegroundWindow(WindowNative.GetWindowHandle(_window));
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
