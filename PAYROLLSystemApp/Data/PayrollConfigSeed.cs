using PAYROLLSystemApp.Models;
using SQLite;

namespace PAYROLLSystemApp.Data;

/// <summary>
/// Seeds section 2.5 — the payroll configuration (FR-040 – FR-045).
///
/// <para><b>Everything here is a starting point, not a fixture.</b> The premium
/// multipliers are the statutory minima, the contribution schedules are the ones
/// current when this was written, and the tax brackets are TRAIN as tabulated in
/// RR 11-2018. All of them are rows precisely so a circular does not require a
/// release (NFR-031, C-02).</para>
///
/// <para><b>Verify before the first live run.</b> SSS, PhilHealth and Pag-IBIG
/// all change by circular, sometimes mid-year, and a seeded schedule that is a
/// year out is wrong in a way that looks entirely plausible on a payslip. The
/// setup screen says the same thing where a payroll officer will see it.</para>
///
/// <para>Each block seeds independently, so a company that has replaced its tax
/// brackets does not get the seeded ones back because it never touched the
/// premium matrix.</para>
/// </summary>
public sealed partial class PayrollDatabase
{
    /// <summary>
    /// The date the seeded statutory schedules are recorded as taking effect.
    /// Deliberately early: a row dated in the future is invisible to a run, so
    /// a brand new database would compute no contributions at all.
    /// </summary>
    private static readonly DateTime StatutoryEffectiveFrom = new(2023, 1, 1);

    private async Task SeedPayrollConfigurationAsync(SQLiteAsyncConnection connection)
    {
        await SeedPayrollSettingsAsync(connection).ConfigureAwait(false);
        await SeedCompanyProfileAsync(connection).ConfigureAwait(false);
        await SeedEarningTypesAsync(connection).ConfigureAwait(false);
        await SeedDeductionTypesAsync(connection).ConfigureAwait(false);
        await SeedPremiumRatesAsync(connection).ConfigureAwait(false);
        await SeedSssScheduleAsync(connection).ConfigureAwait(false);
        await SeedPhilHealthAsync(connection).ConfigureAwait(false);
        await SeedPagIbigAsync(connection).ConfigureAwait(false);
        await SeedTaxBracketsAsync(connection).ConfigureAwait(false);
        await SeedPayPeriodsAsync(connection).ConfigureAwait(false);
    }

