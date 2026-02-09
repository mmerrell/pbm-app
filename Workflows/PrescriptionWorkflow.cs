using Temporalio.Workflows;
using PBMAdjudicationService.Models;
using PBMAdjudicationService.Activities;

namespace PBMAdjudicationService.Workflows
{
    [Workflow]
    public class PrescriptionWorkflow
    {
        private bool approvalReceived = false;
        private bool approvalDenied = false;

        [WorkflowRun]
        public async Task<WorkflowResult> RunAsync(PrescriptionInput input)
        {
            var result = new WorkflowResult { Success = false, Status = "Pending" };

            // Step 1: Validate Eligibility
            var validated = await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.ValidateEligibilityAsync(input.PrescriptionId),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new()
                    {
                        InitialInterval = TimeSpan.FromSeconds(1),
                        MaximumInterval = TimeSpan.FromSeconds(10),
                        BackoffCoefficient = 2,
                        MaximumAttempts = 3
                    }
                });

            if (!validated.Eligible && validated.EligibleDate.HasValue)
            {
                // Not eligible yet - use Temporal's durable timer!
                result.Status = "Waiting";
                var waitTime = validated.EligibleDate.Value - Workflow.UtcNow;
                if (waitTime > TimeSpan.Zero)
                {
                    await Workflow.DelayAsync(waitTime);
                }
                // After waiting, validate again
                validated = await Workflow.ExecuteActivityAsync(
                    (PrescriptionActivities a) => a.ValidateEligibilityAsync(input.PrescriptionId),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5) });
            }

            result.Status = "Validated";

            // Step 2: Prior Authorization
            await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.CheckPriorAuthorizationAsync(input.PrescriptionId),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new()
                    {
                        InitialInterval = TimeSpan.FromSeconds(1),
                        MaximumInterval = TimeSpan.FromSeconds(10),
                        BackoffCoefficient = 2,
                        MaximumAttempts = 3
                    }
                });

            result.Status = "Authorized";

            // Step 3: Adjudicate Claim
            var adjudication = await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.AdjudicateClaimAsync(input.PrescriptionId),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new()
                    {
                        InitialInterval = TimeSpan.FromSeconds(1),
                        MaximumInterval = TimeSpan.FromSeconds(10),
                        BackoffCoefficient = 2,
                        MaximumAttempts = 3
                    }
                });

            result.Copay = adjudication.Copay;
            result.Status = "Adjudicated";

            // Step 4: Doctor Approval (if needed)
            var approvalResult = await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.RequestDoctorApprovalAsync(input.PrescriptionId),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5) });

            if (approvalResult.ApprovalNeeded)
            {
                result.Status = "ApprovalNeeded";

                // Wait for signal from doctor - this is the human-in-the-loop!
                // Workflow will pause here indefinitely until signal received
                await Workflow.WaitConditionAsync(() => approvalReceived || approvalDenied);

                if (approvalDenied)
                {
                    result.Status = "Denied";
                    result.Success = false;
                    return result;
                }

                result.Status = "Approved";
            }

            // Step 5: Submit to Pharmacy
            await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.SubmitToPharmacyAsync(input.PrescriptionId),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new()
                    {
                        InitialInterval = TimeSpan.FromSeconds(1),
                        MaximumInterval = TimeSpan.FromSeconds(10),
                        BackoffCoefficient = 2,
                        MaximumAttempts = 3
                    }
                });

            result.Status = "Completed";
            result.Success = true;
            return result;
        }

        // Signal handler for doctor approval
        [WorkflowSignal]
        public async Task ApproveAsync()
        {
            approvalReceived = true;
        }

        // Signal handler for doctor denial
        [WorkflowSignal]
        public async Task DenyAsync()
        {
            approvalDenied = true;
        }

        // Query handler to check current status
        [WorkflowQuery]
        public string GetStatus()
        {
            if (approvalDenied) return "Denied";
            if (approvalReceived) return "Approved";
            return "Processing";
        }
    }
}