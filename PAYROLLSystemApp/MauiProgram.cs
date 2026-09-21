using Microsoft.Extensions.Logging;
using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Handlers;
using PAYROLLSystemApp.Security;
using PAYROLLSystemApp.Services;
using PAYROLLSystemApp.ViewModels;
using PAYROLLSystemApp.Views;
using PAYROLLSystemApp.Views.Sections;

namespace PAYROLLSystemApp
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Strip the platform chrome from text inputs so only the app's own
            // field frame is drawn.
            FieldChrome.Register();

            RegisterServices(builder.Services);
            RegisterViewModels(builder.Services);
            RegisterViews(builder.Services);

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }

        private static void RegisterServices(IServiceCollection services)
        {
            // Security primitives and storage are shared for the life of the app.
            services.AddSingleton<IPasswordHasher, PasswordHasher>();
            services.AddSingleton<PayrollDatabase>();
            services.AddSingleton<AuthOptions>();
            services.AddSingleton<IAuditService, AuditService>();
            services.AddSingleton<ISessionService, SessionService>();
            services.AddSingleton<IAuthService, AuthService>();

            // Section 2.2 — employee masterfile and its reference data.
            services.AddSingleton<IOrganizationService, OrganizationService>();
            services.AddSingleton<IDetachmentService, DetachmentService>();
            services.AddSingleton<IEmployeeService, EmployeeService>();

            // Section 2.3 — daily time records and the holiday calendar.
            services.AddSingleton<IHolidayService, HolidayService>();
            services.AddSingleton<IAttendanceService, AttendanceService>();
            services.AddSingleton<ITimesheetService, TimesheetService>();

            // Section 2.4 — leave types, credits and the request queue.
            services.AddSingleton<ILeaveService, LeaveService>();

            // Section 2.5 — the configuration every payroll figure derives from.
            services.AddSingleton<IPayrollConfigService, PayrollConfigService>();
            services.AddSingleton<IStatutoryTableService, StatutoryTableService>();

            // Section 2.6 — payroll runs, payslips, loans and adjustments.
            services.AddSingleton<IPayrollRunService, PayrollRunService>();

            // Section 2.7 — payslips and their PDF export.
            services.AddSingleton<IPayslipService, PayslipService>();

            // Section 2.8 — the register, the remittance forms and the exports.
            services.AddSingleton<IReportService, ReportService>();

            // Section 2.9 — backup, restore and closed-year archival.
            services.AddSingleton<IDataManagementService, DataManagementService>();

            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IAppNavigator, AppNavigator>();
        }

        private static void RegisterViewModels(IServiceCollection services)
        {
            services.AddTransient<LoginViewModel>();
            services.AddTransient<MainViewModel>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<EmployeeDirectoryViewModel>();
            services.AddTransient<OrganizationViewModel>();
            services.AddTransient<DetachmentsViewModel>();
            services.AddTransient<AttendanceViewModel>();
            services.AddTransient<TimesheetsViewModel>();
            services.AddTransient<LeaveViewModel>();
            services.AddTransient<PayrollSetupViewModel>();
            services.AddTransient<PayrollRunsViewModel>();
            services.AddTransient<ApprovalsViewModel>();
            services.AddTransient<PayslipsViewModel>();
            services.AddTransient<ReportsViewModel>();
            services.AddTransient<UserManagementViewModel>();
            services.AddTransient<AuditLogViewModel>();
            services.AddTransient<DataManagementViewModel>();
        }

        private static void RegisterViews(IServiceCollection services)
        {
            // Pages and sections are transient so each sign-in starts clean.
            services.AddTransient<LoginPage>();
            services.AddTransient<MainPage>();

            services.AddTransient<DashboardView>();
            services.AddTransient<EmployeesView>();
            services.AddTransient<OrganizationView>();
            services.AddTransient<DetachmentsView>();
            services.AddTransient<AttendanceView>();
            services.AddTransient<TimesheetsView>();
            services.AddTransient<LeaveView>();
            services.AddTransient<PayrollSetupView>();
            services.AddTransient<PayrollRunsView>();
            services.AddTransient<ApprovalsView>();
            services.AddTransient<PayslipsView>();
            services.AddTransient<ReportsView>();
            services.AddTransient<UserManagementView>();
            services.AddTransient<AuditLogView>();
            services.AddTransient<DataManagementView>();
        }
    }
}
