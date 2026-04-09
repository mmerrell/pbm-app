namespace PBMAdjudication.Core
{
    // ============================================================================
    // DATA MODELS
    // ============================================================================

    public class Prescription
    {
        public string? ApprovalNeededReason { get; set; }
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PatientId { get; set; } = "";
        public string PatientName { get; set; } = "";
        public string Medication { get; set; } = "";
        public DateTime EligibleDate { get; set; }
        public int RefillsRemaining { get; set; }
        public string Status { get; set; } = "Pending";
        public decimal? Copay { get; set; }
        public DateTime RequestedDate { get; set; } = DateTime.UtcNow;
        public int? FailedStep { get; set; }
        public string? NotificationStatus { get; set; } // null, "Retrying", "Sent", "Failed"
    }

    public class DoctorApprovalRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PrescriptionId { get; set; } = "";
        public string PatientName { get; set; } = "";
        public string Medication { get; set; } = "";
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
        public int ReminderCount { get; set; } = 0;
        public bool IsApproved { get; set; } = false;
        public bool IsDenied { get; set; } = false;
    }

    // Specialty prior authorization request for GLP-1 medications (v2+).
    // Distinct from DoctorApprovalRequest — different regulatory track,
    // different workflow signal, visually distinct card in the UI.
    public class SpecialtyApprovalRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PrescriptionId { get; set; } = "";
        public string PatientName { get; set; } = "";
        public string Medication { get; set; } = "";
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
        public bool IsApproved { get; set; } = false;
        public bool IsDenied { get; set; } = false;
        public bool IsTimedOut { get; set; } = false;
    }

    public class EndpointConfig
    {
        public int FailureRatePercent { get; set; } = 0;
        public int LatencyMs { get; set; } = 0;
        public bool CompleteOutage { get; set; } = false;
    }

}
