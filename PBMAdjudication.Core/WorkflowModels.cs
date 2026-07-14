namespace PBMAdjudication.Core
{
    public class PrescriptionInput
    {
        public string TransferId { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string RecipientName { get; set; } = "";
        public decimal Amount { get; set; }
        public string CurrencyCorridor { get; set; } = "";
        public DateTime FundsAvailableDate { get; set; }
        public int PriorCleanTransfers { get; set; }
        public bool IsHighRisk { get; set; } = false;
    }

    public class ValidationResult
    {
        public bool Eligible { get; set; }
        public DateTime? EligibleDate { get; set; }
    }

    public class AuthorizationResult
    {
        public bool Authorized { get; set; }
    }

    public class AdjudicationResult
    {
        public decimal Copay { get; set; }
    }

    public class ApprovalResult
    {
        public bool ApprovalNeeded { get; set; }
        public string? ApprovalId { get; set; }
    }

    // Result returned by each child workflow in the EDD split path (v2+)
    public class AdjudicationChildResult
    {
        public bool Success { get; set; }
        public decimal Copay { get; set; }
        public string Track { get; set; } = ""; // "standard" or "edd"
    }

    public class SubmissionResult
    {
        public bool Submitted { get; set; }
    }

    public class WorkflowResult
    {
        public bool Success { get; set; }
        public string Status { get; set; } = "";
        public decimal? Copay { get; set; }
    }
}
