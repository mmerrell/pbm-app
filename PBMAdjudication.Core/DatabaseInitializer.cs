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
                    recipient_name TEXT NOT NULL DEFAULT '',
                    amount NUMERIC(14,2) NOT NULL DEFAULT 0,
                    medication TEXT NOT NULL,
                    eligible_date TIMESTAMPTZ NOT NULL,
                    refills_remaining INT NOT NULL,
                    is_high_risk BOOLEAN NOT NULL DEFAULT FALSE,
                    status TEXT NOT NULL,
                    copay NUMERIC(10,2),
                    requested_date TIMESTAMPTZ NOT NULL,
                    failed_step INT,
                    notification_status TEXT,
                    approval_needed_reason TEXT
                );

                CREATE TABLE IF NOT EXISTS doctor_approval_requests (
                    id TEXT PRIMARY KEY,
                    prescription_id TEXT NOT NULL,
                    patient_name TEXT NOT NULL,
                    recipient_name TEXT NOT NULL DEFAULT '',
                    amount NUMERIC(14,2) NOT NULL DEFAULT 0,
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
                    recipient_name TEXT NOT NULL DEFAULT '',
                    amount NUMERIC(14,2) NOT NULL DEFAULT 0,
                    medication TEXT NOT NULL,
                    requested_at TIMESTAMPTZ NOT NULL,
                    is_approved BOOLEAN NOT NULL DEFAULT FALSE,
                    is_denied BOOLEAN NOT NULL DEFAULT FALSE,
                    is_timed_out BOOLEAN NOT NULL DEFAULT FALSE
                );

                CREATE TABLE IF NOT EXISTS endpoint_configs (
                    endpoint TEXT PRIMARY KEY,
                    failure_rate_percent INT NOT NULL DEFAULT 0,
                    latency_ms INT NOT NULL DEFAULT 0,
                    complete_outage BOOLEAN NOT NULL DEFAULT FALSE
                );

                -- Lightweight migrations for tables that pre-date these columns
                ALTER TABLE prescriptions ADD COLUMN IF NOT EXISTS recipient_name TEXT NOT NULL DEFAULT '';
                ALTER TABLE prescriptions ADD COLUMN IF NOT EXISTS amount NUMERIC(14,2) NOT NULL DEFAULT 0;
                ALTER TABLE prescriptions ADD COLUMN IF NOT EXISTS is_high_risk BOOLEAN NOT NULL DEFAULT FALSE;
                ALTER TABLE doctor_approval_requests ADD COLUMN IF NOT EXISTS recipient_name TEXT NOT NULL DEFAULT '';
                ALTER TABLE doctor_approval_requests ADD COLUMN IF NOT EXISTS amount NUMERIC(14,2) NOT NULL DEFAULT 0;
                ALTER TABLE specialty_approval_requests ADD COLUMN IF NOT EXISTS recipient_name TEXT NOT NULL DEFAULT '';
                ALTER TABLE specialty_approval_requests ADD COLUMN IF NOT EXISTS amount NUMERIC(14,2) NOT NULL DEFAULT 0;
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
                    RecipientName = "Emma Wilson",
                    Amount = 2500.00m,
                    Medication = "USD → EUR",
                    EligibleDate = DateTime.UtcNow.AddMinutes(2),
                    RefillsRemaining = 5,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P002",
                    PatientName = "John Smith",
                    RecipientName = "Oliver Bennett",
                    Amount = 850.00m,
                    Medication = "USD → GBP",
                    EligibleDate = DateTime.UtcNow.AddDays(-1),
                    RefillsRemaining = 3,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P003",
                    PatientName = "Mary Johnson",
                    RecipientName = "Wei Tan",
                    Amount = 12000.00m,
                    Medication = "USD → SGD",
                    EligibleDate = DateTime.UtcNow.AddMinutes(20),
                    RefillsRemaining = 2,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P004",
                    PatientName = "Robert Williams",
                    RecipientName = "Sarah Chen",
                    Amount = 4200.00m,
                    Medication = "USD → CAD",
                    EligibleDate = DateTime.UtcNow.AddDays(-5),
                    RefillsRemaining = 0,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P005",
                    PatientName = "Patricia Brown",
                    RecipientName = "Jack Osei",
                    Amount = 1750.00m,
                    Medication = "USD → AUD",
                    EligibleDate = DateTime.UtcNow.AddDays(-3),
                    RefillsRemaining = 1,
                    Status = "Pending"
                },
                new Prescription
                {
                    PatientId = "P006",
                    PatientName = "Linda Martinez",
                    RecipientName = "Priya Nair",
                    Amount = 78000.00m,
                    Medication = "USD → INR",
                    EligibleDate = DateTime.UtcNow.AddDays(-1),
                    RefillsRemaining = 0,
                    IsHighRisk = true,
                    Status = "Pending"
                },
            };

            foreach (var rx in prescriptions)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO prescriptions
                        (id, patient_id, patient_name, recipient_name, amount, medication, eligible_date,
                         refills_remaining, is_high_risk, status, copay, requested_date,
                         failed_step, notification_status, approval_needed_reason)
                    VALUES
                        (@Id, @PatientId, @PatientName, @RecipientName, @Amount, @Medication, @EligibleDate,
                         @RefillsRemaining, @IsHighRisk, @Status, @Copay, @RequestedDate,
                         @FailedStep, @NotificationStatus, @ApprovalNeededReason)
                    ON CONFLICT (id) DO NOTHING",
                    rx);
            }

            // Seed default endpoint configs
            var endpoints = new[] { "validate", "authorize", "adjudicate", "adjudicate-edd", "notify", "submit", "submit-specialty" };
            foreach (var endpoint in endpoints)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO endpoint_configs (endpoint, failure_rate_percent, latency_ms, complete_outage)
                    VALUES (@endpoint, 0, 0, FALSE)
                    ON CONFLICT (endpoint) DO NOTHING",
                    new { endpoint });
            }
        }
    }
}