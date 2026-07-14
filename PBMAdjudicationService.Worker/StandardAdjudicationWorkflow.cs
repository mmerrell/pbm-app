using Temporalio.Common;
using Temporalio.Workflows;
using PBMAdjudication.Core;

namespace PBMAdjudication.Worker
{
    /// <summary>
    /// Child workflow (v2+): handles the standard line items when a transfer
    /// is split at adjudication. Runs the standard adjudication and settlement
    /// path — short-lived, no HITL.
    /// </summary>
    [Workflow]
    public class StandardAdjudicationWorkflow
    {
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

        [WorkflowRun]
        public async Task<AdjudicationChildResult> RunAsync(string prescriptionId)
        {
            var result = new AdjudicationChildResult { Track = "standard" };

            var adjudication = await Workflow.ExecuteActivityAsync(
                (PrescriptionActivities a) => a.CalculateFxFeesAsync(prescriptionId),
                DefaultOptions);

            result.Copay = adjudication.Copay;
            result.Success = true;
            return result;
        }
    }
}
