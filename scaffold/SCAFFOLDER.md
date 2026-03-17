# Temporal Demo Scaffolder

Generates a new self-contained `.NET` demo project from `pbm-app` as the template. Each generated project is unaware of the others — it just looks like a purpose-built app for its domain.

## Quick start

```bash
# From pbm-app/scaffold/
node scaffold.js \
  --domain ./domains/bank-transfer.json \
  --source /path/to/pbm-app \
  --out ~/Projects
```

This produces `~/Projects/bank-transfer/` — a complete, runnable project. No post-processing needed.

```bash
cd ~/Projects/bank-transfer
docker compose up --build
open http://localhost:5002
```

---

## Generating a new domain config with Claude

You don't write configs by hand. Open a conversation with Claude (this Project), paste the prompt below, and describe your domain. Claude will output a ready-to-use `domain-config.json`.

### Prompt template

```
I need a domain config for the pbm-app scaffolder.

The scaffolder takes a domain-config.json and produces a new .NET Temporal demo project
by replacing all PBM-specific content with domain-appropriate equivalents.

The app structure is fixed — it always has:
- 5 sequential workflow steps (the 4th is a human-approval step with wait/signal)
- A "portal 1" panel showing the main entities (left column in the UI)
- A "portal 2" panel showing pending approval requests (middle column)
- A control panel with endpoint chaos controls and logs (right column)
- A value field shown as a badge on each entity card (like "$12.50 copay")
- A quota field that, when zero, triggers the human-approval step
- An eligibility/scheduled date field (can be in the future, causing a wait)
- A "Generate 20" load test button that creates random entities

The domain I want to build:

[DESCRIBE YOUR DOMAIN HERE — e.g.:
"An e-commerce order fulfillment workflow. Steps are: inventory check, payment
processing, fraud review, warehouse pick/pack, and shipping dispatch. Human
intervention triggers when the fraud score is high (quota field = 0 means manual
review needed). Portal 1 shows customer orders, Portal 2 shows the fraud review
queue for the operations team. The value field should be the order total. Sample
data should use realistic product names and company names as customers."]

Please output a complete domain-config.json following this exact schema:

{
  "tier1": {
    "projectSlug":       "kebab-case-name",
    "projectTitle":      "Human Readable Title",
    "namespace":         "PascalCase",
    "serviceNamespace":  "PascalCaseService",
    "workerNamespace":   "PascalCase.Worker",
    "coreNamespace":     "PascalCase.Core",
    "dbName":            "snake_case_db_name",
    "taskQueue":         "entity-task-queue",
    "worflowIdPrefix":   "entity",
    "port":              "5002",
    "accentColor":       "#hexcolor"
  },
  "tier2": {
    "entitySingular":        "PascalCase",
    "entityPlural":          "PascalCasePlural",
    "entitySingularLower":   "camelCase",
    "entityPluralLower":     "camelCasePlural",

    "portal1Title":          "Portal Name",
    "portal2Title":          "Approval Portal Name",
    "portal1Actor":          "PascalCase actor (who submits)",
    "portal2Actor":          "PascalCase actor (who approves)",
    "portal1ActorLower":     "lowercase",
    "portal2ActorLower":     "lowercase",

    "itemField":             "PascalCase (what the entity IS — medication, product, account)",
    "itemFieldLower":        "camelCase",
    "itemFieldLabel":        "Human label",
    "valueField":            "PascalCase (the money/score/result field)",
    "valueFieldLower":       "camelCase",
    "valueFieldLabel":       "Human label",
    "valuePrefix":           "$",
    "valueSuffix":           "",

    "eligibilityField":      "PascalCase (scheduled/eligible date field)",
    "eligibilityFieldLower": "camelCase",
    "eligibilityLabel":      "Human label",

    "quotaField":            "PascalCase (the field that, when 0, triggers approval)",
    "quotaFieldLower":       "camelCase",
    "quotaLabel":            "Human label",
    "quotaZeroMeansApproval": true,

    "approvalActor":         "PascalCase",
    "approvalActorLower":    "lowercase",
    "approvalActorName":     "Specific name shown in logs (e.g. 'Dr. Smith', 'Compliance Team')",

    "steps": [
      { "key": "validate",   "label": "Step 1 label", "apiRoute": "validate",         "logPrefix": "short" },
      { "key": "authorize",  "label": "Step 2 label", "apiRoute": "authorize",        "logPrefix": "short" },
      { "key": "adjudicate", "label": "Step 3 label", "apiRoute": "adjudicate",       "logPrefix": "short" },
      { "key": "approval",   "label": "Step 4 label", "apiRoute": "request-approval", "logPrefix": "short" },
      { "key": "submit",     "label": "Step 5 label", "apiRoute": "submit",           "logPrefix": "short" }
    ]
  },
  "tier3": {
    "seedRecords": [
      // 5 records. eligibleOffsetMinutes: positive = future (causes wait), negative = past (eligible now), 0 = now.
      // quota: 0 triggers the approval step. Mix of values to show different scenarios.
      { "patientId": "X001", "patientName": "Actor name", "item": "Item name", "eligibleOffsetMinutes": 2,     "quota": 3 },
      { "patientId": "X002", "patientName": "Actor name", "item": "Item name", "eligibleOffsetMinutes": -1440, "quota": 2 },
      { "patientId": "X003", "patientName": "Actor name", "item": "Item name", "eligibleOffsetMinutes": 20,    "quota": 1 },
      { "patientId": "X004", "patientName": "Actor name", "item": "Item name", "eligibleOffsetMinutes": -7200, "quota": 0 },
      { "patientId": "X005", "patientName": "Actor name", "item": "Item name", "eligibleOffsetMinutes": -4320, "quota": 1 }
    ],
    "loadGenItems": ["item1", "item2", "item3", "item4", "item5", "item6", "item7"],
    "loadGenItemSuffixes": ["variant1", "variant2", "variant3"],
    "loadGenActorFirstNames": ["name1", "name2", "name3", "name4", "name5", "name6", "name7", "name8", "name9", "name10"],
    "loadGenActorLastNames":  ["name1", "name2", "name3", "name4", "name5", "name6", "name7", "name8", "name9", "name10"],
    "newRequestItemOptions": [
      "Full item name 1",
      "Full item name 2",
      "Full item name 3",
      "Full item name 4",
      "Full item name 5",
      "Full item name 6",
      "Full item name 7"
    ]
  }
}

A few notes to guide your output:
- accentColor: pick something that fits the domain's industry feel (finance = blue, healthcare = teal, logistics = amber, etc.)
- The 5 step keys (validate, authorize, adjudicate, approval, submit) are fixed — only the labels and logPrefixes change
- For domains where actors are companies not people, loadGenActorLastNames can be all empty strings ""
- The 4th seed record should always have quota=0 to demonstrate the approval step in the default data
- The 1st and 3rd seed records should have a future eligibleOffsetMinutes to demonstrate the wait behavior
```

