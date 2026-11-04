namespace AlShifa.Api.Features.Visits;

/// <summary>Allowed visit transitions; mirrors WORKFLOWS.md and is deliberately permissive of skipped steps.</summary>
public static class VisitWorkflow
{
    private static readonly Dictionary<VisitStatus, VisitStatus[]> Allowed = new()
    {
        [VisitStatus.Registered] = [VisitStatus.Waiting, VisitStatus.InPreExam, VisitStatus.ReadyForDoctor, VisitStatus.InConsultation, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.Waiting] = [VisitStatus.InPreExam, VisitStatus.ReadyForDoctor, VisitStatus.InConsultation, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.InPreExam] = [VisitStatus.ReadyForDoctor, VisitStatus.Waiting, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.ReadyForDoctor] = [VisitStatus.InConsultation, VisitStatus.AwaitingDiagnostic, VisitStatus.Waiting, VisitStatus.Cancelled],
        [VisitStatus.InConsultation] = [VisitStatus.AwaitingDiagnostic, VisitStatus.FollowUpRequired, VisitStatus.Completed, VisitStatus.Waiting],
        [VisitStatus.AwaitingDiagnostic] = [VisitStatus.InConsultation, VisitStatus.ReadyForDoctor, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.FollowUpRequired] = [VisitStatus.Completed],
        [VisitStatus.Completed] = [],
        [VisitStatus.Cancelled] = [],

        // Only a settled fee moves a visit on from here, and that happens in VisitFeeService, never by hand.
        [VisitStatus.AwaitingPayment] = [VisitStatus.Cancelled],
    };

    /// <summary>Statuses that put a patient in front of a doctor, which no one may set by hand before Vision is complete.</summary>
    public static bool NeedsVision(VisitStatus to) => to is VisitStatus.ReadyForDoctor or VisitStatus.InConsultation;

    public static IReadOnlyList<VisitStatus> NextOf(VisitStatus status, bool visionComplete) =>
        visionComplete ? Allowed[status] : Allowed[status].Where(s => !NeedsVision(s)).ToList();

    public static bool CanMove(VisitStatus from, VisitStatus to) => Allowed[from].Contains(to);
}
