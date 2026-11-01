namespace AlShifa.Api.Features.Visits;

/// <summary>Allowed visit transitions; mirrors WORKFLOWS.md and is deliberately permissive of skipped steps.</summary>
public static class VisitWorkflow
{
    private static readonly Dictionary<VisitStatus, VisitStatus[]> Allowed = new()
    {
        [VisitStatus.Registered] = [VisitStatus.Waiting, VisitStatus.InPreExam, VisitStatus.ReadyForDoctor, VisitStatus.InConsultation, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.Waiting] = [VisitStatus.InPreExam, VisitStatus.ReadyForDoctor, VisitStatus.InConsultation, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.InPreExam] = [VisitStatus.ReadyForDoctor, VisitStatus.Waiting, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.ReadyForDoctor] = [VisitStatus.InConsultation, VisitStatus.Waiting, VisitStatus.Cancelled],
        [VisitStatus.InConsultation] = [VisitStatus.AwaitingDiagnostic, VisitStatus.FollowUpRequired, VisitStatus.Completed, VisitStatus.Waiting],
        [VisitStatus.AwaitingDiagnostic] = [VisitStatus.InConsultation, VisitStatus.ReadyForDoctor, VisitStatus.Completed, VisitStatus.Cancelled],
        [VisitStatus.FollowUpRequired] = [VisitStatus.Completed],
        [VisitStatus.Completed] = [],
        [VisitStatus.Cancelled] = [],
    };

    public static IReadOnlyList<VisitStatus> NextOf(VisitStatus status) => Allowed[status];

    public static bool CanMove(VisitStatus from, VisitStatus to) => Allowed[from].Contains(to);
}
