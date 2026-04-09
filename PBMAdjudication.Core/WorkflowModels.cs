namespace PBMAdjudication.Core
{
    public class PrescriptionInput
    {
        public string PrescriptionId { get; set; } = "";
        public string PatientName { get; set; } = "";
        public string Medication { get; set; } = "";
        public DateTime EligibleDate { get; set; }
        public int RefillsRemaining { get; set; }
        public bool IsGlp1 { get; set; } = false;
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

    // Result returned by each child workflow in the GLP-1 split path (v2+)
    public class AdjudicationChildResult
    {
        public bool Success { get; set; }
        public decimal Copay { get; set; }
        public string Track { get; set; } = ""; // "standard" or "glp1"
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
