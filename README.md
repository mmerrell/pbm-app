# PBM Adjudication Service — Temporal Branch

> This is the **durable execution** implementation. The workflow is orchestrated by Temporal, giving you automatic retries, resumability, durable timers, payload codecs, and worker versioning — with no changes to business logic.

## Quick Start (Docker)

Docker Compose is the recommended way to run this branch. It starts Postgres, Temporal Server, the Temporal UI, the API, and the Worker together.

**1. Create your `.env` file**

The encryption codec requires a 256-bit base64 key. Generate one and save it:

```bash
# Option A: use the example key (fine for local demo)
cp .env.example .env

# Option B: generate a fresh key in .NET
dotnet script -e 'Console.WriteLine(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));'
# or in a quick C# REPL / scratch file — paste the output into .env as:
# TEMPORAL_ENCRYPTION_KEY=<your-base64-key>
```

> Both the API and Worker must use the same key. The `.env` file is gitignored — never commit it.

**2. Start the stack**

```bash
# Standard mode (codec demo — encryption + claim check)
docker compose --profile default up --build

# Versioning mode (GLP-1 worker versioning demo)
docker compose --profile versioning up --build
```

Navigate to `http://localhost:5002` — Temporal UI at `http://localhost:8233`

---

## Local Development (dotnet run)

Three processes required, started in order:

```bash
# 1. Temporal Server
temporal server start-dev

# 2. Web API
dotnet run --project PBMAdjudicationService.csproj

# 3. Worker (separate terminal)
cd PBMAdjudicationService.Worker
dotnet run
```

Add your encryption key to `appsettings.Local.json` (gitignored) in both the API root and the Worker directory:

```json
{
  "Temporal": {
    "EncryptionKeyBase64": "<your-base64-key>"
  }
}
```

---

## Demo Walkthroughs

### Retries and Resilience

**Automatic retries** — set any service to a failure rate and watch the workflow retry transparently. The pip stays in-progress; logs show each attempt.

**Complete outage** — set a service to Complete Outage. The workflow retries to the configured maximum, marks the prescription OnHold. Turn the outage off and reprocess — the workflow resumes from the failed step.

**Kill the worker** — stop the worker process mid-flight. Restart it and watch in-flight workflows resume exactly where they left off.

### Human-in-the-Loop

**Doctor approval** — submit an Rx with 0 refills. The workflow durably waits for the doctor's signal. Approve or deny from the Doctor Portal panel. The workflow resumes immediately via a Temporal signal.

**Notification retry** — set Notifications to a high failure rate. The amber banner appears while retries are in flight, updates to green on success or red on permanent failure — without blocking the main workflow.

### Payload Codecs (Encryption + Claim Check)

Toggle either codec from the Feature Flags section in the Control Panel — no restart required.

**Encryption** — PII is encrypted in Temporal history. Toggle it on, submit an Rx, and inspect the workflow in Temporal UI. The payload is opaque. Toggle it off and the fields are visible again.

**Claim Check** — attach a large image (>128 KB) to a new request. With Claim Check enabled the payload is offloaded to external storage and only a token appears in Temporal history. With it disabled the full bytes flow through Temporal — you'll see the size difference in the UI.

### Worker Versioning (GLP-1 Split Track)

Requires `docker compose --profile versioning up --build`.

**The story:** GLP-1 medications (Ozempic, Wegovy, Mounjaro, Zepbound) were initially processed through the standard adjudication path. A new CMS mandate requires them to be split into two parallel tracks at adjudication — the GLP-1 line routes to a specialty pharmacy with mandatory clinical prior authorization, while the remaining line items continue through the standard path. This is a structural change to the workflow DAG. Existing in-flight claims cannot be replayed on the new code without a non-determinism error — which is exactly the problem Worker Versioning solves.

**Step 1 — Establish v1 as current**

Once both workers are polling, promote v1:

```bash
temporal worker deployment set-current-version \
  --deployment-name pbm-adjudication --build-id 1.0
```

**Step 2 — Create a long-lived v1 execution**

Linda Martinez (Ozempic, 0 refills) is seeded by default. Process her workflow. It routes through v1's single-track adjudication, hits the doctor approval step (0 refills), and parks waiting for a signal. This execution is now pinned to v1.

**Step 3 — Deploy v2**

```bash
temporal worker deployment set-current-version \
  --deployment-name pbm-adjudication --build-id 2.0
```

**Step 4 — Submit a new GLP-1 claim**

Create a new prescription with a GLP-1 medication checked. It routes to v2 and fans out into two child workflows visible in the Temporal UI — one for the standard line items, one for the GLP-1 specialty track. A teal "Specialty Prior Auth" card appears in the Doctor Portal.

**Step 5 — Observe both versions running simultaneously**

Linda's workflow is still alive on v1, waiting for its doctor approval signal (amber card). The new claim is running on v2 with its specialty auth card (teal). Approve or deny each independently.

**What this demonstrates:** Temporal routes each workflow's tasks to the version it started on. The structural code change in v2 never touches Linda's execution. No patching, no coordination, no downtime.

---

## Architecture

```
Browser ──► ASP.NET Core API ──► Temporal Server ──► Worker(s)
                                                      ├── PrescriptionWorkflow
                                                      ├── StandardAdjudicationWorkflow  (v2+)
                                                      ├── Glp1AdjudicationWorkflow      (v2+)
                                                      └── PrescriptionActivities
                                                            ├── ValidateEligibilityAsync
                                                            ├── CheckPriorAuthorizationAsync
                                                            ├── AdjudicateClaimAsync
                                                            ├── AdjudicateGlp1ClaimAsync       (v2+)
                                                            ├── RequestSpecialtyPriorAuthAsync (v2+)
                                                            ├── SubmitToSpecialtyPharmacyAsync (v2+)
                                                            ├── RequestDoctorApprovalAsync
                                                            ├── SubmitToPharmacyAsync
                                                            ├── SendNotificationAsync
                                                            ├── MarkOnHoldAsync
                                                            └── MarkNotificationFailedAsync
```

The API starts workflows and sends signals. Workers execute them. SignalR pushes status updates to the browser as each activity completes.

In versioning mode, `worker-v1` and `worker-v2` run simultaneously, each tagged with a deployment build ID. Temporal routes workflow tasks to the correct version based on which version the execution started on.
