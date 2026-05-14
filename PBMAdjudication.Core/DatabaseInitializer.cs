using Npgsql;
using Dapper;

namespace PBMAdjudication.Core
{
    public class DatabaseInitializer
    {
        private readonly string _connectionString;

        public DatabaseInitializer(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task InitializeAsync()
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS prescriptions (
                    id TEXT PRIMARY KEY,
                    patient_id TEXT NOT NULL,
                    patient_name TEXT NOT NULL,
                    medication TEXT NOT NULL,
                    eligible_date TIMESTAMPTZ NOT NULL,
                    refills_remaining INT NOT NULL,
                    status TEXT NOT NULL,
                    copay NUMERIC(10,2),
                    requested_date TIMESTAMPTZ NOT NULL,
                    failed_step INT,
                    notification_status TEXT,
                    approval_needed_reason TEXT,
                    activity_retry_status TEXT,
                    activity_retry_step TEXT
                );

                CREATE TABLE IF NOT EXISTS doctor_approval_requests (
                    id TEXT PRIMARY KEY,
                    prescription_id TEXT NOT NULL,
                    patient_name TEXT NOT NULL,
                    medication TEXT NOT NULL,
                    requested_at TIMESTAMPTZ NOT NULL,
                    reminder_count INT NOT NULL DEFAULT 0,
                    is_approved BOOLEAN NOT NULL DEFAULT FALSE,
                    is_denied BOOLEAN NOT NULL DEFAULT FALSE
                );

                CREATE TABLE IF NOT EXISTS specialty_approval_requests (
                    id TEXT PRIMARY KEY,
                    prescription_id TEXT NOT NULL,
                    patient_name TEXT NOT NULL,
                    medication TEXT NOT NULL,
                    requested_at TIMESTAMPTZ NOT NULL,
                    is_approved BOOLEAN NOT NULL DEFAULT FALSE,
                    is_denied BOOLEAN NOT NULL DEFAULT FALSE,
                    is_timed_out BOOLEAN NOT NULL DEFAULT FALSE
                );
            ");

            // Add columns to existing tables if they don't exist yet (safe migration).
            await conn.ExecuteAsync(@"
                ALTER TABLE prescriptions ADD COLUMN IF NOT EXISTS activity_retry_status TEXT;
                ALTER TABLE prescriptions ADD COLUMN IF NOT EXISTS activity_retry_step TEXT;
            ");

            await SeedDefaultDataAsync(conn);
        }

        private async Task SeedDefaultDataAsync(NpgsqlConnection conn)
        {
            // Only seed if empty
            var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM prescriptions");
            if (count > 0) return;

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
                new Prescription
                {
                    PatientId = "P006",
                    PatientName = "Linda Martinez",
                    Medication = "Ozempic 0.5mg (semaglutide)",
                    EligibleDate = DateTime.UtcNow.AddDays(-1),
                    RefillsRemaining = 0,
                    Status = "Pending"
                },
            };

            foreach (var rx in prescriptions)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO prescriptions 
                        (id, patient_id, patient_name, medication, eligible_date, 
                         refills_remaining, status, copay, requested_date, 
                         failed_step, notification_status, approval_needed_reason,
                         activity_retry_status, activity_retry_step)
                    VALUES 
                        (@Id, @PatientId, @PatientName, @Medication, @EligibleDate,
                         @RefillsRemaining, @Status, @Copay, @RequestedDate,
                         @FailedStep, @NotificationStatus, @ApprovalNeededReason,
                         @ActivityRetryStatus, @ActivityRetryStep)
                    ON CONFLICT (id) DO NOTHING",
                    rx);
            }
        }
    }
}
