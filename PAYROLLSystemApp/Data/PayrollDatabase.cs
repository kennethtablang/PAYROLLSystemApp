using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;
using SQLite;

namespace PAYROLLSystemApp.Data;

/// <summary>
/// Owns the SQLite connection and schema creation.
///
/// All access goes through sqlite-net, which parameterises every query — the
/// application never concatenates SQL from user input (NFR-018). The connection
/// is created once and shared; sqlite-net's async connection serialises writes.
/// The storage engine is reachable only through this class and the services
/// above it, so it can be swapped later without touching business logic (NFR-029).
/// </summary>
public sealed partial class PayrollDatabase
{
    public const string DatabaseFileName = "payroll.db3";

    private const SQLiteOpenFlags Flags =
        SQLiteOpenFlags.ReadWrite |
        SQLiteOpenFlags.Create |
        SQLiteOpenFlags.SharedCache |
        SQLiteOpenFlags.FullMutex;

    private readonly IPasswordHasher _passwordHasher;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private SQLiteAsyncConnection? _connection;
    private bool _initialised;

    public PayrollDatabase(IPasswordHasher passwordHasher)
    {
        _passwordHasher = passwordHasher;
        DatabasePath = Path.Combine(FileSystem.AppDataDirectory, DatabaseFileName);
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Credentials for the account seeded on a brand new database.
    ///
    /// There is no forced password change, so this password stays valid until
    /// an administrator changes it from User Accounts. Change it before the
    /// system holds real payroll data.
    /// </summary>
    public const string SeedAdminUsername = "admin";
    public const string SeedAdminPassword = "Admin@123";

    public async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_initialised && _connection is not null)
            return _connection;