    private static async Task SeedPayrollSettingsAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<PayrollSettings>().CountAsync().ConfigureAwait(false) > 0)
            return;

        await connection.InsertAsync(new PayrollSettings()).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the empty company row so the setup screen has something to edit.
    /// It is deliberately blank: a placeholder name would end up printed on a
    /// payslip by an installation that never filled it in.
    /// </summary>
    private static async Task SeedCompanyProfileAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<CompanyProfile>().CountAsync().ConfigureAwait(false) > 0)
            return;

        await connection.InsertAsync(new CompanyProfile()).ConfigureAwait(false);
    }

    /// <summary>
    /// FR-041. The earnings the engine produces, plus the allowances a company
    /// most often adds.
    ///
    /// <para>The de-minimis allowances are seeded at their current caps and
    /// flagged non-taxable. An amount <em>above</em> the cap is taxable, which
    /// is a per-run question rather than a property of the type — the excess is
    /// added as a taxable line instead of quietly making the whole allowance
    /// taxable.</para>
    /// </summary>
    private static async Task SeedEarningTypesAsync(SQLiteAsyncConnection connection)
    {
        // Seeded per code rather than all-or-nothing. The engine looks its own
        // lines up by code, so a database created before a system component
        // existed has to gain it on the next launch — but a component the
        // company has since edited, or retired, is left exactly as it is.
        var existing = (await connection.Table<EarningType>().ToListAsync().ConfigureAwait(false))
            .Select(e => e.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seed = new[]
        {
            new EarningType
            {
                Code = PayComponentCodes.BasicPay, Name = "Basic pay",
                Description = "Pay for the days and hours rendered in the period.",
                Category = EarningCategory.BasicPay,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsPartOfContributionBase = true,
                IsThirteenthMonthBase = true, IsSystem = true, DisplayOrder = 10
            },
            new EarningType
            {
                // Historical. The engine now writes the three codes below; this
                // stays so payslips computed before the split still have a name.
                Code = PayComponentCodes.Overtime, Name = "Overtime pay",
                Description = "Hours beyond the standard day, at the premium the day carries.",
                Category = EarningCategory.Overtime,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsSystem = true, DisplayOrder = 20
            },
            new EarningType
            {
                Code = PayComponentCodes.OvertimeRegular, Name = "Overtime — regular",
                Description = "Hours beyond the standard day on an ordinary working day.",
                Category = EarningCategory.Overtime,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsSystem = true, DisplayOrder = 21
            },
            new EarningType
            {
                Code = PayComponentCodes.OvertimeRestDay, Name = "Overtime — rest day",
                Description = "Overtime on a scheduled rest day. The summary's Sunday column.",
                Category = EarningCategory.Overtime,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsSystem = true, DisplayOrder = 22
            },
            new EarningType
            {
                Code = PayComponentCodes.OvertimeHoliday, Name = "Overtime — holiday",
                Description = "Overtime on a regular or special day, including one on a rest day.",
                Category = EarningCategory.Overtime,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsSystem = true, DisplayOrder = 23
            },
            new EarningType
            {
                Code = PayComponentCodes.NightDifferential, Name = "Night differential",
                Description = "Art. 86: 10% added for hours worked between 22:00 and 06:00.",
                Category = EarningCategory.NightDifferential,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsSystem = true, DisplayOrder = 30
            },
            new EarningType
            {
                Code = PayComponentCodes.RestDayPremium, Name = "Rest day premium",
                Description = "Art. 93: the premium for work on a scheduled rest day.",
                Category = EarningCategory.Other,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsSystem = true, DisplayOrder = 40
            },
            new EarningType
            {
                Code = PayComponentCodes.HolidayPay, Name = "Holiday premium",
                Description = "The premium for hours worked on a regular or special day.",
                Category = EarningCategory.HolidayPay,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsSystem = true, DisplayOrder = 50
            },
            new EarningType
            {
                Code = PayComponentCodes.HolidayUnworked, Name = "Unworked regular holiday",
                Description = "Art. 94: a regular holiday is paid even when it is not worked.",
                Category = EarningCategory.HolidayPay,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsThirteenthMonthBase = true,
                IsSystem = true, DisplayOrder = 55
            },
            new EarningType
            {
                Code = PayComponentCodes.PaidLeave, Name = "Paid leave",
                Description = "Approved leave granted as paid. Daily- and hourly-paid staff only — " +
                              "a monthly salary already covers the day.",
                Category = EarningCategory.Other,
                Method = ComputationMethod.TimeDerived,
                IsTaxable = true, IsThirteenthMonthBase = true,
                IsSystem = true, DisplayOrder = 57
            },
            new EarningType
            {
                Code = PayComponentCodes.RecurringAllowance, Name = "Allowance",
                Description = "The recurring allowance held on the employee record. " +
                              "Its tax treatment and contribution base are yours to set here.",
                Category = EarningCategory.Allowance,
                Method = ComputationMethod.FixedAmount,
                IsTaxable = true, IsPartOfContributionBase = false,
                IsSystem = true, DisplayOrder = 58
            },
            new EarningType
            {
                Code = PayComponentCodes.ThirteenthMonth, Name = "13th month pay",
                Description = "PD 851: a twelfth of the basic salary earned in the calendar year.",
                Category = EarningCategory.Bonus,
                Method = ComputationMethod.Manual,
                // Non-taxable only up to the ₱90,000 benefits exclusion. The
                // engine adds the excess as its own taxable line rather than
                // flipping this flag.
                IsTaxable = false, IsSystem = true, DisplayOrder = 60
            },
            new EarningType
            {
                Code = PayComponentCodes.LeaveConversion, Name = "Leave conversion",
                Description = "Art. 95: unused Service Incentive Leave converted to cash.",
                Category = EarningCategory.Other,
                Method = ComputationMethod.Manual,
                IsTaxable = true, IsSystem = true, DisplayOrder = 65
            },

            // Ordinary company earnings. Editable, and none of them are seeded
            // with a recurring flag — an allowance is granted per employee.
            new EarningType
            {
                Code = "ALW_RICE", Name = "Rice allowance",
                Description = "De-minimis up to ₱2,000 a month; the excess is taxable.",
                Category = EarningCategory.Allowance,
                Method = ComputationMethod.FixedAmount, DefaultAmount = 2000m,
                IsTaxable = false, DisplayOrder = 110
            },
            new EarningType
            {
                Code = "ALW_TRANS", Name = "Transportation allowance",
                Description = "Company transport allowance. Taxable unless it is a reimbursement.",
                Category = EarningCategory.Allowance,
                Method = ComputationMethod.FixedAmount,
                IsTaxable = true, IsPartOfContributionBase = true, DisplayOrder = 120
            },
            new EarningType
            {
                Code = "ALW_MEAL", Name = "Meal allowance",
                Description = "De-minimis where it does not exceed 25% of the basic minimum wage.",
                Category = EarningCategory.Allowance,
                Method = ComputationMethod.FixedAmount,
                IsTaxable = false, DisplayOrder = 130
            },
            new EarningType
            {
                Code = PayComponentCodes.SpecialEmergencyAllowance, Name = "Special emergency allowance",
                Description = "Wage-order SEA. Reported with ECOLA in the payroll summary.",
                Category = EarningCategory.Allowance,
                Method = ComputationMethod.FixedAmount,
                IsTaxable = false, IsRecurring = true, DisplayOrder = 115
            },
            new EarningType
            {
                Code = PayComponentCodes.FiveSlip, Name = "5Slip",
                Description =
                    "Art. 95 service incentive leave, accrued per day rendered rather than " +
                    "banked. The engine computes it from the daily rate; nobody types it.",
                Category = EarningCategory.Allowance,
                Method = ComputationMethod.RatePerDay,
                IsTaxable = false, IsRecurring = true, IsSystem = true, DisplayOrder = 118
            },
            new EarningType
            {
                Code = PayComponentCodes.CostOfLivingAllowance, Name = "Cost of living allowance",
                Description = "COLA under the applicable regional wage order.",
                Category = EarningCategory.Allowance,
                Method = ComputationMethod.FixedAmount,
                IsTaxable = true, IsPartOfContributionBase = true, DisplayOrder = 140
            },
            new EarningType
            {
                Code = "COMM", Name = "Commission",
                Description = "Sales commission earned in the period.",
                Category = EarningCategory.Commission,
                Method = ComputationMethod.Manual,
                IsTaxable = true, DisplayOrder = 150
            },
            new EarningType
            {
                Code = "BONUS", Name = "Bonus",
                Description = "Discretionary bonus. Counts against the ₱90,000 exclusion.",
                Category = EarningCategory.Bonus,
                Method = ComputationMethod.Manual,
                IsTaxable = true, DisplayOrder = 160
            }
        };

        var missing = seed.Where(e => !existing.Contains(e.Code)).ToList();

        if (missing.Count > 0)
            await connection.InsertAllAsync(missing).ConfigureAwait(false);

        await AdoptComputedFiveSlipAsync(connection).ConfigureAwait(false);
    }

    /// <summary>
    /// A database seeded before the engine computed <c>5Slip</c> holds it as an
    /// ordinary fixed allowance somebody typed per run. The engine now derives
    /// it from the daily rate (Art. 95), so the row is brought into line —
    /// otherwise a typed adjustment and the computed accrual would both be paid.
    ///
    /// <para><b>Only the shape is changed, never the tax treatment.</b> Whether
    /// the accrual is taxable is the company's call and may have been set
    /// deliberately; the flag that matters here is <see cref="EarningType.IsSystem"/>,
    /// which is what refuses a duplicate adjustment against the code.</para>
    /// </summary>
    private static async Task AdoptComputedFiveSlipAsync(SQLiteAsyncConnection connection)
    {
        var row = await connection.Table<EarningType>()
            .Where(e => e.Code == PayComponentCodes.FiveSlip)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (row is null || row.IsSystem)
            return;

        row.IsSystem = true;
        row.Method = ComputationMethod.RatePerDay;
        row.DefaultAmount = 0m;
        row.Description =
            "Art. 95 service incentive leave, accrued per day rendered rather than " +
            "banked. The engine computes it from the daily rate; nobody types it.";

        await connection.UpdateAsync(row).ConfigureAwait(false);
    }

    /// <summary>
    /// FR-042. The deductions the engine produces, plus the ordinary company
    /// ones.
    ///
    /// <para><see cref="DeductionType.Priority"/> matters when pay will not cover
    /// everything: statutory first, then loans, then the discretionary. What gets
    /// short should be the company's own deduction, never a remittance.</para>
    /// </summary>
    private static async Task SeedDeductionTypesAsync(SQLiteAsyncConnection connection)
    {
        var existing = (await connection.Table<DeductionType>().ToListAsync().ConfigureAwait(false))
            .Select(d => d.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seed = new[]
        {
            new DeductionType
            {
                Code = PayComponentCodes.Sss, Name = "SSS contribution",
                Description = "Employee share of the regular SS contribution, from the schedule.",
                Category = DeductionCategory.Statutory,
                Method = ComputationMethod.StatutoryTable,
                ReducesTaxableIncome = true, IsSystem = true,
                Priority = 10, DisplayOrder = 10
            },
            new DeductionType
            {
                Code = PayComponentCodes.SssWisp, Name = "SSS WISP",
                Description = "Provident fund portion for a salary credit above ₱20,000.",
                Category = DeductionCategory.Statutory,
                Method = ComputationMethod.StatutoryTable,
                ReducesTaxableIncome = true, IsSystem = true,
                Priority = 11, DisplayOrder = 15
            },
            new DeductionType
            {
                Code = PayComponentCodes.PhilHealth, Name = "PhilHealth premium",
                Description = "Employee half of the premium, computed on monthly basic salary.",
                Category = DeductionCategory.Statutory,
                Method = ComputationMethod.StatutoryTable,
                ReducesTaxableIncome = true, IsSystem = true,
                Priority = 12, DisplayOrder = 20
            },
            new DeductionType
            {
                Code = PayComponentCodes.PagIbig, Name = "Pag-IBIG contribution",
                Description = "Employee share of the HDMF contribution.",
                Category = DeductionCategory.Statutory,
                Method = ComputationMethod.StatutoryTable,
                ReducesTaxableIncome = true, IsSystem = true,
                Priority = 13, DisplayOrder = 30
            },
            new DeductionType
            {
                Code = PayComponentCodes.WithholdingTax, Name = "Withholding tax",
                Description = "BIR withholding on compensation, from the effective-dated brackets.",
                Category = DeductionCategory.Statutory,
                Method = ComputationMethod.StatutoryTable,
                // Tax does not reduce the base it is computed from.
                ReducesTaxableIncome = false, IsSystem = true,
                Priority = 14, DisplayOrder = 40
            },
            new DeductionType
            {
                Code = PayComponentCodes.Tardiness, Name = "Tardiness",
                Description = "FR-052: late minutes priced at the employee's hourly rate.",
                Category = DeductionCategory.Tardiness,
                Method = ComputationMethod.TimeDerived,
                IsSystem = true, Priority = 20, DisplayOrder = 50
            },
            new DeductionType
            {
                Code = PayComponentCodes.Undertime, Name = "Undertime",
                Description = "FR-052: hours short of the shift, at the hourly rate.",
                Category = DeductionCategory.Tardiness,
                Method = ComputationMethod.TimeDerived,
                IsSystem = true, Priority = 21, DisplayOrder = 60
            },
            new DeductionType
            {
                Code = PayComponentCodes.Absence, Name = "Absence",
                Description = "FR-052: unpaid days, at the employee's daily rate.",
                Category = DeductionCategory.Absence,
                Method = ComputationMethod.TimeDerived,
                IsSystem = true, Priority = 22, DisplayOrder = 70
            },

            // Ordinary company deductions.
            new DeductionType
            {
                Code = "SSS_LOAN", Name = "SSS salary loan",
                Description = "Amortisation of an SSS salary loan. Decrements each period.",
                Category = DeductionCategory.Loan,
                Method = ComputationMethod.FixedAmount,
                IsAmortised = true, Priority = 30, DisplayOrder = 110
            },
            new DeductionType
            {
                Code = "HDMF_LOAN", Name = "Pag-IBIG loan",
                Description = "Amortisation of a multi-purpose or calamity loan.",
                Category = DeductionCategory.Loan,
                Method = ComputationMethod.FixedAmount,
                IsAmortised = true, Priority = 31, DisplayOrder = 120
            },
            new DeductionType
            {
                Code = PayComponentCodes.SecondUniform, Name = "2nd uniform",
                Description = "The second uniform set, recovered over the periods agreed.",
                Category = DeductionCategory.Other,
                Method = ComputationMethod.FixedAmount,
                IsAmortised = true, Priority = 40, DisplayOrder = 145
            },
            new DeductionType
            {
                Code = PayComponentCodes.CompanyLoan, Name = "Company loan",
                Description = "Amortisation of a loan granted by the employer.",
                Category = DeductionCategory.Loan,
                Method = ComputationMethod.FixedAmount,
                IsAmortised = true, Priority = 32, DisplayOrder = 130
            },
            new DeductionType
            {
                Code = "CA", Name = "Cash advance",
                Description = "Recovery of an advance against salary.",
                Category = DeductionCategory.CashAdvance,
                Method = ComputationMethod.FixedAmount,
                IsAmortised = true, Priority = 40, DisplayOrder = 140
            },
            new DeductionType
            {
                Code = "UNION", Name = "Union dues",
                Description = "Dues checked off under a collective bargaining agreement.",
                Category = DeductionCategory.Other,
                Method = ComputationMethod.FixedAmount,
                ReducesTaxableIncome = true, Priority = 50, DisplayOrder = 150
            },

            // The legacy entry screen's remaining slots. None of the three
            // amortise: they are standing deductions set up per employee on
            // EmployeeDeduction and stopped by date, not by a balance running out.
            new DeductionType
            {
                Code = PayComponentCodes.PerformanceBond, Name = "Performance bond",
                Description =
                    "Accumulated against the guard and refundable on separation. Not a loan — " +
                    "it builds towards a figure rather than down from one.",
                Category = DeductionCategory.Other,
                Method = ComputationMethod.FixedAmount,
                Priority = 45, DisplayOrder = 155
            },
            new DeductionType
            {
                Code = PayComponentCodes.Insurance, Name = "Insurance",
                Description = "Group life premium, taken each period for as long as the cover runs.",
                Category = DeductionCategory.Other,
                Method = ComputationMethod.FixedAmount,
                Priority = 46, DisplayOrder = 160
            },
            new DeductionType
            {
                Code = PayComponentCodes.ProcessingFee, Name = "Processing fee",
                Description = "The agency's processing charge for the cut-off.",
                Category = DeductionCategory.Other,
                Method = ComputationMethod.FixedAmount,
                Priority = 47, DisplayOrder = 165
            }
        };

        var missing = seed.Where(d => !existing.Contains(d.Code)).ToList();

        if (missing.Count > 0)
            await connection.InsertAllAsync(missing).ConfigureAwait(false);
    }

    /// <summary>
    /// FR-044. The DOLE premium pay matrix.
    ///
    /// <para>These are the statutory minima under Arts. 87, 93 and 94. Each cell
    /// is the multiplier on the hourly rate for an hour worked in that
    /// combination; overtime cells already include the 30% overtime premium on
    /// top of the day's rate, which is why 1.30× rest day becomes 1.69× rest day
    /// overtime rather than 1.60×.</para>
    ///
    /// <para>Night differential is not a cell. It is 10% added to whatever rate
    /// already applies to the hour, so it is stored as an additive row.</para>
    /// </summary>
    private static async Task SeedPremiumRatesAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<PremiumRate>().CountAsync().ConfigureAwait(false) > 0)
            return;

        PremiumRate Cell(string code, string name, decimal multiplier, HolidayType day,
            bool restDay, bool overtime, string description) => new()
            {
                Code = code,
                Name = name,
                Multiplier = multiplier,
                DayType = day,
                IsRestDay = restDay,
                IsOvertime = overtime,
                Description = description,
                EffectiveFrom = StatutoryEffectiveFrom
            };

        await connection.InsertAllAsync(new[]
        {
            Cell(PremiumCodes.Ordinary, "Ordinary day", 1.00m,
                HolidayType.None, false, false,
                "An ordinary working hour. No premium."),

            Cell(PremiumCodes.OrdinaryOvertime, "Ordinary day overtime", 1.25m,
                HolidayType.None, false, true,
                "Art. 87: hours beyond eight earn an additional 25%."),

            Cell(PremiumCodes.RestDay, "Rest day", 1.30m,
                HolidayType.None, true, false,
                "Art. 93: work on a scheduled rest day earns an additional 30%."),

            Cell(PremiumCodes.RestDayOvertime, "Rest day overtime", 1.69m,
                HolidayType.None, true, true,
                "1.30 × 1.30 — the overtime premium is charged on the rest day rate."),

            Cell(PremiumCodes.SpecialWorking, "Special working day", 1.00m,
                HolidayType.SpecialWorking, false, false,
                "A special *working* day carries no premium; it is an ordinary day that was proclaimed."),

            Cell(PremiumCodes.SpecialNonWorking, "Special non-working day", 1.30m,
                HolidayType.SpecialNonWorking, false, false,
                "No work, no pay unless company policy says otherwise; worked hours pay 1.30×."),

            Cell(PremiumCodes.SpecialNonWorkingOvertime, "Special non-working day overtime", 1.69m,
                HolidayType.SpecialNonWorking, false, true,
                "1.30 × 1.30."),

            Cell(PremiumCodes.SpecialNonWorkingRestDay, "Special non-working day on a rest day", 1.50m,
                HolidayType.SpecialNonWorking, true, false,
                "A special day that falls on the employee's rest day."),

            Cell(PremiumCodes.SpecialNonWorkingRestDayOvertime,
                "Special non-working day on a rest day, overtime", 1.95m,
                HolidayType.SpecialNonWorking, true, true,
                "1.50 × 1.30."),

            Cell(PremiumCodes.RegularHoliday, "Regular holiday", 2.00m,
                HolidayType.Regular, false, false,
                "Art. 94: worked hours on a regular holiday pay double."),

            Cell(PremiumCodes.RegularHolidayOvertime, "Regular holiday overtime", 2.60m,
                HolidayType.Regular, false, true,
                "2.00 × 1.30."),

            Cell(PremiumCodes.RegularHolidayRestDay, "Regular holiday on a rest day", 2.60m,
                HolidayType.Regular, true, false,
                "2.00 × 1.30 — a regular holiday falling on the employee's rest day."),

            Cell(PremiumCodes.RegularHolidayRestDayOvertime,
                "Regular holiday on a rest day, overtime", 3.38m,
                HolidayType.Regular, true, true,
                "2.60 × 1.30."),

            new PremiumRate
            {
                Code = PremiumCodes.RegularHolidayUnworked,
                Name = "Regular holiday, unworked",
                Multiplier = 1.00m,
                DayType = HolidayType.Regular,
                IsUnworked = true,
                Description = "Art. 94: a regular holiday is paid even when unworked, " +
                              "provided the employee was present on the preceding workday.",
                EffectiveFrom = StatutoryEffectiveFrom
            },

            new PremiumRate
            {
                Code = PremiumCodes.NightDifferential,
                Name = "Night differential",
                Multiplier = 0.10m,
                IsAdditive = true,
                Description = "Art. 86: 10% added to the rate already applying, " +
                              "for hours worked between 22:00 and 06:00.",
                EffectiveFrom = StatutoryEffectiveFrom
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// FR-043. The SSS contribution schedule.
    ///
    /// <para><b>Generated from the circular's rule rather than typed.</b> The
    /// schedule is sixty-one brackets that all follow one arithmetic: the salary
    /// credit is the compensation rounded to the nearest ₱500 within the ₱5,000
    /// floor and ₱35,000 ceiling, the employee pays 5% of it and the employer
    /// 10%, the portion of the credit above ₱20,000 goes to the WISP instead of
    /// regular SS, and the employer's EC is a flat ₱10 below a ₱15,000 credit
    /// and ₱30 at or above it. Typing sixty-one rows by hand would introduce
    /// exactly the transcription errors this avoids — but the rates and the
    /// bounds still have to be checked against the current circular, because it
    /// is the rule that changes, not the arithmetic.</para>
    /// </summary>
    private static async Task SeedSssScheduleAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<SssBracket>().CountAsync().ConfigureAwait(false) > 0)
            return;

        const decimal step = 500m;
        const decimal floorMsc = 5000m;
        const decimal ceilingMsc = 35000m;
        const decimal regularCeiling = 20000m;
        const decimal employeeRate = 0.05m;
        const decimal employerRate = 0.10m;

        var brackets = new List<SssBracket>();

        for (var msc = floorMsc; msc <= ceilingMsc; msc += step)
        {
            var regular = Math.Min(msc, regularCeiling);
            var wisp = Math.Max(0m, msc - regularCeiling);

            brackets.Add(new SssBracket
            {
                EffectiveFrom = StatutoryEffectiveFrom,

                // The bracket is centred on its salary credit: ₱250 either side,
                // open at the bottom for the floor and unbounded at the top.
                RangeFrom = msc == floorMsc ? 0m : msc - step / 2m,
                RangeTo = msc == ceilingMsc ? null : msc + step / 2m,

                MonthlySalaryCredit = msc,
                EmployeeShare = PayrollRounding.Money(regular * employeeRate),
                EmployerShare = PayrollRounding.Money(regular * employerRate),
                EmployeeWisp = PayrollRounding.Money(wisp * employeeRate),
                EmployerWisp = PayrollRounding.Money(wisp * employerRate),
                EmployerEc = msc < 15000m ? 10m : 30m
            });
        }

        await connection.InsertAllAsync(brackets).ConfigureAwait(false);
    }

    /// <summary>FR-043. The PhilHealth premium in force (RA 11223).</summary>
    private static async Task SeedPhilHealthAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<PhilHealthRate>().CountAsync().ConfigureAwait(false) > 0)
            return;

        await connection.InsertAsync(new PhilHealthRate
        {
            EffectiveFrom = new DateTime(2024, 1, 1),
            PremiumRatePercent = 5.0m,
            SalaryFloor = 10000m,
            SalaryCeiling = 100000m,
            Remarks = "Universal Health Care Act premium schedule. " +
                      "Split equally between employee and employer. Verify against the current circular."
        }).ConfigureAwait(false);
    }

    /// <summary>FR-043. The Pag-IBIG (HDMF) contribution in force.</summary>
    private static async Task SeedPagIbigAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<PagIbigRate>().CountAsync().ConfigureAwait(false) > 0)
            return;

        await connection.InsertAsync(new PagIbigRate
        {
            EffectiveFrom = new DateTime(2024, 2, 1),
            LowerRateThreshold = 1500m,
            EmployeeRateLowPercent = 1.0m,
            EmployeeRateHighPercent = 2.0m,
            EmployerRatePercent = 2.0m,
            FundSalaryCap = 10000m,
            Remarks = "HDMF Circular 460 raised the fund salary cap to ₱10,000, " +
                      "making the effective maximum ₱200 each side. Verify before go-live."
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// FR-043, FR-054. The TRAIN withholding tax tables (RA 10963), as tabulated
    /// in RR 11-2018 Annex E for 2023 onward.
    ///
    /// <para>Five sets: the annual table the year-end settlement uses, and one
    /// per pay frequency for what a run actually withholds. The <b>bounds</b> are
    /// the published ones — the BIR rounds them, so dividing the annual brackets
    /// here would disagree with the table an auditor is holding. The <b>base tax</b>
    /// of each band is then the tax at the band below it, which makes the table
    /// continuous at every boundary; a transcribed figure that is a peso out
    /// produces a step in the withholding that nobody sees until year end.</para>
    ///
    /// <para>The annual set carries a frequency only because the column is not
    /// nullable; <see cref="WithholdingTaxBracket.IsAnnual"/> is what selects it.</para>
    ///
    /// <para><b>There is no bi-weekly set</b>, because the BIR publishes no
    /// bi-weekly table. Seeding the weekly brackets under that frequency would
    /// under-withhold a fortnight's pay every period, so the frequency is left
    /// without a table and
    /// <see cref="PayrollEnumNames.HasPublishedTaxTable"/> says so where a
    /// payroll officer will see it.</para>
    /// </summary>
    private static async Task SeedTaxBracketsAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<WithholdingTaxBracket>().CountAsync().ConfigureAwait(false) > 0)
            return;

        var rows = new List<WithholdingTaxBracket>();

        void AddSet(PayFrequency frequency, bool annual, (decimal Lower, decimal? Upper, decimal Base, decimal Rate)[] bands)
        {
            foreach (var band in bands)
            {
                rows.Add(new WithholdingTaxBracket
                {
                    EffectiveFrom = StatutoryEffectiveFrom,
                    Frequency = frequency,
                    IsAnnual = annual,
                    LowerLimit = band.Lower,
                    UpperLimit = band.Upper,
                    BaseTax = band.Base,
                    RateOnExcessPercent = band.Rate
                });
            }
        }

        // Annual — the settlement table, RA 10963 §5 as amended for 2023 onward.
        AddSet(PayFrequency.Monthly, annual: true,
        [
            (0m,         250_000m,   0m,           0m),
            (250_000m,   400_000m,   0m,          15m),
            (400_000m,   800_000m,   22_500m,     20m),
            (800_000m,   2_000_000m, 102_500m,    25m),
            (2_000_000m, 8_000_000m, 402_500m,    30m),
            (8_000_000m, null,       2_202_500m, 35m)
        ]);

        AddSet(PayFrequency.Monthly, annual: false,
        [
            (0m,       20_833m,  0m,          0m),
            (20_833m,  33_333m,  0m,         15m),
            (33_333m,  66_667m,  1_875.00m,  20m),
            (66_667m,  166_667m, 8_541.80m,  25m),
            (166_667m, 666_667m, 33_541.80m, 30m),
            (666_667m, null,     183_541.80m, 35m)
        ]);

        AddSet(PayFrequency.SemiMonthly, annual: false,
        [
            (0m,       10_417m,  0m,          0m),
            (10_417m,  16_667m,  0m,         15m),
            (16_667m,  33_333m,  937.50m,    20m),
            (33_333m,  83_333m,  4_270.70m,  25m),
            (83_333m,  333_333m, 16_770.70m, 30m),
            (333_333m, null,     91_770.70m, 35m)
        ]);

        AddSet(PayFrequency.Weekly, annual: false,
        [
            (0m,       4_808m,   0m,          0m),
            (4_808m,   7_692m,   0m,         15m),
            (7_692m,   15_385m,  432.60m,    20m),
            (15_385m,  38_462m,  1_971.20m,  25m),
            (38_462m,  153_846m, 7_740.45m,  30m),
            (153_846m, null,     42_355.65m, 35m)
        ]);

        AddSet(PayFrequency.Daily, annual: false,
        [
            (0m,      685m,     0m,        0m),
            (685m,    1_096m,   0m,       15m),
            (1_096m,  2_192m,   61.65m,   20m),
            (2_192m,  5_479m,   280.85m,  25m),
            (5_479m,  21_918m,  1_102.60m, 30m),
            (21_918m, null,     6_034.30m, 35m)
        ]);

        await connection.InsertAllAsync(rows).ConfigureAwait(false);
    }

    /// <summary>
    /// FR-040. A calendar for the current year, so attendance recorded today
    /// already belongs to a period.
    ///
    /// <para>Only the current year: generating several would fill the list with
    /// periods whose pay dates nobody has checked against a bank calendar.</para>
    /// </summary>
    private static async Task SeedPayPeriodsAsync(SQLiteAsyncConnection connection)
    {
        if (await connection.Table<PayPeriod>().CountAsync().ConfigureAwait(false) > 0)
            return;

        var settings = await connection.Table<PayrollSettings>()
            .FirstOrDefaultAsync()
            .ConfigureAwait(false) ?? new PayrollSettings();

        var periods = PayPeriodGenerator.ForYear(DateTime.Today.Year, settings);

        await connection.InsertAllAsync(periods).ConfigureAwait(false);
    }
}
