using Npgsql;
using Dapper;

namespace PBMAdjudication.Core
{
    public class PostgresPrescriptionRepository : IPrescriptionRepository
    {
        private readonly string _connectionString;

        public PostgresPrescriptionRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        private NpgsqlConnection CreateConnection() => new NpgsqlConnection(_connectionString);

        // ============================================================================
        // PRESCRIPTIONS
        // ============================================================================

        public async Task<IEnumerable<Prescription>> GetAllPrescriptionsAsync()
        {
            using var conn = CreateConnection();
            return await conn.QueryAsync<Prescription>(@"
                SELECT 
                    id AS Id, patient_id AS PatientId, patient_name AS PatientName,
                    medication AS Medication, eligible_date AS EligibleDate,
                    refills_remaining AS RefillsRemaining, status AS Status,
                    copay AS Copay, requested_date AS RequestedDate,
                    failed_step AS FailedStep, notification_status AS NotificationStatus,
                    approval_needed_reason AS ApprovalNeededReason
                FROM prescriptions
                ORDER BY requested_date DESC");
        }

        public async Task<Prescription?> GetPrescriptionAsync(string id)
        {
            using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<Prescription>(@"
                SELECT 
                    id AS Id, patient_id AS PatientId, patient_name AS PatientName,
                    medication AS Medication, eligible_date AS EligibleDate,
                    refills_remaining AS RefillsRemaining, status AS Status,
                    copay AS Copay, requested_date AS RequestedDate,
                    failed_step AS FailedStep, notification_status AS NotificationStatus,
                    approval_needed_reason AS ApprovalNeededReason
                FROM prescriptions
                WHERE id = @id",
                new { id });
        }

        public async Task<IEnumerable<Prescription>> GetPendingPrescriptionsAsync()
        {
            using var conn = CreateConnection();
            return await conn.QueryAsync<Prescription>(@"
                SELECT 
                    id AS Id, patient_id AS PatientId, patient_name AS PatientName,
                    medication AS Medication, eligible_date AS EligibleDate,
                    refills_remaining AS RefillsRemaining, status AS Status,
                    copay AS Copay, requested_date AS RequestedDate,
                    failed_step AS FailedStep, notification_status AS NotificationStatus,
                    approval_needed_reason AS ApprovalNeededReason
                FROM prescriptions
                WHERE status = 'Pending'
                ORDER BY requested_date DESC");
        }

        public async Task UpsertPrescriptionAsync(Prescription prescription)
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO prescriptions 
                    (id, patient_id, patient_name, medication, eligible_date,
                     refills_remaining, status, copay, requested_date,
                     failed_step, notification_status, approval_needed_reason)
                VALUES 
                    (@Id, @PatientId, @PatientName, @Medication, @EligibleDate,
                     @RefillsRemaining, @Status, @Copay, @RequestedDate,
                     @FailedStep, @NotificationStatus, @ApprovalNeededReason)
                ON CONFLICT (id) DO UPDATE SET
                    patient_id = EXCLUDED.patient_id,
                    patient_name = EXCLUDED.patient_name,
                    medication = EXCLUDED.medication,
                    eligible_date = EXCLUDED.eligible_date,
                    refills_remaining = EXCLUDED.refills_remaining,
                    status = EXCLUDED.status,
                    copay = EXCLUDED.copay,
                    requested_date = EXCLUDED.requested_date,
                    failed_step = EXCLUDED.failed_step,
                    notification_status = EXCLUDED.notification_status,
                    approval_needed_reason = EXCLUDED.approval_needed_reason",
                prescription);
        }

        // ============================================================================
        // APPROVAL REQUESTS
        // ============================================================================

        public async Task<DoctorApprovalRequest?> GetApprovalRequestAsync(string id)
        {
            using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<DoctorApprovalRequest>(@"
                SELECT 
                    id AS Id, prescription_id AS PrescriptionId,
                    patient_name AS PatientName, medication AS Medication,
                    requested_at AS RequestedAt, reminder_count AS ReminderCount,
                    is_approved AS IsApproved, is_denied AS IsDenied
                FROM doctor_approval_requests
                WHERE id = @id",
                new { id });
        }

        public async Task<DoctorApprovalRequest?> GetApprovalRequestByPrescriptionAsync(string prescriptionId)
        {
            using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<DoctorApprovalRequest>(@"
                SELECT 
                    id AS Id, prescription_id AS PrescriptionId,
                    patient_name AS PatientName, medication AS Medication,
                    requested_at AS RequestedAt, reminder_count AS ReminderCount,
                    is_approved AS IsApproved, is_denied AS IsDenied
                FROM doctor_approval_requests
                WHERE prescription_id = @prescriptionId
                ORDER BY requested_at DESC
                LIMIT 1",
                new { prescriptionId });
        }

