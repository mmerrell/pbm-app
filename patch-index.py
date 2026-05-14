"""
Restores wwwroot/index.html with the proxy-enabled version.
Run from the project root: python patch-index.py
"""
import os, sys

HTML_PATH = "wwwroot/index.html"

# Check if git can restore the original
import subprocess
result = subprocess.run(
    ["git", "show", "HEAD:wwwroot/index.html"],
    capture_output=True, text=True, encoding="utf-8"
)

if result.returncode != 0:
    print("ERROR: Could not restore index.html from git. Please check git history.")
    print(result.stderr)
    sys.exit(1)

content = result.stdout

if "endpointConfigs" not in content and "proxyRules" in content:
    print("Already patched — nothing to do.")
    sys.exit(0)

if "endpointConfigs" not in content:
    print("ERROR: Unexpected index.html content — cannot patch safely.")
    sys.exit(1)

print(f"Restored index.html from git HEAD ({len(content)} bytes). Applying proxy patch...")

# ── Patches ───────────────────────────────────────────────────────────────────

content = content.replace(
    "/* \u2500\u2500\u2500 Endpoint Config \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500 */",
    "/* \u2500\u2500\u2500 Network Proxy Controls \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500 */"
)

content = content.replace(
    "        let endpointConfigs = {};",
    "        let proxyRules = {};"
)

content = content.replace(
    "                await loadConfigs();",
    "                await loadProxyRules();"
)

content = content.replace(
    '<div id="endpointConfigs"></div>',
    '<div id="networkControls"></div>'
)

content = content.replace(
    "        async function loadConfigs() {\n            const response = await fetch('/api/config');\n            endpointConfigs = await response.json();\n            renderConfigs();\n        }",
    """        async function loadProxyRules() {
            try {
                const response = await fetch('/api/proxy/rules');
                proxyRules = await response.json();
                renderNetworkControls();
            } catch (e) {
                console.warn('[proxy] Control API not available:', e);
            }
        }"""
)

OLD_RENDER = "        // \u2500\u2500 Render endpoint configs \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n\n        function renderConfigs() {"
if OLD_RENDER not in content:
    print("WARNING: Could not find renderConfigs block by comment anchor. Trying fallback...")
    OLD_RENDER = "        function renderConfigs() {"

NEW_RENDER_FULL = """        // \u2500\u2500 Render network proxy controls \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        const ENDPOINT_LABELS = {
            'validate':         'Validate Eligibility',
            'authorize':        'Prior Authorization',
            'adjudicate':       'Adjudicate Claim',
            'adjudicate-glp1':  'Adjudicate GLP-1 (Specialty)',
            'notify':           'Notifications',
            'submit':           'Submit to Pharmacy',
            'submit-specialty': 'Submit to Specialty Pharmacy',
        };

        function renderNetworkControls() {
            const container = document.getElementById('networkControls');
            const keys = Object.keys(ENDPOINT_LABELS);
            container.innerHTML = keys.map(key => {
                const rule = proxyRules[key] || { outage: false, failure_pct: 0, latency_ms: 0 };
                const label = ENDPOINT_LABELS[key];
                return `
                    <div class="endpoint-config">
                        <div class="endpoint-header">
                            <h3>${label}</h3>
                            <label class="toggle-switch">
                                <input type="checkbox" id="${key}-outage" ${rule.outage ? 'checked' : ''}
                                    onchange="setProxyRule('${key}', 'outage', this.checked)">
                                <span class="toggle-slider"></span>
                            </label>
                        </div>
                        <div class="control-group">
                            <label class="control-label">Failure Rate</label>
                            <div class="slider-container">
                                <input type="range" min="0" max="100" value="${rule.failure_pct}"
                                    oninput="setProxyRule('${key}', 'failure_pct', this.value)">
                                <span class="slider-value" id="${key}-failure">${rule.failure_pct}%</span>
                            </div>
                        </div>
                        <div class="control-group">
                            <label class="control-label">Latency (ms)</label>
                            <div class="slider-container">
                                <input type="range" min="0" max="5000" step="100" value="${rule.latency_ms}"
                                    oninput="setProxyRule('${key}', 'latency_ms', this.value)">
                                <span class="slider-value" id="${key}-latency">${rule.latency_ms}ms</span>
                            </div>
                        </div>
                    </div>`;
            }).join('');
        }

        async function setProxyRule(endpoint, property, value) {
            const rule = proxyRules[endpoint] || { outage: false, failure_pct: 0, latency_ms: 0 };
            if (property === 'outage') rule.outage = value;
            else rule[property] = parseInt(value);
            proxyRules[endpoint] = rule;
            if (property === 'failure_pct') document.getElementById(`${endpoint}-failure`).textContent = `${value}%`;
            else if (property === 'latency_ms') document.getElementById(`${endpoint}-latency`).textContent = `${value}ms`;
            await fetch(`/api/proxy/rules/${endpoint}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(rule)
            });
        }"""

# Find and replace the entire renderConfigs+updateConfig block
import re
pattern = r"        // \u2500\u2500 Render endpoint configs.*?async function updateConfig\(endpoint, property, value\) \{.*?\n        \}"
match = re.search(pattern, content, re.DOTALL)
if match:
    content = content[:match.start()] + NEW_RENDER_FULL + content[match.end():]
    print("Replaced renderConfigs+updateConfig block.")
else:
    print("WARNING: Could not find renderConfigs block via regex. Manual edit may be needed.")

# Reset block
OLD_RESET = """                Object.keys(endpointConfigs).forEach(key => {
                    endpointConfigs[key] = { failureRatePercent: 0, latencyMs: 0, completeOutage: false };
                });
                for (const endpoint of Object.keys(endpointConfigs)) {
                    await fetch(`/api/config/${endpoint}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(endpointConfigs[endpoint]) });
                }
                await fetch('/api/admin/reset', { method: 'POST' });
                clearLogs();
                await loadPrescriptions();
                await loadApprovals();
                await loadSpecialtyApprovals();
                renderConfigs();"""

NEW_RESET = """                await fetch('/api/proxy/reset', { method: 'POST' });
                await fetch('/api/admin/reset', { method: 'POST' });
                clearLogs();
                await loadPrescriptions();
                await loadApprovals();
                await loadSpecialtyApprovals();
                await loadProxyRules();"""

content = content.replace(OLD_RESET, NEW_RESET)

# ── Verify ────────────────────────────────────────────────────────────────────
remaining = [l.strip() for l in content.splitlines()
             if "endpointConfigs" in l
             or ("api/config/" in l and "features" not in l)
             or "renderConfigs(" in l
             or "loadConfigs(" in l]
if remaining:
    print("WARNING: unexpected old references still present:")
    for r in remaining:
        print(" ", r)
else:
    print("All old references cleaned.")

with open(HTML_PATH, "w", encoding="utf-8") as f:
    f.write(content)

print(f"Done. Wrote {HTML_PATH} ({len(content)} bytes).")
