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

        private Window? _window;

        public App(IServiceProvider services, ISessionService session, IUserPreferences preferences)
        {
            InitializeComponent();

            // Before the first window, so it never flashes in the other theme.
            preferences.ApplyTheme(this);

            _services = services;
            _session = session;

            _session.SessionStarted += OnSessionStarted;
            _session.SessionEnded += OnSessionEnded;
            _session.PasswordChangeRequested += OnPasswordChangeRequested;
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

        /// <summary>Sign-in always opens the main layout.</summary>
        private void OnSessionStarted(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(() =>
                SetRoot(_services.GetRequiredService<MainPage>()));

        private void OnPasswordChangeRequested(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(() =>
                SetRoot(_services.GetRequiredService<ChangePasswordPage>()));

        /// <summary>Sign-out always returns to the sign-in screen.</summary>
        private void OnSessionEnded(object? sender, SessionEndedEventArgs e) =>
            MainThread.BeginInvokeOnMainThread(() =>
                SetRoot(_services.GetRequiredService<LoginPage>()));

        private void SetRoot(Page page)
        {
            if (_window is not null)
                _window.Page = page;
        }
    }
}