        public async Task<IEnumerable<DoctorApprovalRequest>> GetPendingApprovalRequestsAsync()
        {
            using var conn = CreateConnection();
            return await conn.QueryAsync<DoctorApprovalRequest>(@"
                SELECT 
                    id AS Id, prescription_id AS PrescriptionId,
                    patient_name AS PatientName, medication AS Medication,
                    requested_at AS RequestedAt, reminder_count AS ReminderCount,
                    is_approved AS IsApproved, is_denied AS IsDenied
                FROM doctor_approval_requests
                WHERE is_approved = FALSE AND is_denied = FALSE
                ORDER BY requested_at DESC");
        }

        public async Task UpsertApprovalRequestAsync(DoctorApprovalRequest request)
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO doctor_approval_requests
                    (id, prescription_id, patient_name, medication,
                     requested_at, reminder_count, is_approved, is_denied)
                VALUES
                    (@Id, @PrescriptionId, @PatientName, @Medication,
                     @RequestedAt, @ReminderCount, @IsApproved, @IsDenied)
                ON CONFLICT (id) DO UPDATE SET
                    reminder_count = EXCLUDED.reminder_count,
                    is_approved = EXCLUDED.is_approved,
                    is_denied = EXCLUDED.is_denied",
                request);
        }

        // ============================================================================
        // SPECIALTY APPROVAL REQUESTS (GLP-1, v2+)
        // ============================================================================

        public async Task<SpecialtyApprovalRequest?> GetSpecialtyApprovalRequestAsync(string id)
        {
            using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<SpecialtyApprovalRequest>(@"
                SELECT
                    id AS Id, prescription_id AS PrescriptionId,
                    patient_name AS PatientName, medication AS Medication,
                    requested_at AS RequestedAt,
                    is_approved AS IsApproved, is_denied AS IsDenied, is_timed_out AS IsTimedOut
                FROM specialty_approval_requests
                WHERE id = @id",
                new { id });
        }

        public async Task<SpecialtyApprovalRequest?> GetSpecialtyApprovalRequestByPrescriptionAsync(string prescriptionId)
        {
            using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<SpecialtyApprovalRequest>(@"
                SELECT
                    id AS Id, prescription_id AS PrescriptionId,
                    patient_name AS PatientName, medication AS Medication,
                    requested_at AS RequestedAt,
                    is_approved AS IsApproved, is_denied AS IsDenied, is_timed_out AS IsTimedOut
                FROM specialty_approval_requests
                WHERE prescription_id = @prescriptionId
                ORDER BY requested_at DESC
                LIMIT 1",
                new { prescriptionId });
        }

        public async Task<IEnumerable<SpecialtyApprovalRequest>> GetPendingSpecialtyApprovalRequestsAsync()
        {
            using var conn = CreateConnection();
            return await conn.QueryAsync<SpecialtyApprovalRequest>(@"
                SELECT
                    id AS Id, prescription_id AS PrescriptionId,
                    patient_name AS PatientName, medication AS Medication,
                    requested_at AS RequestedAt,
                    is_approved AS IsApproved, is_denied AS IsDenied, is_timed_out AS IsTimedOut
                FROM specialty_approval_requests
                WHERE is_approved = FALSE AND is_denied = FALSE AND is_timed_out = FALSE
                ORDER BY requested_at DESC");
        }

        public async Task UpsertSpecialtyApprovalRequestAsync(SpecialtyApprovalRequest request)
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO specialty_approval_requests
                    (id, prescription_id, patient_name, medication,
                     requested_at, is_approved, is_denied, is_timed_out)
                VALUES
                    (@Id, @PrescriptionId, @PatientName, @Medication,
                     @RequestedAt, @IsApproved, @IsDenied, @IsTimedOut)
                ON CONFLICT (id) DO UPDATE SET
                    is_approved = EXCLUDED.is_approved,
                    is_denied = EXCLUDED.is_denied,
                    is_timed_out = EXCLUDED.is_timed_out",
                request);
        }

        // ============================================================================
        // ADMIN
        // ============================================================================

        public async Task ResetAsync()
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync("DELETE FROM specialty_approval_requests");
            await conn.ExecuteAsync("DELETE FROM doctor_approval_requests");
            await conn.ExecuteAsync("DELETE FROM prescriptions");

            var initializer = new DatabaseInitializer(_connectionString);
            await initializer.InitializeAsync();
        }
    }
}
