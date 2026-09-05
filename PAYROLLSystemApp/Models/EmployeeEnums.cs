namespace PAYROLLSystemApp.Models;

/// <summary>FR-011. Recorded because government reporting asks for it.</summary>
public enum Gender
{
    Unspecified = 0,
    Male = 1,
    Female = 2
}

/// <summary>
/// FR-011. Kept for the employee record and for reporting only.
///
/// It deliberately does <b>not</b> feed withholding tax. The TRAIN law
/// (RA 10963) removed personal and additional exemptions, so tax no longer
/// depends on civil status or dependants; treating it as if it did would
/// produce a wrong payslip.
/// </summary>
public enum CivilStatus
{
    Single = 0,
    Married = 1,
    Widowed = 2,
    Separated = 3,
    Divorced = 4
}

/// <summary>
/// FR-012. Broader than the four the requirement names, because the payroll
/// engine treats project-based, seasonal and consultant engagements
/// differently from a plain contractual one.
/// </summary>
public enum EmploymentStatus
{
    Probationary = 0,
    Regular = 1,
    Contractual = 2,
    ProjectBased = 3,
    PartTime = 4,
    Seasonal = 5,
    Consultant = 6
}

/// <summary>FR-013. Decides how <see cref="Employee.BasicRate"/> is read.</summary>
public enum PayType
{
    Monthly = 0,
    Daily = 1,
    Hourly = 2
}

/// <summary>A-02: semi-monthly is the default; the rest are configurable.</summary>
public enum PayFrequency
{
    SemiMonthly = 0,
    Monthly = 1,
    Weekly = 2,
    BiWeekly = 3,
    Daily = 4
}

/// <summary>
/// Display names for the employee enumerations, in one place so a list, a form
/// and a payslip cannot word the same value differently (NFR-024).
/// </summary>
public static class EmployeeEnumNames
{
    public static string Display(Gender value) => value switch
    {
        Gender.Unspecified => "Not stated",
        _ => value.ToString()
    };

    public static string Display(CivilStatus value) => value.ToString();

    public static string Display(EmploymentStatus value) => value switch
    {
        EmploymentStatus.ProjectBased => "Project-based",
        EmploymentStatus.PartTime => "Part-time",
        _ => value.ToString()
    };

    public static string Display(PayType value) => value switch
    {
        PayType.Monthly => "Monthly rate",
        PayType.Daily => "Daily rate",
        PayType.Hourly => "Hourly rate",
        _ => value.ToString()
    };

    public static string Display(PayFrequency value) => value switch
    {
        PayFrequency.SemiMonthly => "Semi-monthly (1-15, 16-EOM)",
        PayFrequency.BiWeekly => "Bi-weekly",
        _ => value.ToString()
    };

    /// <summary>How many pay periods a frequency produces in a year.</summary>
    public static int PeriodsPerYear(PayFrequency value) => value switch
    {
        PayFrequency.SemiMonthly => 24,
        PayFrequency.Monthly => 12,
        PayFrequency.Weekly => 52,
        PayFrequency.BiWeekly => 26,
        PayFrequency.Daily => 313,
        _ => 24
    };
}