        await _initGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialised && _connection is not null)
                return _connection;

            _connection ??= new SQLiteAsyncConnection(DatabasePath, Flags);

            // Before anything is created: a rate table built around the old
            // (detachment, position) key has to lose its position column, and it
            // has to happen while its old index is still the one on disk.
            await MigrateDetachmentRatesAsync(_connection).ConfigureAwait(false);

            await _connection.CreateTableAsync<User>().ConfigureAwait(false);
            await _connection.CreateTableAsync<AuditEntry>().ConfigureAwait(false);

            // Section 2.2 — the employee masterfile and the reference data it
            // keys off. CreateTableAsync is additive: an existing database gains
            // the new tables on next launch without losing its accounts.
            await _connection.CreateTableAsync<Department>().ConfigureAwait(false);
            await _connection.CreateTableAsync<Position>().ConfigureAwait(false);
            await _connection.CreateTableAsync<Detachment>().ConfigureAwait(false);
            await _connection.CreateTableAsync<DetachmentRate>().ConfigureAwait(false);
            await _connection.CreateTableAsync<WorkSchedule>().ConfigureAwait(false);
            await _connection.CreateTableAsync<Employee>().ConfigureAwait(false);
            await _connection.CreateTableAsync<SalaryRateHistory>().ConfigureAwait(false);

            // Section 2.3 — daily time records and the holiday calendar they
            // are classified against.
            await _connection.CreateTableAsync<Holiday>().ConfigureAwait(false);
            await _connection.CreateTableAsync<AttendanceRecord>().ConfigureAwait(false);

            // Section 2.4 — leave types, the credits held against them and the
            // filings that spend those credits.
            await _connection.CreateTableAsync<LeaveType>().ConfigureAwait(false);
            await _connection.CreateTableAsync<LeaveBalance>().ConfigureAwait(false);
            await _connection.CreateTableAsync<LeaveRequest>().ConfigureAwait(false);

            // Section 2.5 — the payroll configuration everything downstream is
            // computed from: the calendar, the components, the premium matrix
            // and the effective-dated statutory tables.
            await _connection.CreateTableAsync<PayPeriod>().ConfigureAwait(false);
            await _connection.CreateTableAsync<EarningType>().ConfigureAwait(false);
            await _connection.CreateTableAsync<DeductionType>().ConfigureAwait(false);
            await _connection.CreateTableAsync<PremiumRate>().ConfigureAwait(false);
            await _connection.CreateTableAsync<SssBracket>().ConfigureAwait(false);
            await _connection.CreateTableAsync<PhilHealthRate>().ConfigureAwait(false);
            await _connection.CreateTableAsync<PagIbigRate>().ConfigureAwait(false);
            await _connection.CreateTableAsync<WithholdingTaxBracket>().ConfigureAwait(false);
            await _connection.CreateTableAsync<CompanyProfile>().ConfigureAwait(false);
            await _connection.CreateTableAsync<PayrollSettings>().ConfigureAwait(false);

            // Section 2.6 — the runs themselves, the payslips they produce, and
            // the recurring deductions and one-off adjustments they read.
            await _connection.CreateTableAsync<PayrollRun>().ConfigureAwait(false);
            await _connection.CreateTableAsync<Payslip>().ConfigureAwait(false);
            await _connection.CreateTableAsync<PayslipLine>().ConfigureAwait(false);
            await _connection.CreateTableAsync<EmployeeLoan>().ConfigureAwait(false);
            await _connection.CreateTableAsync<LoanPayment>().ConfigureAwait(false);
            await _connection.CreateTableAsync<PayrollAdjustment>().ConfigureAwait(false);

            // A standing deduction ends by date rather than by exhausting a
            // balance, which is why it is not an EmployeeLoan — the insurance
            // premium, the performance bond, the processing fee.
            await _connection.CreateTableAsync<EmployeeDeduction>().ConfigureAwait(false);

            // The client's timesheet, keyed per run. Sits beside attendance
            // rather than inside it: it records a cut-off's totals, not a day.
            await _connection.CreateTableAsync<PeriodTimesheet>().ConfigureAwait(false);

            // Section 2.9 — backup policy and the archive receipts that make a
            // purge refusable when the files are not there (FR-093, FR-094).
            await _connection.CreateTableAsync<BackupSettings>().ConfigureAwait(false);
            await _connection.CreateTableAsync<ArchiveRecord>().ConfigureAwait(false);

            await MigrateUserRolesAsync(_connection).ConfigureAwait(false);

            await SeedAdministratorAsync(_connection).ConfigureAwait(false);
            await SeedOrganizationAsync(_connection).ConfigureAwait(false);
            await SeedHolidaysAsync(_connection).ConfigureAwait(false);
            await SeedLeaveTypesAsync(_connection).ConfigureAwait(false);
            await SeedPayrollConfigurationAsync(_connection).ConfigureAwait(false);

            _initialised = true;
            return _connection;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// FR-093. Closes the connection so the file underneath can be replaced.
    ///
    /// <para>SQLite holds the database open, and on Windows an open handle makes
    /// the file unreplaceable. Restoring therefore has to shut the connection
    /// first and let the next caller of <see cref="GetConnectionAsync"/> build a
    /// new one — which also re-runs schema creation and seeding, so a restored
    /// database from an older release gains any tables added since.</para>
    /// </summary>
    public async Task CloseAsync()
    {
        await _initGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                await _connection.CloseAsync().ConfigureAwait(false);

            _connection = null;
            _initialised = false;

            // The pool holds its own handle to the file. Without this the
            // replace still fails, and only on Windows, which is the worst kind
            // of bug to find later.
            SQLiteAsyncConnection.ResetPool();
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Brings accounts created under the five-role matrix onto the two the
    /// system now issues (<see cref="UserRole"/>).
    ///
    /// <para>Administrator was 0 and still is, so those rows are untouched. The
    /// three roles that no longer exist all become Accounting — but an account
    /// that was <b>Employee</b> is additionally <b>deactivated</b>, because
    /// Employee was self-service and Accounting reads every salary in the
    /// company. Widening a login's reach without anyone deciding to is the one
    /// outcome a migration must not produce; an administrator can reactivate it
    /// deliberately.</para>
    ///
    /// <para>Runs on every launch and is a no-op once there is nothing left to
    /// remap, so it costs one indexed count against a table with two rows in it.
    /// </para>
    /// </summary>
    private static async Task MigrateUserRolesAsync(SQLiteAsyncConnection connection)
    {
        const int formerPayrollOfficer = 2;
        const int formerApprover = 3;
        const int formerEmployee = 4;

        var stale = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE Role NOT IN (?, ?)",
            (int)UserRole.Administrator, (int)UserRole.Accounting).ConfigureAwait(false);

        if (stale == 0)
            return;

        // Deactivate before remapping: once the role is Accounting the WHERE
        // clause can no longer tell which rows were self-service accounts.
        var demoted = await connection.ExecuteAsync(
            "UPDATE users SET IsActive = 0 WHERE Role = ?", formerEmployee).ConfigureAwait(false);

        await connection.ExecuteAsync(
            "UPDATE users SET Role = ? WHERE Role IN (?, ?, ?)",
            (int)UserRole.Accounting, formerPayrollOfficer, formerApprover, formerEmployee)
            .ConfigureAwait(false);

        await connection.InsertAsync(new AuditEntry
        {
            Actor = "system",
            Action = AuditActions.UserUpdated,
            Entity = nameof(User),
            Success = true,
            Details =
                $"Role matrix collapsed to Administrator and Accounting: {stale} account(s) remapped " +
                $"to Accounting, of which {demoted} former self-service account(s) were deactivated."
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops <c>PositionId</c> from <c>detachment_rates</c>.
    ///
    /// <para>The rate used to hang off a (detachment, position) pair. It does
    /// not: the client's own paperwork quotes one figure against one code —
    /// "CS75 (600.00 per day)" — and a post paying two figures is two codes. The
    /// column has to go rather than sit unused, because SQLite-net declares it
    /// <c>not null</c> with no default, so an insert that omits it fails.</para>
    ///
    /// <para><b>Where a post held several rates for one date, the highest
    /// survives.</b> Those rows were the same wage order split by position, and
    /// collapsing them by picking the dearest cannot underpay anyone. The losers
    /// are deactivated rather than deleted and the audit log names the count, so
    /// the choice is visible and can be corrected by posting a new rate.</para>
    ///
    /// <para>Runs before any table is created, while the old index is still the
    /// one on disk — a column cannot be dropped while an index references it.
    /// A no-op on a new database and on one already migrated.</para>
    /// </summary>
    private static async Task MigrateDetachmentRatesAsync(SQLiteAsyncConnection connection)
    {
        var tableExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'detachment_rates'")
            .ConfigureAwait(false);

        if (tableExists == 0)
            return;

        var columns = await connection.QueryScalarsAsync<string>(
            "SELECT name FROM pragma_table_info('detachment_rates')").ConfigureAwait(false);

        if (!columns.Contains("PositionId", StringComparer.OrdinalIgnoreCase))
            return;

        // Same post, same effective date, several positions: keep the dearest.
        var collapsed = await connection.ExecuteAsync(
            """
            UPDATE detachment_rates SET IsActive = 0
            WHERE IsActive = 1 AND Id NOT IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (
                        PARTITION BY DetachmentId, date(EffectiveFrom)
                        ORDER BY DailyRate DESC, Id DESC) AS rn
                    FROM detachment_rates WHERE IsActive = 1)
                WHERE rn = 1)
            """).ConfigureAwait(false);

        await connection.ExecuteAsync("DROP INDEX IF EXISTS ix_detachment_rates_lookup")
            .ConfigureAwait(false);

        await connection.ExecuteAsync("ALTER TABLE detachment_rates DROP COLUMN PositionId")
            .ConfigureAwait(false);

        await connection.InsertAsync(new AuditEntry
        {
            Actor = "system",
            Action = AuditActions.DetachmentRateWithdrawn,
            Entity = nameof(DetachmentRate),
            Success = true,
            Details =
                "Detachment rates are now keyed by post alone rather than by post and position. " +
                (collapsed == 0
                    ? "No rate needed collapsing."
                    : $"{collapsed} rate(s) that shared a post and an effective date were withdrawn, " +
                      "the dearest of each kept. Check the rate table and post a correction if the " +
                      "figure kept is not the one intended.")
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the bootstrap administrator on an empty database. Without it
    /// there would be no way to sign in and create the first real account.
    /// </summary>
    private async Task SeedAdministratorAsync(SQLiteAsyncConnection connection)
    {
        var userCount = await connection.Table<User>().CountAsync().ConfigureAwait(false);
        if (userCount > 0)
            return;

        var admin = new User
        {
            Username = SeedAdminUsername,
            Email = "admin@payrollsystem.local",
            FullName = "System Administrator",
            PasswordHash = _passwordHasher.Hash(SeedAdminPassword),
            Role = UserRole.Administrator,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            PasswordChangedUtc = DateTime.UtcNow
        };

        await connection.InsertAsync(admin).ConfigureAwait(false);

        await connection.InsertAsync(new AuditEntry
        {
            Actor = "system",
            Action = AuditActions.UserCreated,
            Entity = nameof(User),
            EntityId = admin.Id,
            Success = true,
            Details = "Bootstrap administrator account seeded on database creation."
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds a starting set of departments, positions and work schedules so the
    /// first employee can be created without a detour through three empty
    /// reference lists.
    ///
    /// <para>These are ordinary editable rows, not fixtures: rename them, add to
    /// them, or deactivate the ones that do not apply. Each of the three seeds
    /// independently, so a company that cleared its departments does not get
    /// them back merely because it never touched the schedules.</para>
    /// </summary>
    private static async Task SeedOrganizationAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<Department>().CountAsync().ConfigureAwait(false) == 0)
        {
            await connection.InsertAllAsync(new[]
            {
                new Department { Code = "ADM", Name = "Administration", Description = "Executive and general administration" },
                new Department { Code = "HR",  Name = "Human Resources", Description = "Recruitment, records and employee relations" },
                new Department { Code = "FIN", Name = "Finance and Accounting", Description = "Accounting, treasury and payroll" },
                new Department { Code = "OPS", Name = "Operations", Description = "Core service delivery" },
                new Department { Code = "IT",  Name = "Information Technology", Description = "Systems, support and infrastructure" },
                new Department { Code = "SLS", Name = "Sales and Marketing", Description = "Business development and marketing" }
            }).ConfigureAwait(false);
        }

        if (await connection.Table<Position>().CountAsync().ConfigureAwait(false) == 0)
        {
            // IsManagerial carries a payroll consequence, not a courtesy title:
            // Art. 82 puts these roles outside overtime, night differential and
            // premium pay. Review it before the first live run.
            await connection.InsertAllAsync(new[]
            {
                new Position { Code = "PRES", Title = "President",           IsManagerial = true },
                new Position { Code = "MGR",  Title = "Manager",             IsManagerial = true },
                new Position { Code = "SUPV", Title = "Supervisor",          IsManagerial = true },
                new Position { Code = "HROF", Title = "HR Officer",          IsManagerial = false },
                new Position { Code = "ACCT", Title = "Accountant",          IsManagerial = false },
                new Position { Code = "PAYO", Title = "Payroll Officer",     IsManagerial = false },
                new Position { Code = "ADMA", Title = "Administrative Assistant", IsManagerial = false },
                new Position { Code = "STAF", Title = "Staff",               IsManagerial = false },
                new Position { Code = "RANK", Title = "Rank and File",       IsManagerial = false }
            }).ConfigureAwait(false);
        }

        if (await connection.Table<WorkSchedule>().CountAsync().ConfigureAwait(false) == 0)
        {
            await connection.InsertAllAsync(new[]
            {
                new WorkSchedule
                {
                    Name = "Regular day shift",
                    StartMinutes = 8 * 60, EndMinutes = 17 * 60,
                    BreakMinutes = 60, GraceMinutes = 15,
                    WorkDays = WorkSchedule.MondayToFriday
                },
                new WorkSchedule
                {
                    Name = "Mid shift",
                    StartMinutes = 13 * 60, EndMinutes = 22 * 60,
                    BreakMinutes = 60, GraceMinutes = 15,
                    WorkDays = WorkSchedule.MondayToFriday
                },
                new WorkSchedule
                {
                    // Crosses midnight, so the whole shift falls inside the
                    // 22:00-06:00 night differential window.
                    Name = "Night shift",
                    StartMinutes = 22 * 60, EndMinutes = 7 * 60,
                    BreakMinutes = 60, GraceMinutes = 15,
                    WorkDays = WorkSchedule.MondayToFriday
                },
                new WorkSchedule
                {
                    Name = "Six-day day shift",
                    StartMinutes = 8 * 60, EndMinutes = 17 * 60,
                    BreakMinutes = 60, GraceMinutes = 15,
                    WorkDays = WorkSchedule.MondayToFriday | (1 << (int)DayOfWeek.Saturday)
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Seeds the Philippine holidays that are fixed to a calendar date (FR-025).
    ///
    /// <para><b>Only the fixed ones.</b> Maundy Thursday, Good Friday and Black
    /// Saturday follow Easter; Eid'l Fitr and Eid'l Adha follow the lunar
    /// calendar; National Heroes Day is the last Monday of August; and any of
    /// them can be moved by proclamation. None of those can be seeded honestly,
    /// so they are added to the calendar each year as they are proclaimed —
    /// which is exactly why the calendar is data (NFR-031).</para>
    ///
    /// <para>The dates below are entered under the current year, but
    /// <see cref="Holiday.IsAnnual"/> makes them match on month and day in every
    /// year, so the seed does not go stale.</para>
    /// </summary>
    private static async Task SeedHolidaysAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<Holiday>().CountAsync().ConfigureAwait(false) > 0)
            return;

        var year = DateTime.Today.Year;

        Holiday Fixed(int month, int day, string name, HolidayType type) => new()
        {
            Date = new DateTime(year, month, day),
            Name = name,
            Type = type,
            IsAnnual = true,
            Remarks = "Fixed-date holiday under RA 9492. Verify against the annual proclamation."
        };

        await connection.InsertAllAsync(new[]
        {
            // Regular holidays — paid even when unworked (Art. 94), and worked
            // hours pay double.
            Fixed(1,  1,  "New Year's Day",        HolidayType.Regular),
            Fixed(4,  9,  "Araw ng Kagitingan",    HolidayType.Regular),
            Fixed(5,  1,  "Labor Day",             HolidayType.Regular),
            Fixed(6,  12, "Independence Day",      HolidayType.Regular),
            Fixed(11, 30, "Bonifacio Day",         HolidayType.Regular),
            Fixed(12, 25, "Christmas Day",         HolidayType.Regular),
            Fixed(12, 30, "Rizal Day",             HolidayType.Regular),

            // Special non-working days — no work, no pay unless company policy
            // says otherwise; worked hours pay 1.30×.
            Fixed(8,  21, "Ninoy Aquino Day",      HolidayType.SpecialNonWorking),
            Fixed(11, 1,  "All Saints' Day",       HolidayType.SpecialNonWorking),
            Fixed(12, 8,  "Feast of the Immaculate Conception", HolidayType.SpecialNonWorking),
            Fixed(12, 31, "Last Day of the Year",  HolidayType.SpecialNonWorking)
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds a starting set of leave types (FR-030).
    ///
    /// <para>The statutory ones carry their legal minimum: five days of Service
    /// Incentive Leave under Art. 95, accrued monthly and convertible to cash;
    /// 105 days of maternity leave under RA 11210; seven of paternity leave under
    /// RA 8187. The rest — vacation, sick, emergency — are ordinary company
    /// policy with placeholder credits, because the law sets no floor for them.</para>
    ///
    /// <para>All of it is editable. A company that grants fifteen days of vacation
    /// changes the number here rather than waiting for a release (NFR-031).</para>
    /// </summary>
    private static async Task SeedLeaveTypesAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<LeaveType>().CountAsync().ConfigureAwait(false) > 0)
            return;

        await connection.InsertAllAsync(new[]
        {
            new LeaveType
            {
                Code = "SIL", Name = "Service Incentive Leave",
                Description = "Art. 95: five days a year after one year of service, convertible to cash.",
                DefaultAnnualCredits = 5m,
                IsPaid = true, AccruesMonthly = true, IsConvertibleToCash = true
            },
            new LeaveType
            {
                Code = "VL", Name = "Vacation Leave",
                Description = "Company-granted paid time off. Not a statutory entitlement.",
                DefaultAnnualCredits = 10m,
                IsPaid = true
            },
            new LeaveType
            {
                Code = "SL", Name = "Sick Leave",
                Description = "Company-granted paid sick days. Not a statutory entitlement.",
                DefaultAnnualCredits = 10m,
                IsPaid = true
            },
            new LeaveType
            {
                Code = "EL", Name = "Emergency Leave",
                Description = "Short-notice leave for a family emergency or bereavement.",
                DefaultAnnualCredits = 3m,
                IsPaid = true
            },
            new LeaveType
            {
                Code = "ML", Name = "Maternity Leave",
                Description = "RA 11210: 105 days, extendable by 30 unpaid. Reimbursed through the SSS.",
                DefaultAnnualCredits = 105m,
                IsPaid = true, AppliesTo = LeaveApplicability.FemaleOnly
            },
            new LeaveType
            {
                Code = "PL", Name = "Paternity Leave",
                Description = "RA 8187: seven days for the first four deliveries of a lawful wife.",
                DefaultAnnualCredits = 7m,
                IsPaid = true, AppliesTo = LeaveApplicability.MaleOnly
            },
            new LeaveType
            {
                Code = "SPL", Name = "Solo Parent Leave",
                Description = "RA 8972 as amended: seven days a year for a qualified solo parent.",
                DefaultAnnualCredits = 7m,
                IsPaid = true
            },
            new LeaveType
            {
                Code = "LWOP", Name = "Leave Without Pay",
                Description = "Authorised absence that is not compensated. Draws on no credits.",
                DefaultAnnualCredits = 0m,
                IsPaid = false
            }
        }).ConfigureAwait(false);
    }
}
