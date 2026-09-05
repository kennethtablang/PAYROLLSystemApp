namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-031. Where a request has got to.
///
/// <para>There is no "Draft". A request that has not been submitted is not a
/// record anyone needs to keep, and a status nobody acts on is a status that
/// only makes the queue harder to read.</para>
/// </summary>
public enum LeaveRequestStatus
{
    /// <summary>Filed and waiting on an approver.</summary>
    Pending = 0,

    /// <summary>Granted. The credits are spent and the days are marked on attendance.</summary>
    Approved = 1,

    /// <summary>Refused. Nothing is deducted, and the reason is kept.</summary>
    Rejected = 2,

    /// <summary>
    /// Withdrawn after the fact. Anything already deducted is given back, which
    /// is why a cancelled request is retained rather than deleted.
    /// </summary>
    Cancelled = 3
}

/// <summary>
/// FR-030. Who a leave type is available to.
///
/// <para>Maternity leave (RA 11210) and paternity leave (RA 8187) are written
/// into law for one sex each, so the filing screen should not offer them to
/// everybody and then reject the filing.</para>
/// </summary>
public enum LeaveApplicability
{
    Everyone = 0,
    FemaleOnly = 1,
    MaleOnly = 2
}

/// <summary>
/// Display names for the leave enumerations, in one place so the request queue,
/// the balance list and a later report cannot word the same value differently
/// (NFR-024).
/// </summary>
public static class LeaveEnumNames
{
    public static string Display(LeaveRequestStatus value) => value.ToString();

    public static string Display(LeaveApplicability value) => value switch
    {
        LeaveApplicability.Everyone => "Everyone",
        LeaveApplicability.FemaleOnly => "Female employees only",
        LeaveApplicability.MaleOnly => "Male employees only",
        _ => value.ToString()
    };

    /// <summary>Whether an employee's sex admits them to a leave type.</summary>
    public static bool Admits(LeaveApplicability applicability, Gender gender) => applicability switch
    {
        LeaveApplicability.FemaleOnly => gender == Gender.Female,
        LeaveApplicability.MaleOnly => gender == Gender.Male,
        _ => true
    };
}
