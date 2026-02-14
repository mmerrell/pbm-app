# PBM Adjudication Service — Pre-Temporal Branch

> This is the **ad-hoc** implementation. The browser orchestrates the workflow by calling each step sequentially. There is no durability, no automatic retry, and no resumability.
>
> For full documentation, architecture overview, and demo walkthrough, see the [main branch README](https://github.com/mmerrell/PBMAdjudicationService).

## Running

```bash
cd PBMAdjudicationService
dotnet run
```

Navigate to `http://localhost:5001`

No additional processes required.

## What to Look For

**The happy path** works fine — prescriptions flow through all five steps when services are healthy.

**Introduce failure** using the Control Panel sliders and toggles to see where this approach breaks down:

- Set any service to a high failure rate — the workflow stops at that step with an orange pip and requires manual reprocessing
- Set a service to Complete Outage — same result, no automatic recovery
- Kill the browser tab mid-workflow — state is lost, the workflow is gone
- The notification service failure is cosmetic only — there is no retry, just a warning banner

**Doctor approval** pauses the workflow correctly, but the pause is held in browser memory. A page refresh loses the pending state.

## What's Different From the Temporal Branch

| Concern | Pre-Temporal | Temporal |
|---------|-------------|----------|
| Orchestration | Client (browser) | Server (Temporal workflow) |
| Retries | Manual — click "Reprocess" | Automatic with configurable backoff |
| Resumability | None — refresh loses state | Full — worker restart resumes in-flight workflows |
| Doctor approval wait | Browser memory | Durable timer — can wait days |
| Notification failure | Warning banner only | Retried automatically, UI reflects retry state |
| Observability | Browser console | Temporal UI at `http://localhost:8233` |