Save the output as `scaffold/domains/your-domain.json`, then run the scaffolder.

---

## Schema reference

### Tier 1 — project identity

Controls namespaces, filenames, database, and infrastructure config. Everything that makes it a distinct project.

| Field | Example | What it affects |
|-------|---------|-----------------|
| `projectSlug` | `bank-transfer` | Output directory name |
| `projectTitle` | `Wire Transfer Processing System` | HTML title, README |
| `namespace` | `BankTransfer` | C# namespace root, Core project name |
| `serviceNamespace` | `BankTransferService` | API project name, .sln name |
| `workerNamespace` | `BankTransfer.Worker` | Worker project name |
| `coreNamespace` | `BankTransfer.Core` | Core project name |
| `dbName` | `bank_transfer` | Postgres database name |
| `taskQueue` | `transfer-task-queue` | Temporal task queue name |
| `worflowIdPrefix` | `transfer` | Workflow ID prefix (`transfer-{patientId}-{id}`) |
| `port` | `5002` | App port (keep 5002 unless you need to run multiple side-by-side) |
| `accentColor` | `#0EA5E9` | UI accent color (replaces Temporal UV blue `#444CE7`) |

### Tier 2 — domain concepts

The business vocabulary. PascalCase and camelCase forms are replaced separately throughout all `.cs`, `.json`, and `.html` files.

**Entity** — what the workflow processes (Prescription → Transfer → Order):
- `entitySingular` / `entitySingularLower` — used in C# class names, API routes, log messages
- `entityPlural` / `entityPluralLower` — used in API endpoint names, UI headers

**Portals** — the two actor-facing panels in the UI:
- `portal1Title` / `portal1Actor` — left panel (the submitter: Patient, Customer, Employee)
- `portal2Title` / `portal2Actor` — middle panel (the approver: Doctor, Compliance Officer, Manager)

**Fields** — the four semantic fields on each entity card:
- `itemField` — what the entity IS (`Medication`, `Destination`, `Product`)
- `valueField` — the computed result shown as a badge (`Copay`, `Amount`, `Total`)
- `eligibilityField` — the scheduled/eligible date that may cause a wait step
- `quotaField` — the field that triggers human approval when it reaches zero

**Steps** — the 5 workflow steps. Keys are fixed; only `label` and `logPrefix` change:

| Key | PBM label | Your label |
|-----|-----------|------------|
| `validate` | Validate Eligibility | KYC Verification, Inventory Check, ... |
| `authorize` | Prior Authorization | Fraud Screening, Credit Check, ... |
| `adjudicate` | Adjudicate Claim | Fee Calculation, Price Quote, ... |
| `approval` | Doctor Approval | Compliance Hold, Manager Review, ... |
| `submit` | Submit to Pharmacy | Submit to SWIFT, Dispatch Order, ... |

### Tier 3 — sample data

What appears in the UI on first load and when "Generate 20" is clicked.

**seedRecords** (5 records, always):
- `patientId` — entity ID prefix (P001, C001, ORD001...)
- `patientName` — the actor name shown on the card
- `item` — the item field value
- `eligibleOffsetMinutes` — positive = future (triggers wait), negative = already eligible
- `quota` — set one record to `0` to seed an approval scenario automatically

**loadGen** — used by the "Generate 20" button:
- `loadGenItems` + `loadGenItemSuffixes` — combined randomly to generate item names
- `loadGenActorFirstNames` + `loadGenActorLastNames` — combined for actor names (use empty strings `""` for last names if actors are companies)

**newRequestItemOptions** — the dropdown in the "New Request" modal (7 options is a good number).

---

## Existing domains

| File | Domain | Accent |
|------|--------|--------|
| `domains/pbm-adjudication.json` | PBM prescription adjudication | `#444CE7` Temporal UV blue |
| `domains/bank-transfer.json` | Bank wire transfer with compliance hold | `#0EA5E9` sky blue |

---

## Notes

- The scaffolder skips `bin/`, `obj/`, `.git/`, and `node_modules/` automatically
- The `.sln` file, all `.csproj` files, Docker files, and GitHub Actions workflow are all transformed
- If your output directory already exists the scaffolder will refuse to overwrite it — delete it first
- The `port` field defaults to `5002` for all domains. If you need two demo projects running simultaneously, give one a different port (e.g. `5003`) and also update the `docker-compose.yml` port mapping after scaffolding
