#!/usr/bin/env node
/**
 * scaffold.js — Temporal Demo Domain Scaffolder
 *
 * Usage:
 *   node scaffold.js --domain ./domains/bank-transfer.json --source /path/to/pbm-app --out /path/to/output
 *
 * What it does:
 *   1. Deep-copies source (skipping bin/, obj/, .git/, node_modules/)
 *   2. Token-replaces all domain strings in text files (.cs, .json, .yml, .yaml, .html, .md, .csproj, .sln, .txt)
 *   3. Renames files and directories that contain source project names
 *   4. Injects domain-specific seed data and UI sample data
 */

import fs from 'fs';
import path from 'path';
import { execSync } from 'child_process';

// ─── CLI args ──────────────────────────────────────────────────────────────
const args = process.argv.slice(2);
function getArg(flag) {
  const i = args.indexOf(flag);
  return i !== -1 ? args[i + 1] : null;
}

const domainConfigPath = getArg('--domain');
const sourcePath       = getArg('--source') || path.join(process.cwd(), '..', 'pbm-app');
const outputBase       = getArg('--out')    || path.join(process.cwd(), 'output');

if (!domainConfigPath) {
  console.error('Usage: node scaffold.js --domain <config.json> [--source <pbm-app-path>] [--out <output-dir>]');
  process.exit(1);
}

const domain = JSON.parse(fs.readFileSync(domainConfigPath, 'utf8'));
const { tier1, tier2, tier3 } = domain;

const destPath = path.join(outputBase, tier1.projectSlug);

// ─── PBM source tokens (what to search for) ───────────────────────────────
const PBM = {
  // Tier 1
  projectSlug:       'pbm-adjudication',
  namespace:         'PBMAdjudication',
  serviceNamespace:  'PBMAdjudicationService',
  workerNamespace:   'PBMAdjudication.Worker',
  coreNamespace:     'PBMAdjudication.Core',
  dbName:            'pbm_adjudication',
  taskQueue:         'prescription-task-queue',
  workflowIdPrefix:  'prescription',
  port:              '5002',
  accentColor:       '#444CE7',

  // Tier 2 — entity
  entitySingular:        'Prescription',
  entityPlural:          'Prescriptions',
  entitySingularLower:   'prescription',
  entityPluralLower:     'prescriptions',

  // Tier 2 — portals
  portal1Title:      'Patient Portal',
  portal2Title:      'Doctor Portal',
  portal1Actor:      'Patient',
  portal2Actor:      'Doctor',
  portal1ActorLower: 'patient',
  portal2ActorLower: 'doctor',

  // Tier 2 — fields
  itemField:             'Medication',
  itemFieldLower:        'medication',
  valueField:            'Copay',
  valueFieldLower:       'copay',
  eligibilityField:      'EligibleDate',
  eligibilityFieldLower: 'eligibleDate',
  eligibilityLabel:      'Eligible Date',
  quotaField:            'RefillsRemaining',
  quotaFieldLower:       'refillsRemaining',
  quotaLabel:            'Refills Remaining',

  // Tier 2 — approval actor
  approvalActorName:  'Dr. Smith',
};

// ─── Skip list ─────────────────────────────────────────────────────────────
const SKIP_DIRS  = new Set(['bin', 'obj', '.git', 'node_modules', '.vs', 'scaffold']);
const TEXT_EXTS  = new Set(['.cs', '.json', '.yml', '.yaml', '.html', '.md', '.csproj', '.sln', '.txt', '.env', '.dockerignore', '.gitignore']);

// ─── Helpers ───────────────────────────────────────────────────────────────
function shouldSkipDir(name) {
  return SKIP_DIRS.has(name);
}

function isTextFile(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  const base = path.basename(filePath);
  // Catches Dockerfile, Dockerfile.api, Dockerfile.worker, .env, .env.example
  if (base.startsWith('Dockerfile') || base === '.env' || base === '.env.example') return true;
  return TEXT_EXTS.has(ext);
}

