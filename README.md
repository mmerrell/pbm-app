# PBM Adjudication Service — Temporal Branch

> This is the **durable execution** implementation. The workflow is orchestrated by Temporal, giving you automatic retries, resumability, and durable timers — with no changes to the business logic.
>
> For full documentation, architecture overview, and demo walkthrough, see the [main branch README](https://github.com/mmerrell/PBMAdjudicationService).

## Running

Three processes are required. Start them in order:

**1. Temporal Server**
```bash
temporal server start-dev
```

**2. Web API**
```bash
cd PBMAdjudicationService
dotnet run
```

**3. Temporal Worker** (separate terminal)
```bash
cd PBMAdjudicationService/PBMAdjudicationService.Worker
dotnet run
```

Navigate to `http://localhost:5002`

Temporal UI is available at `http://localhost:8233`

> **Development note:** The worker must be restarted after rebuilding the main project. Stop both processes, build, then restart in order.

## What to Look For

**Automatic retries** — set any service to a failure rate and watch the workflow retry transparently. The pip stays in the in-progress state, the system logs show each retry attempt.

**Complete outage** — set a service to Complete Outage. The workflow retries up to the configured maximum, then marks the prescription OnHold. Turn the outage off and reprocess — the workflow picks up from the failed step.

**Kill the worker** — stop the worker process mid-flight while prescriptions are processing. Restart it and watch in-flight workflows resume exactly where they left off. This is impossible in the pre-temporal branch.

**Doctor approval** — submit an Rx with 0 refills. The workflow durably waits for the doctor's signal. Approve or deny from the Doctor Portal. The workflow resumes immediately via a Temporal signal.

**Notification retry** — set Notifications to a high failure rate. The amber banner appears on the card while retries are in flight. It updates to green on success or red on permanent failure — all without blocking the main workflow.

**Temporal UI** — browse to `http://localhost:8233` during any of the above to see workflow execution history, activity retries, and pending timers in real time.

## Architecture

```
Browser ──► ASP.NET Core API ──► Temporal Server ──► Worker
                                                      ├── PrescriptionWorkflow
                                                      └── PrescriptionActivities
                                                            ├── ValidateEligibilityAsync
                                                            ├── CheckPriorAuthorizationAsync
                                                            ├── AdjudicateClaimAsync
                                                            ├── RequestDoctorApprovalAsync
                                                            ├── SubmitToPharmacyAsync
                                                            ├── SendNotificationAsync
                                                            ├── MarkOnHoldAsync
                                                            └── MarkNotificationFailedAsync
```

The API starts workflows and sends signals. The worker executes them. SignalR pushes status updates to the browser as each activity completes.