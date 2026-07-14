using Temporalio.Common;
using Temporalio.Workflows;
using PBMAdjudication.Core;

namespace PBMAdjudication.Worker
{
    [Workflow]
    public class PaymentWorkflow
    {
        /// <summary>
        /// Set at worker startup from the USE_GLP1_SPLIT environment variable.
        /// false = v1 behavior (single-track, ignores IsHighRisk flag)
        /// true  = v2 behavior (GLP-1 prescriptions split into parallel child workflows)
        /// </summary>
        public static bool UseGlp1Split { get; set; } = false;

        private bool approvalReceived = false;
        private bool approvalDenied = false;

        private static readonly ActivityOptions DefaultActivityOptions = new()
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

        private static readonly ActivityOptions NotificationActivityOptions = new()
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
        public async Task<WorkflowResult> RunAsync(PrescriptionInput input, string? imageData = null)
        {
            var result = new WorkflowResult { Success = false, Status = "Pending" };

            // Step 0: Validate Eligibility
            try
            {
                var validated = await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.VerifyAccountAsync(input.TransferId, imageData),
                    DefaultActivityOptions);

                if (!validated.Eligible && validated.EligibleDate.HasValue)
                {
                    result.Status = "Waiting";
                    var waitTime = validated.EligibleDate.Value - Workflow.UtcNow;
                    if (waitTime > TimeSpan.Zero)
                        await Workflow.DelayAsync(waitTime);

                    validated = await Workflow.ExecuteActivityAsync(
                        (PrescriptionActivities a) => a.VerifyAccountAsync(input.TransferId),
                        DefaultActivityOptions);
                }
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.TransferId, 0);
                return result;
            }

            result.Status = "Validated";

