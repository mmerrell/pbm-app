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
                    recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication, eligible_date AS EligibleDate,
                    refills_remaining AS RefillsRemaining, is_high_risk AS IsHighRisk, status AS Status,
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
                    recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication, eligible_date AS EligibleDate,
                    refills_remaining AS RefillsRemaining, is_high_risk AS IsHighRisk, status AS Status,
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
                    recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication, eligible_date AS EligibleDate,
                    refills_remaining AS RefillsRemaining, is_high_risk AS IsHighRisk, status AS Status,
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
                    (id, patient_id, patient_name, recipient_name, amount, medication, eligible_date,
                     refills_remaining, is_high_risk, status, copay, requested_date,
                     failed_step, notification_status, approval_needed_reason)
                VALUES
                    (@Id, @PatientId, @PatientName, @RecipientName, @Amount, @Medication, @EligibleDate,
                     @RefillsRemaining, @IsHighRisk, @Status, @Copay, @RequestedDate,
                     @FailedStep, @NotificationStatus, @ApprovalNeededReason)
                ON CONFLICT (id) DO UPDATE SET
                    patient_id = EXCLUDED.patient_id,
                    patient_name = EXCLUDED.patient_name,
                    recipient_name = EXCLUDED.recipient_name,
                    amount = EXCLUDED.amount,
                    medication = EXCLUDED.medication,
                    eligible_date = EXCLUDED.eligible_date,
                    refills_remaining = EXCLUDED.refills_remaining,
                    is_high_risk = EXCLUDED.is_high_risk,
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
                    patient_name AS PatientName, recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication,
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
                    patient_name AS PatientName, recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication,
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
                    patient_name AS PatientName, recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication,
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
                    (id, prescription_id, patient_name, recipient_name, amount, medication,
                     requested_at, reminder_count, is_approved, is_denied)
                VALUES
                    (@Id, @PrescriptionId, @PatientName, @RecipientName, @Amount, @Medication,
                     @RequestedAt, @ReminderCount, @IsApproved, @IsDenied)
                ON CONFLICT (id) DO UPDATE SET
                    reminder_count = EXCLUDED.reminder_count,
                    is_approved = EXCLUDED.is_approved,
                    is_denied = EXCLUDED.is_denied",
                request);
        }

        // ============================================================================
        // SPECIALTY APPROVAL REQUESTS (EDD, v2+)
        // ============================================================================

        public async Task<SpecialtyApprovalRequest?> GetSpecialtyApprovalRequestAsync(string id)
        {
            using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<SpecialtyApprovalRequest>(@"
                SELECT
                    id AS Id, prescription_id AS PrescriptionId,
                    patient_name AS PatientName, recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication,
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
                    patient_name AS PatientName, recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication,
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
                    patient_name AS PatientName, recipient_name AS RecipientName, amount AS Amount,
                    medication AS Medication,
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
                    (id, prescription_id, patient_name, recipient_name, amount, medication,
                     requested_at, is_approved, is_denied, is_timed_out)
                VALUES
                    (@Id, @PrescriptionId, @PatientName, @RecipientName, @Amount, @Medication,
                     @RequestedAt, @IsApproved, @IsDenied, @IsTimedOut)
                ON CONFLICT (id) DO UPDATE SET
                    is_approved = EXCLUDED.is_approved,
                    is_denied = EXCLUDED.is_denied,
                    is_timed_out = EXCLUDED.is_timed_out",
                request);
        }

        // ============================================================================
        // ENDPOINT CONFIGS
        // ============================================================================

        public async Task<EndpointConfig> GetEndpointConfigAsync(string endpoint)
        {
            using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<EndpointConfig>(@"
                SELECT 
                    failure_rate_percent AS FailureRatePercent,
                    latency_ms AS LatencyMs,
                    complete_outage AS CompleteOutage
                FROM endpoint_configs
                WHERE endpoint = @endpoint",
                new { endpoint }) ?? new EndpointConfig();
        }

        public async Task<Dictionary<string, EndpointConfig>> GetAllEndpointConfigsAsync()
        {
            using var conn = CreateConnection();
            var rows = await conn.QueryAsync(@"
        SELECT 
            endpoint,
            failure_rate_percent AS FailureRatePercent,
            latency_ms AS LatencyMs,
            complete_outage AS CompleteOutage
        FROM endpoint_configs");

            return rows.ToDictionary(
                row => (string)row.endpoint,
                row => new EndpointConfig
                {
                    FailureRatePercent = row.FailureRatePercent == null ? 0 : (int)row.FailureRatePercent,
                    LatencyMs = row.LatencyMs == null ? 0 : (int)row.LatencyMs,
                    CompleteOutage = row.CompleteOutage == null ? false : (bool)row.CompleteOutage
                });
        }

        public async Task UpsertEndpointConfigAsync(string endpoint, EndpointConfig config)
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO endpoint_configs (endpoint, failure_rate_percent, latency_ms, complete_outage)
                VALUES (@endpoint, @FailureRatePercent, @LatencyMs, @CompleteOutage)
                ON CONFLICT (endpoint) DO UPDATE SET
                    failure_rate_percent = EXCLUDED.failure_rate_percent,
                    latency_ms = EXCLUDED.latency_ms,
                    complete_outage = EXCLUDED.complete_outage",
                new { endpoint, config.FailureRatePercent, config.LatencyMs, config.CompleteOutage });
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
            await conn.ExecuteAsync("DELETE FROM endpoint_configs");

            var initializer = new DatabaseInitializer(_connectionString);
            await initializer.InitializeAsync();
        }
    }
}