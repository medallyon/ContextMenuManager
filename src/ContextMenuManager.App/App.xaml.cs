using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

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

        private void BringToFront()
        {
            if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }

            _window.Activate();
        }
    }
}
