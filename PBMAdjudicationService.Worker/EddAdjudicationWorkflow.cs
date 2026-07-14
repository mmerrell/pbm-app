using Temporalio.Common;
using Temporalio.Workflows;
using PBMAdjudication.Core;

namespace PBMAdjudication.Worker
{
    /// <summary>
    /// Child workflow (v2+): handles the GLP-1 line item when a prescription is
    /// split at adjudication. Always requires specialty prior authorization — a
    /// human-in-the-loop step reflecting the new regulatory framework for GLP-1
    /// drugs. Times out after 5 minutes if no signal is received (represents days
    /// in production).
    /// </summary>
    [Workflow]
    public class EddAdjudicationWorkflow
    {
        private bool specialtyApprovalReceived = false;
        private bool specialtyApprovalDenied = false;

        private static readonly ActivityOptions DefaultOptions = new()
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(5),
            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromSeconds(1),
                MaximumInterval = TimeSpan.FromSeconds(10),
                BackoffCoefficient = 2,
                MaximumAttempts = 10
            }
        };

        private static readonly ActivityOptions NotificationOptions = new()
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(2),
            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromSeconds(1),
                MaximumInterval = TimeSpan.FromSeconds(10),
                BackoffCoefficient = 2,
                MaximumAttempts = 5
            }
        };

        [WorkflowRun]
        public async Task<AdjudicationChildResult> RunAsync(
            string prescriptionId,
            string patientName,
            string recipientName,
            decimal amount,
            string medication)
        {
            var result = new AdjudicationChildResult { Track = "glp1" };

            // Adjudicate through the specialty endpoint
            var adjudication = await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.AdjudicateEddClaimAsync(prescriptionId),
                DefaultOptions);

            result.Copay = adjudication.Copay;

            // High-risk transfers always require enhanced due diligence review
            await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.RequestEddReviewAsync(prescriptionId, patientName, medication),
                DefaultOptions);

            // Notify patient that EDD review is pending
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.SendNotificationAsync(
                        prescriptionId, "patient", patientName,
                        $"Your transfer of ${amount:N2} ({medication}) to {recipientName} requires enhanced due diligence review. " +
                        $"A compliance reviewer has been assigned."),
                    NotificationOptions);
            }
            catch
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.MarkNotificationFailedAsync(prescriptionId),
                    DefaultOptions);
            }

            // Wait up to 5 minutes for specialty approval signal
            // (In production this would be days — the long-lived execution that
            // demonstrates why Worker Versioning matters: this workflow is pinned
            // to the version it started on and cannot be moved to a new version.)
            var responseReceived = await Workflow.WaitConditionAsync(
                () => specialtyApprovalReceived || specialtyApprovalDenied,
                TimeSpan.FromMinutes(5));

            if (!responseReceived)
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.HandleEddReviewTimeoutAsync(prescriptionId),
                    DefaultOptions);

                result.Success = false;
                return result;
            }

            if (specialtyApprovalDenied)
            {
                result.Success = false;
                return result;
            }

            // Approved — submit the GLP-1 line to the specialty pharmacy
            await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.SettleEnhancedRailAsync(prescriptionId),
                DefaultOptions);

            result.Success = true;
            return result;
        }

        // Deliberately distinct signal names from the parent workflow's
        // ApproveAsync/DenyAsync so there is no ambiguity in routing or UI.
        [WorkflowSignal]
        public async Task SpecialtyApproveAsync()
        {
            specialtyApprovalReceived = true;
        }

        [WorkflowSignal]
        public async Task SpecialtyDenyAsync()
        {
            specialtyApprovalDenied = true;
        }

        [WorkflowQuery]
        public string GetSpecialtyAuthStatus()
        {
            if (specialtyApprovalDenied) return "Denied";
            if (specialtyApprovalReceived) return "Approved";
            return "AwaitingSpecialtyAuth";
        }
    }
}
