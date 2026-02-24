using Microsoft.AspNetCore.SignalR;
using PBMAdjudication.Core;

namespace PBMAdjudicationService
{
    // ============================================================================
    // SIGNALR HUB
    // ============================================================================

    public class NotificationHub : Hub
    {
        public async Task SendLog(string message)
        {
            await Clients.All.SendAsync("ReceiveLog", message);
        }

        public async Task UpdatePrescription(Prescription prescription)
        {
            await Clients.All.SendAsync("PrescriptionUpdated", prescription);
        }

        public async Task UpdateApprovalRequest(DoctorApprovalRequest request)
        {
            await Clients.All.SendAsync("ApprovalRequestUpdated", request);
        }
    }

}