/** Build ordered replacement pairs. Order matters: longest/most-specific first. */
function buildReplacements() {
  const pairs = [];

  // Tier 1 — project identity (longest namespaces first to avoid partial matches)
  pairs.push([PBM.serviceNamespace + '.Worker', tier1.workerNamespace]);  // PBMAdjudicationService.Worker → before plain PBMAdjudicationService
  pairs.push([PBM.serviceNamespace,             tier1.serviceNamespace]);
  pairs.push([PBM.workerNamespace,              tier1.workerNamespace]);
  pairs.push([PBM.coreNamespace,                tier1.coreNamespace]);
  pairs.push([PBM.namespace,                    tier1.namespace]);

  pairs.push([PBM.dbName,            tier1.dbName]);
  pairs.push([PBM.taskQueue,         tier1.taskQueue]);
  pairs.push([PBM.workflowIdPrefix + '-', tier1.worflowIdPrefix + '-']);  // "prescription-{id}"
  pairs.push([PBM.accentColor,       tier1.accentColor]);

  // Tier 1 — project title (in HTML title, README)
  pairs.push(['PBM Adjudication System', tier1.projectTitle]);
  pairs.push(['PBM Adjudication',        tier1.projectTitle]);

  // Tier 2 — capitalized forms first (before lowercased)
  pairs.push([PBM.portal1Title,      tier2.portal1Title]);
  pairs.push([PBM.portal2Title,      tier2.portal2Title]);
  pairs.push([PBM.approvalActorName, tier2.approvalActorName]);

  pairs.push([PBM.entityPlural,          tier2.entityPlural]);
  pairs.push([PBM.entitySingular,        tier2.entitySingular]);
  pairs.push([PBM.itemField,             tier2.itemField]);
  pairs.push([PBM.valueField,            tier2.valueField]);
  pairs.push([PBM.eligibilityField,      tier2.eligibilityField]);
  pairs.push([PBM.quotaField,            tier2.quotaField]);
  pairs.push([PBM.portal1Actor,          tier2.portal1Actor]);
  pairs.push([PBM.portal2Actor,          tier2.portal2Actor]);

  // Lowercase forms
  pairs.push([PBM.entityPluralLower,      tier2.entityPluralLower]);
  pairs.push([PBM.entitySingularLower,    tier2.entitySingularLower]);
  pairs.push([PBM.itemFieldLower,         tier2.itemFieldLower]);
  pairs.push([PBM.valueFieldLower,        tier2.valueFieldLower]);
  pairs.push([PBM.eligibilityFieldLower,  tier2.eligibilityFieldLower]);
  pairs.push([PBM.quotaFieldLower,        tier2.quotaFieldLower]);
  pairs.push([PBM.portal1ActorLower,      tier2.portal1ActorLower]);
  pairs.push([PBM.portal2ActorLower,      tier2.portal2ActorLower]);

  // Tier 2 — labels in UI
  pairs.push(['Eligible Date',     tier2.eligibilityLabel]);
  pairs.push(['Refills Remaining', tier2.quotaLabel]);
  pairs.push(['Refills:',          tier2.quotaLabel + ':']);
  pairs.push([PBM.valueField + ':',  tier2.valueField + ':']);

  return pairs;
}

function applyReplacements(text, pairs) {
  let result = text;
  for (const [from, to] of pairs) {
    if (from === to) continue;
    // Escape regex special chars in 'from'
    const escaped = from.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    result = result.replace(new RegExp(escaped, 'g'), to);
  }
  return result;
}

/** Rename a path segment using the same replacement logic */
function renamePath(name) {
  const filenamePairs = [
    ['PBMAdjudicationService.Worker', tier1.workerNamespace],
    ['PBMAdjudicationService',        tier1.serviceNamespace],
    ['PBMAdjudication.Core',          tier1.coreNamespace],
    ['PBMAdjudication',               tier1.namespace],
    ['Prescription',                  tier2.entitySingular],
  ];
  let result = name;
  for (const [from, to] of filenamePairs) {
    if (result.includes(from)) {
      result = result.split(from).join(to);
    }
  }
  return result;
}

// ─── Copy + transform ──────────────────────────────────────────────────────
function copyDir(src, dest, replacements) {
  fs.mkdirSync(dest, { recursive: true });
  const entries = fs.readdirSync(src, { withFileTypes: true });

  for (const entry of entries) {
    if (shouldSkipDir(entry.name)) continue;

    const srcFull  = path.join(src, entry.name);
    const newName  = renamePath(entry.name);
    const destFull = path.join(dest, newName);

    if (entry.isDirectory()) {
      copyDir(srcFull, destFull, replacements);
    } else {
      if (isTextFile(srcFull)) {
        let content = fs.readFileSync(srcFull, 'utf8');
        content = applyReplacements(content, replacements);
        content = injectDomainData(srcFull, entry.name, content);
        fs.writeFileSync(destFull, content, 'utf8');
      } else {
        fs.copyFileSync(srcFull, destFull);
      }
    }
  }
}

// ─── Tier 3: Inject domain-specific data ──────────────────────────────────
function injectDomainData(srcPath, filename, content) {
  const basename = path.basename(srcPath);

  if (basename === 'DatabaseInitializer.cs') {
    content = injectSeedData(content);
  }

  if (basename === 'Program.cs') {
    content = injectLoadGenData(content);
  }

  if (basename === 'index.html') {
    content = injectUIData(content);
  }

  return content;
}

