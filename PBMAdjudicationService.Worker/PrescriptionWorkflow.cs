using Temporalio.Api.Update.V1;
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
        private ActivityOptions DefaultActivityOptions = new()
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

        private ActivityOptions NotificationActivityOptions = new()
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
            // imageData is passed as a separate workflow argument so the ClaimCheckCodec
            // can offload it independently — the Rx fields in `input` remain visible
            // in Temporal history while only the image payload is claim-checked.
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
                    {
                        await Workflow.DelayAsync(waitTime);
                    }
                    // After waiting, validate again (no image needed for retry)
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

            // Step 3: Doctor Approval (if needed)
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
                        TimeSpan.FromMinutes(2)
                    );

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
                            // API may be temporarily unavailable - workflow still completes
                            // with ApprovalTimeout status. State will be reconciled when API recovers.
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
                // API may be temporarily unavailable - workflow still completes
                // with OnHold status. State will be reconciled when API recovers.
            }
        }
    }
}
