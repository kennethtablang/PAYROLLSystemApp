using Microsoft.Extensions.DependencyInjection;
using PAYROLLSystemApp.Services;
using PAYROLLSystemApp.Views;

namespace PAYROLLSystemApp
{
    /// <summary>
    /// Owns the two root states of the application — sign-in and the main
    /// layout — and swaps between them as the session starts and ends. Section
    /// navigation inside the main layout is handled by <see cref="IAppNavigator"/>.
    /// </summary>
    public partial class App : Application
    {
        private readonly IServiceProvider _services;
        private readonly ISessionService _session;
        private readonly IDialogService _dialogs;

        private Window? _window;

        public App(IServiceProvider services, ISessionService session, IDialogService dialogs)
        {
            InitializeComponent();

            _services = services;
            _session = session;
            _dialogs = dialogs;

            _session.SessionStarted += OnSessionStarted;
            _session.SessionEnded += OnSessionEnded;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            _window = new Window(_services.GetRequiredService<LoginPage>())
            {
                Title = "Payroll Management System",
                Width = 1280,
                Height = 800,
                MinimumWidth = 900,
                MinimumHeight = 620
            };

            return _window;
        }

        private void OnSessionStarted(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(() =>
                SetRoot(_services.GetRequiredService<MainPage>()));

        /// <summary>FR-006 / sign-out: always return to the sign-in screen.</summary>
        private void OnSessionEnded(object? sender, SessionEndedEventArgs e) =>
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                SetRoot(_services.GetRequiredService<LoginPage>());

                if (e.Reason == SessionEndReason.TimedOut)
                    await _dialogs.AlertAsync("Session ended", e.Message);
            });

        private void SetRoot(Page page)
        {
            if (_window is not null)
                _window.Page = page;
        }
    }
}