function injectSeedData(content) {
  const seedBlock = tier3.seedRecords.map(r => {
    const eligibleExpr = `DateTime.UtcNow.AddMinutes(${r.eligibleOffsetMinutes})`;
    return `                new ${tier2.entitySingular}
                {
                    PatientId = "${r.patientId}",
                    PatientName = "${r.patientName}",
                    ${tier2.itemField} = "${r.item}",
                    ${tier2.eligibilityField} = ${eligibleExpr},
                    ${tier2.quotaField} = ${r.quota},
                    Status = "Pending"
                }`;
  }).join(',\n');

  content = content.replace(
    /var prescriptions = new\[\]\s*\{[\s\S]*?\};(\s*\n\s*foreach)/,
    `var prescriptions = new[]\n            {\n${seedBlock}\n            };\n$1`
  );

  return content;
}

function injectLoadGenData(content) {
  // Replace the medications array
  const medicationsPattern = /var medications = new\[\] \{[^}]+\}/;
  const newMedications = `var medications = new[] { ${tier3.loadGenItems.map(i => `"${i}"`).join(', ')} }`;
  content = content.replace(medicationsPattern, newMedications);

  // Replace firstNames
  const firstNamesPattern = /var firstNames = new\[\] \{[^}]+\}/;
  const newFirstNames = `var firstNames = new[] { ${tier3.loadGenActorFirstNames.map(n => `"${n}"`).join(', ')} }`;
  content = content.replace(firstNamesPattern, newFirstNames);

  // Replace lastNames
  const lastNamesPattern = /var lastNames = new\[\] \{[^}]+\}/;
  const newLastNames = `var lastNames = new[] { ${tier3.loadGenActorLastNames.map(n => `"${n}"`).join(', ')} }`;
  content = content.replace(lastNamesPattern, newLastNames);

  // Replace the medication line in load gen
  const medLinePBM = /Medication = \$"\{medications\[Random\.Shared\.Next\(medications\.Length\)\]\} \{Random\.Shared\.Next\(10, 80\)\}mg"/;
  const newMedLine = `${tier2.itemField} = $"{medications[Random.Shared.Next(medications.Length)]} ${tier3.loadGenItemSuffixes.length > 0 ? `{new[]{ ${tier3.loadGenItemSuffixes.map(s=>`"${s}"`).join(', ')} }[Random.Shared.Next(${tier3.loadGenItemSuffixes.length})]}` : ''}"`;
  content = content.replace(medLinePBM, newMedLine);

  return content;
}

function injectUIData(content) {
  const optionsBlock = tier3.newRequestItemOptions
    .map(o => `                    <option>${o}</option>`)
    .join('\n');

  content = content.replace(
    /(<select id="medication">)[\s\S]*?(<\/select>)/,
    `$1\n${optionsBlock}\n                </select>`
  );

  return content;
}

// ─── Generate README ───────────────────────────────────────────────────────
function generateReadme() {
  const steps = tier2.steps.map((s, i) => `${i + 1}. **${s.label}** (\`${s.apiRoute}\`)`).join('\n');
  return `# ${tier1.projectTitle}

A [Temporal](https://temporal.io) workflow demo for ${tier1.projectTitle.toLowerCase()}.

## Workflow Steps

${steps}

## Quick Start

\`\`\`bash
# Start all services
docker compose up --build

# App runs at http://localhost:${tier1.port}
\`\`\`

## Configuration

| Setting | Value |
|---------|-------|
| Task Queue | \`${tier1.taskQueue}\` |
| Database | \`${tier1.dbName}\` |
| Port | \`${tier1.port}\` |

---
*Generated by [pbm-app scaffolder](https://github.com/mmerrell/PBMAdjudicationService) from the \`pbm-adjudication\` template.*
`;
}

// ─── Main ──────────────────────────────────────────────────────────────────
console.log(`\n🏗️  Scaffolding: ${tier1.projectTitle}`);
console.log(`   Source:  ${sourcePath}`);
console.log(`   Output:  ${destPath}\n`);

if (fs.existsSync(destPath)) {
  console.error(`❌ Output directory already exists: ${destPath}`);
  console.error('   Delete it first or choose a different --out location.');
  process.exit(1);
}

const replacements = buildReplacements();

console.log('📋 Replacement map:');
for (const [from, to] of replacements) {
  if (from !== to) console.log(`   "${from}" → "${to}"`);
}
console.log();

console.log('📁 Copying and transforming files...');
copyDir(sourcePath, destPath, replacements);

console.log('📝 Writing README.md...');
fs.writeFileSync(path.join(destPath, 'README.md'), generateReadme(), 'utf8');

console.log(`\n✅ Done! New project at: ${destPath}`);
console.log(`\n   Next steps:`);
console.log(`   cd ${destPath}`);
console.log(`   docker compose up --build`);
console.log(`   open http://localhost:${tier1.port}\n`);
