# PBM Adjudication Service

A reference implementation demonstrating the value of [Temporal](https://temporal.io/) workflow orchestration for .NET developers. This project simulates a Pharmacy Benefit Management (PBM) prescription adjudication workflow, showing the dramatic difference between ad-hoc orchestration and durable execution.

## Overview

The repo contains two branches that implement the same business logic:

| Branch | Description |
|--------|-------------|
| `pre-temporal` | Traditional ad-hoc orchestration — client-driven, fragile, stateless |
| `temporal` | Durable execution with Temporal — resilient, observable, resumable |

Both branches expose the same UI so you can run them side by side and contrast the behavior when services fail, workers restart, or approvals are delayed.

---

## The Business Problem

A prescription goes through five steps before it reaches the pharmacy:

1. **Validate Eligibility** — Is the patient covered? Is the prescription date valid?
2. **Prior Authorization** — Does the insurer require pre-approval for this drug?
3. **Adjudicate Claim** — Calculate the copay and coverage amount
4. **Doctor Approval** — If no refills remain, route to the prescribing physician
5. **Submit to Pharmacy** — Transmit the approved claim to the dispensing pharmacy

Each step calls an external service. Any of those services can be slow, flaky, or completely down. The Control Panel lets you simulate failure rates, latency, and complete outages for each service independently.

---

## Architecture

```
┌─────────────────────┐     SignalR      ┌──────────────────┐
│   Browser (index.html)│ ◄────────────── │  ASP.NET Core    │
│   Patient Portal    │                  │  Web API         │
│   Doctor Portal     │ ────────────────►│  (Program.cs)    │
│   Control Panel     │     HTTP         └────────┬─────────┘
└─────────────────────┘                           │
                                                  │ HTTP
                                         ┌────────▼─────────┐
                                         │  Temporal Server  │
                                         │  (localhost:7233) │
                                         └────────┬─────────┘
                                                  │ Task Queue
                                         ┌────────▼─────────┐
                                         │  Temporal Worker  │
                                         │  (Worker project) │
                                         │  PrescriptionWorkflow│
                                         │  PrescriptionActivities│
                                         └──────────────────┘
```

### Project Structure (temporal branch)

```
PBMAdjudicationService/
├── Program.cs              # App bootstrap, middleware, API endpoints
├── Models.cs               # Core domain models (Prescription, etc.)
├── DataStore.cs            # In-memory state store
├── NotificationHub.cs      # SignalR hub
├── EndpointHelper.cs       # Service simulation utilities
├── Activities.cs           # Temporal activity implementations
├── Workflows.cs            # PrescriptionWorkflow definition
├── Models/
│   └── WorkflowModels.cs   # Temporal-specific input/output types
├── wwwroot/
│   └── index.html          # Single-page demo UI
└── PBMAdjudicationService.Worker/
    └── Program.cs          # Temporal worker host (separate process)
```

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Temporal CLI](https://docs.temporal.io/cli) (`temporal` command)
- A terminal that supports multiple concurrent processes (or multiple terminal windows)

---

## Setup & Running

### pre-temporal branch

```bash
git checkout pre-temporal
cd PBMAdjudicationService
dotnet run
```

Navigate to `http://localhost:5002`

### temporal branch

Three processes need to be running. Start them in order:

**1. Temporal Server**
```bash
temporal server start-dev
```

**2. Web API** (from repo root)
```bash
cd PBMAdjudicationService
dotnet run
```

**3. Temporal Worker** (separate terminal, from repo root)
```bash
cd PBMAdjudicationService/PBMAdjudicationService.Worker
dotnet run
```

Navigate to `http://localhost:5002`

> **Note:** The worker must be restarted after rebuilding the main project during development. This is a known limitation of the current single-solution structure — see the Architecture Roadmap below.

### Running Both Branches Simultaneously

Use git worktrees to run both versions side by side:

```bash
# One-time setup
git worktree add ../PBMAdjudicationService-pre-temporal pre-temporal
git worktree add ../PBMAdjudicationService-temporal temporal
```

Run pre-temporal on port 5002 and temporal on a different port by setting `ASPNETCORE_URLS` in the temporal worktree's `appsettings.json`.

---

## Configuration

Service behavior is configured at runtime via the Control Panel in the UI. No restart required.

| Setting | Description |
|---------|-------------|
| Failure Rate | % chance the service returns a 500 error |
| Latency | Artificial delay added to each call (ms) |
| Complete Outage | Service returns 503 on every call |

The base URL for the Temporal worker's activity HTTP calls defaults to `http://localhost:5002` and can be overridden in `appsettings.json`:

```json
{
  "BaseUrl": "http://localhost:5002"
}
```

---

## Demo Walkthrough

### The Core Contrast

The same UI, the same business logic, two very different failure characteristics.

**pre-temporal:** The browser orchestrates the workflow. If a service fails mid-flight, you get an orange failed state and a "Reprocess" button. Retries are manual. State lives in the browser — refresh and it's gone. The workflow has no identity beyond the current browser session.

**temporal:** The server orchestrates the workflow via a durable Temporal workflow. If a service fails, Temporal retries automatically. If the worker crashes mid-flight, the workflow resumes exactly where it left off when the worker restarts. State is durable. Doctor approvals can happen hours or days later — the workflow just waits.

### Suggested Demo Flow

1. **Start clean** — hit Reset All to clear state
2. **Happy path** — submit a single Rx and walk through the five pipeline steps
3. **Introduce failure** — set Prior Auth to 50% failure rate, generate 10 Rxs, show retries
4. **Complete outage** — set a service to Complete Outage, show workflow behavior in each branch
5. **Doctor approval** — submit an Rx with 0 refills remaining, approve/deny from the Doctor Portal
6. **Kill the worker** (temporal only) — stop the worker mid-flight, show workflows resume on restart
7. **Notification retry** — set Notifications to a high failure rate, show amber retry banner → green on success

### UI Indicators

| Pip Color | Meaning |
|-----------|---------|
| Gray | Not yet started |
| Blue pulsing | In progress |
| Orange pulsing | Waiting (eligibility date, doctor approval) |
| Green ✓ | Completed |
| Red ✗ | Failed (business logic — denial) |
| Orange 💥 | Failed (infrastructure — service error) |
| Gray ⊘ | Skipped (approval not required) |

| Card Color | Meaning |
|------------|---------|
| Green background | Completed successfully |
| Red background | Denied by doctor |
| Orange background | On hold (infrastructure failure exhausted retries) |

---

## Architecture Roadmap

The following improvements are planned for future versions:

### High Priority

**Three-project solution split**
Currently the Worker project lives inside the main project's directory, causing build conflicts when both are running simultaneously. The correct structure is:
- `PBMAdjudicationService.Api` — web app, SignalR, endpoints, UI
- `PBMAdjudicationService.Worker` — Temporal worker process
- `PBMAdjudicationService.Core` — shared types: models, workflow definitions, activity interfaces

Both Api and Worker reference Core but not each other, enabling independent builds, deployments, and scaling.

**Persistent data store**
The current `DataStore` is in-memory. Concurrent workflow executions can cause race conditions under load. Replace with SQLite (or similar) for demo stability and to survive process restarts.

**Move hardcoded base URL to configuration**
The worker's activity base URL is currently hardcoded to `http://localhost:5002`. This should be injected via `IConfiguration` from `appsettings.json`.

### Future Features

These are planned to showcase additional Temporal capabilities:

- **Encryption codec** — demonstrate Temporal's data converter API for encrypting workflow payloads at rest
- **Claim check pattern** — store large payloads externally, pass references through the workflow
- **Child workflows** — decompose the adjudication workflow into sub-workflows per step
- **Schedules** — demonstrate scheduled workflow execution for batch processing
- **Worker versioning** — show safe deployment of workflow code changes without disrupting in-flight workflows
- **Multi-language activities** — call a Python or Go activity from a .NET workflow

### UI / UX

- **Toggle direction** — outage toggles are currently inverted (On should be right, Off should be left). Needs fixing in both branches simultaneously.
- **Notification badge** — the amber "notification retrying" banner currently stays visible after success. Consider auto-dismissing the green success state after a few seconds.

---

## Key Files Reference

| File | Purpose |
|------|---------|
| `Workflows.cs` | `PrescriptionWorkflow` — the durable workflow definition |
| `Activities.cs` | `PrescriptionActivities` — each step as a Temporal activity |
| `Models/WorkflowModels.cs` | Input/output types for workflow and activity boundaries |
| `EndpointHelper.cs` | `SimulateEndpointBehavior` — injects latency, failures, outages |
| `DataStore.cs` | In-memory state for prescriptions, approvals, configs |
| `wwwroot/index.html` | Entire frontend — SignalR client, rendering, UI logic |

---

## Contributing

This is an active reference implementation. If you're a Temporal Solutions Architect using this for customer demos, please open a PR or file an issue for any scenarios you'd like to see covered.

Planned conference appearances and workshop use are tracked in the repo's project board.

---

## License

MIT