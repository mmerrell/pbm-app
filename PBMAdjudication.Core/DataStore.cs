using System.Collections.Concurrent;

namespace PBMAdjudication.Core
{
    // ============================================================================
    // IN-MEMORY DATA STORAGE
    // ============================================================================

    public static class DataStore
    {
        public static ConcurrentDictionary<string, Prescription> Prescriptions { get; } = new();
        public static ConcurrentDictionary<string, DoctorApprovalRequest> ApprovalRequests { get; } = new();

        public static Dictionary<string, EndpointConfig> EndpointConfigs { get; } = new()
        {
            ["validate"] = new EndpointConfig(),
            ["authorize"] = new EndpointConfig(),
            ["adjudicate"] = new EndpointConfig(),
            ["notify"] = new EndpointConfig(),
            ["submit"] = new EndpointConfig()
        };

        static DataStore()
        {
            var prescriptions = new[]
            {
                new Prescription
                {
                    PatientId = "P001",
                    PatientName = "Michael Davis",
                    Medication = "Omeprazole 20mg",
                    EligibleDate = DateTime.UtcNow.AddMinutes(2),
                    RefillsRemaining = 5,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P002",
                    PatientName = "John Smith",
                    Medication = "Lipitor 20mg",
                    EligibleDate = DateTime.UtcNow.AddDays(-1),
                    RefillsRemaining = 3,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P003",
                    PatientName = "Mary Johnson",
                    Medication = "Metformin 500mg",
                    EligibleDate = DateTime.UtcNow.AddMinutes(20),
                    RefillsRemaining = 2,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P004",
                    PatientName = "Robert Williams",
                    Medication = "Lisinopril 10mg",
                    EligibleDate = DateTime.UtcNow.AddDays(-5),
                    RefillsRemaining = 0,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P005",
                    PatientName = "Patricia Brown",
                    Medication = "Atorvastatin 40mg",
                    EligibleDate = DateTime.UtcNow.AddDays(-3),
                    RefillsRemaining = 1,
                    Status = "Pending"
                },
            };

            foreach (var rx in prescriptions)
            {
                Prescriptions[rx.Id] = rx;
            }
        }
    }
}