            // Step 1: Prior Authorization
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.ScreenSanctionsAsync(input.TransferId),
                    DefaultActivityOptions);
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.TransferId, 1);
                return result;
            }

            result.Status = "Authorized";

            // Step 2: Adjudicate Claim
            // v2 behavior: GLP-1 prescriptions are split into two parallel child workflows —
            // one for the standard line items (existing adjudication path) and one for the
            // GLP-1 line item (specialty endpoint + mandatory specialty prior authorization).
            // This structural change to the workflow DAG is what makes Worker Versioning
            // necessary: a v1 execution cannot be replayed on v2 code without a
            // non-determinism error.
            if (input.IsHighRisk && UseGlp1Split)
            {
                try
                {
                    var standardTask = Workflow.ExecuteChildWorkflowAsync(
                        (StandardAdjudicationWorkflow w) => w.RunAsync(input.TransferId),
                        new ChildWorkflowOptions
                        {
                            Id = $"{input.TransferId}-standard"
                        });

                    var glp1Task = Workflow.ExecuteChildWorkflowAsync(
                        (EddAdjudicationWorkflow w) => w.RunAsync(
                            input.TransferId, input.CustomerName, input.RecipientName, input.Amount, input.CurrencyCorridor),
                        new ChildWorkflowOptions
                        {
                            Id = $"{input.TransferId}-glp1"
                        });

                    // Both tracks run in parallel; parent blocks until both complete.
                    // The GLP-1 child may be parked on a specialty auth signal for
                    // minutes (demo) or days (production).
                    var results = await Task.WhenAll(standardTask, glp1Task);

                    result.Copay = results.Sum(r => r.Copay);
                    result.Success = results.All(r => r.Success);

                    if (result.Success)
                    {
                        // Both tracks complete — submit to pharmacy and mark done
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.SettlePaymentAsync(input.TransferId),
                            DefaultActivityOptions);
                        result.Status = "Completed";
                    }
                    else
                    {
                        result.Status = "Denied";
                    }
                    return result;
                }
                catch (Temporalio.Exceptions.ActivityFailureException)
                {
                    await HandlePrescriptionFailureAsync(result, input.TransferId, 2);
                    return result;
                }
            }

            // Non-GLP-1 path: original single-track adjudication (unchanged from v1)
            try
            {
                var adjudication = await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.CalculateFxFeesAsync(input.TransferId),
                    DefaultActivityOptions);

                result.Copay = adjudication.Copay;
                result.Status = "Adjudicated";
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.TransferId, 2);
                return result;
            }

            // Step 3: Doctor Approval (if needed — 0 refills remaining)
            try
            {
                var approvalResult = await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.RequestComplianceReviewAsync(input.TransferId),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5) });

                if (approvalResult.ApprovalNeeded)
                {
                    result.Status = "ApprovalNeeded";

                    try
                    {
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.SendNotificationAsync(
                                input.TransferId, "patient", input.CustomerName,
                                $"Your transfer of ${input.Amount:N2} ({input.CurrencyCorridor}) to {input.RecipientName} is awaiting compliance review."),
                            NotificationActivityOptions);
                    }
                    catch
                    {
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.MarkNotificationFailedAsync(input.TransferId),
                            DefaultActivityOptions);
                    }

                    // Wait up to 2 minutes for doctor approval
                    var receivedResponse = await Workflow.WaitConditionAsync(
                        () => approvalReceived || approvalDenied,
                        TimeSpan.FromMinutes(2));

                    if (!receivedResponse)
                    {
                        try
                        {
                            await Workflow.ExecuteActivityAsync(
                                (PrescriptionActivities a) => a.HandleReviewTimeoutAsync(input.TransferId),
                                DefaultActivityOptions);
                        }
                        catch (Temporalio.Exceptions.ActivityFailureException)
                        {
                            // Best-effort — workflow still completes with ApprovalTimeout
                        }

                        result.Status = "ApprovalTimeout";
                        result.Success = false;
                        return result;
                    }

                    if (approvalDenied)
                    {
                        result.Status = "Denied";
                        result.Success = false;
                        return result;
                    }

                    result.Status = "Approved";
                    try
                    {
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.SendNotificationAsync(
                                input.TransferId, "patient", input.CustomerName,
                                $"Your transfer of ${input.Amount:N2} ({input.CurrencyCorridor}) to {input.RecipientName} has been approved and settled."),
                            NotificationActivityOptions);
                    }
                    catch
                    {
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.MarkNotificationFailedAsync(input.TransferId),
                            DefaultActivityOptions);
                    }
                }
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.TransferId, 3);
                return result;
            }

            // Step 4: Submit to Pharmacy
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.SettlePaymentAsync(input.TransferId),
                    DefaultActivityOptions);

                result.Status = "Completed";
                result.Success = true;

                try
                {
                    await Workflow.ExecuteActivityAsync(
                        (PrescriptionActivities a) => a.SendNotificationAsync(
                            input.TransferId, "patient", input.CustomerName,
                            $"Your transfer of ${input.Amount:N2} ({input.CurrencyCorridor}) to {input.RecipientName} has been submitted for settlement."),
                        NotificationActivityOptions);
                }
                catch (Temporalio.Exceptions.ActivityFailureException)
                {
                    await Workflow.ExecuteActivityAsync(
                        (PrescriptionActivities a) => a.MarkNotificationFailedAsync(input.TransferId),
                        DefaultActivityOptions);
                }
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.TransferId, 4);
            }

            return result;
        }

        [WorkflowSignal]
        public async Task ApproveAsync()
        {
            approvalReceived = true;
        }

        [WorkflowSignal]
        public async Task DenyAsync()
        {
            approvalDenied = true;
        }

        [WorkflowQuery]
        public string GetStatus()
        {
            if (approvalDenied) return "Denied";
            if (approvalReceived) return "Approved";
            return "Processing";
        }

        private async Task HandlePrescriptionFailureAsync(WorkflowResult result, string prescriptionId, int failedStep)
        {
            result.Status = "OnHold";
            result.Success = false;
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.MarkOnHoldAsync(prescriptionId, failedStep),
                    DefaultActivityOptions);
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                // Best-effort — state will be reconciled when API recovers
            }
        }
    }
}
