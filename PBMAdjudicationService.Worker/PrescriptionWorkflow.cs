using Temporalio.Common;
using Temporalio.Workflows;
using PBMAdjudication.Core;

namespace PBMAdjudication.Worker
{
    [Workflow]
    public class PrescriptionWorkflow
    {
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
                    (PrescriptionActivities a) => a.ValidateEligibilityAsync(input.PrescriptionId, imageData),
                    DefaultActivityOptions);

                if (!validated.Eligible && validated.EligibleDate.HasValue)
                {
                    result.Status = "Waiting";
                    var waitTime = validated.EligibleDate.Value - Workflow.UtcNow;
                    if (waitTime > TimeSpan.Zero)
                        await Workflow.DelayAsync(waitTime);

                    validated = await Workflow.ExecuteActivityAsync(
                        (PrescriptionActivities a) => a.ValidateEligibilityAsync(input.PrescriptionId),
                        DefaultActivityOptions);
                }
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.PrescriptionId, 0);
                return result;
            }

            result.Status = "Validated";

            // Step 1: Prior Authorization
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.CheckPriorAuthorizationAsync(input.PrescriptionId),
                    DefaultActivityOptions);
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.PrescriptionId, 1);
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
            if (input.IsGlp1)
            {
                try
                {
                    var standardTask = Workflow.ExecuteChildWorkflowAsync(
                        (StandardAdjudicationWorkflow w) => w.RunAsync(input.PrescriptionId),
                        new ChildWorkflowOptions
                        {
                            Id = $"{input.PrescriptionId}-standard"
                        });

                    var glp1Task = Workflow.ExecuteChildWorkflowAsync(
                        (Glp1AdjudicationWorkflow w) => w.RunAsync(
                            input.PrescriptionId, input.PatientName, input.Medication),
                        new ChildWorkflowOptions
                        {
                            Id = $"{input.PrescriptionId}-glp1"
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
                            (PrescriptionActivities a) => a.SubmitToPharmacyAsync(input.PrescriptionId),
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
                    await HandlePrescriptionFailureAsync(result, input.PrescriptionId, 2);
                    return result;
                }
            }

            // Non-GLP-1 path: original single-track adjudication (unchanged from v1)
            try
            {
                var adjudication = await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.AdjudicateClaimAsync(input.PrescriptionId),
                    DefaultActivityOptions);

                result.Copay = adjudication.Copay;
                result.Status = "Adjudicated";
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.PrescriptionId, 2);
                return result;
            }

            // Step 3: Doctor Approval (if needed — 0 refills remaining)
            try
            {
                var approvalResult = await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.RequestDoctorApprovalAsync(input.PrescriptionId),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5) });

                if (approvalResult.ApprovalNeeded)
                {
                    result.Status = "ApprovalNeeded";

                    try
                    {
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.SendNotificationAsync(
                                input.PrescriptionId, "patient", input.PatientName,
                                $"Your refill for {input.Medication} is awaiting doctor approval."),
                            NotificationActivityOptions);
                    }
                    catch
                    {
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.MarkNotificationFailedAsync(input.PrescriptionId),
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
                                (PrescriptionActivities a) => a.HandleApprovalTimeoutAsync(input.PrescriptionId),
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
                                input.PrescriptionId, "patient", input.PatientName,
                                $"Your refill for {input.Medication} has been approved and sent to pharmacy."),
                            NotificationActivityOptions);
                    }
                    catch
                    {
                        await Workflow.ExecuteActivityAsync(
                            (PrescriptionActivities a) => a.MarkNotificationFailedAsync(input.PrescriptionId),
                            DefaultActivityOptions);
                    }
                }
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.PrescriptionId, 3);
                return result;
            }

            // Step 4: Submit to Pharmacy
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.SubmitToPharmacyAsync(input.PrescriptionId),
                    DefaultActivityOptions);

                result.Status = "Completed";
                result.Success = true;

                try
                {
                    await Workflow.ExecuteActivityAsync(
                        (PrescriptionActivities a) => a.SendNotificationAsync(
                            input.PrescriptionId, "patient", input.PatientName,
                            $"Your prescription for {input.Medication} has been submitted to the pharmacy."),
                        NotificationActivityOptions);
                }
                catch (Temporalio.Exceptions.ActivityFailureException)
                {
                    await Workflow.ExecuteActivityAsync(
                        (PrescriptionActivities a) => a.MarkNotificationFailedAsync(input.PrescriptionId),
                        DefaultActivityOptions);
                }
            }
            catch (Temporalio.Exceptions.ActivityFailureException)
            {
                await HandlePrescriptionFailureAsync(result, input.PrescriptionId, 4);
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
