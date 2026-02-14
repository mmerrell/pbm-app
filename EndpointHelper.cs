using Microsoft.AspNetCore.SignalR;

namespace PBMAdjudicationService
{
    // ============================================================================
    // HELPER CLASS
    // ============================================================================

    public static class EndpointHelper
    {
        public static async Task<bool> SimulateEndpointBehavior(string endpointName, IHubContext<NotificationHub> hubContext)
        {
            var config = DataStore.EndpointConfigs[endpointName];

            if (config.CompleteOutage)
            {
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"❌ [{endpointName}] Complete outage - service unavailable");
                throw new Exception($"{endpointName} service is down");
            }

            if (config.LatencyMs > 0)
            {
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"⏱️ [{endpointName}] Simulating {config.LatencyMs}ms latency");
                await Task.Delay(config.LatencyMs);
            }

            if (config.FailureRatePercent > 0)
            {
                var random = Random.Shared.Next(100);
                if (random < config.FailureRatePercent)
                {
                    await hubContext.Clients.All.SendAsync("ReceiveLog", $"❌ [{endpointName}] Random failure ({config.FailureRatePercent}% rate)");
                    throw new Exception($"{endpointName} failed randomly");
                }
            }

            return true;
        }

        public static async Task SendNotification(string recipient, string recipientName, string message, IHubContext<NotificationHub> hubContext)
        {
            try
            {
                await SimulateEndpointBehavior("notify", hubContext);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📧 [notify] Sent to {recipient} ({recipientName}): {message}");
            }
            catch (Exception ex)
            {
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"❌ [notify] Failed to send notification: {ex.Message}");
            }
        }
    }

}
