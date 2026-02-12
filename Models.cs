using System;
using System.Collections.Generic;
using System.Text;

namespace PBMAdjudicationService
{
    // ============================================================================
    // DATA MODELS
    // ============================================================================

    public class Prescription
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PatientId { get; set; } = "";
        public string PatientName { get; set; } = "";
        public string Medication { get; set; } = "";
        public DateTime EligibleDate { get; set; }
        public string? ApprovalNeededReason { get; set; }
        public int RefillsRemaining { get; set; }
        public string Status { get; set; } = "Pending";
        public decimal? Copay { get; set; }
        public DateTime RequestedDate { get; set; } = DateTime.UtcNow;
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

    public class EndpointConfig
    {
        public int FailureRatePercent { get; set; } = 0;
        public int LatencyMs { get; set; } = 0;
        public bool CompleteOutage { get; set; } = false;
    }

}
