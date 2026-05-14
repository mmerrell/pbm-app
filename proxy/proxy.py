"""
PBM Network Proxy
=================
A transparent HTTP reverse proxy that sits between the Temporal worker and the
PBM API. The control API (port 5001) lets the Network Console tab inject faults
per endpoint without touching any C# code.

Ports
-----
  5003  Proxy — worker sends requests here instead of directly to the API
  5001  Control API — frontend reads/writes fault rules here

Fault rules (stored in memory, reset on container restart)
-----------------------------------------------------------
Each API path prefix maps to a rule dict:
  {
    "outage":     bool   — return HTTP 503 immediately
    "latency_ms": int    — delay before forwarding (0 = no delay)
    "failure_pct": int   — random failure rate 0-100
  }

Control API endpoints
---------------------
  GET  /rules              — return all current rules
  POST /rules/{endpoint}   — set rule for an endpoint key
  POST /reset              — clear all rules back to defaults
"""

import asyncio
import json
import random
import re

import aiohttp
from aiohttp import web

# ── Config ────────────────────────────────────────────────────────────────────

UPSTREAM = "http://api:5002"
PROXY_PORT = 5003
CONTROL_PORT = 5001

# The endpoint keys shown in the Network Console, mapped to the URL path
# fragments they match. Order matters — first match wins.
ENDPOINTS = {
    "validate":        "/api/validate/",
    "authorize":       "/api/authorize/",
    "adjudicate-glp1": "/api/adjudicate-glp1/",
    "adjudicate":      "/api/adjudicate/",
    "notify":          "/api/notify",
    "submit-specialty":"/api/submit-specialty/",
    "submit":          "/api/submit/",
}

DEFAULT_RULE = {"outage": False, "latency_ms": 0, "failure_pct": 0}

# Live rules dict — mutated by the control API
rules: dict[str, dict] = {key: DEFAULT_RULE.copy() for key in ENDPOINTS}


# ── Helpers ───────────────────────────────────────────────────────────────────

def _match_endpoint(path: str) -> str | None:
    """Return the first endpoint key whose path fragment appears in `path`."""
    for key, fragment in ENDPOINTS.items():
        if fragment in path:
            return key
    return None


def _should_fail(rule: dict) -> bool:
    pct = rule.get("failure_pct", 0)
    return pct > 0 and random.randint(1, 100) <= pct


# ── Proxy handler ─────────────────────────────────────────────────────────────

async def proxy_handler(request: web.Request) -> web.Response:
    path = request.path_qs
    endpoint_key = _match_endpoint(path)
    rule = rules.get(endpoint_key, DEFAULT_RULE) if endpoint_key else DEFAULT_RULE

    # Outage — hard 503, no upstream call
    if rule.get("outage"):
        return web.Response(
            status=503,
            text=f"[pbm-proxy] '{endpoint_key}' is unavailable (outage toggled on)",
        )

    # Random failure
    if _should_fail(rule):
        return web.Response(
            status=503,
            text=f"[pbm-proxy] '{endpoint_key}' failed (random {rule['failure_pct']}% rate)",
        )

    # Latency injection
    latency = rule.get("latency_ms", 0)
    if latency > 0:
        await asyncio.sleep(latency / 1000)

    # Forward to upstream
    upstream_url = UPSTREAM + path
    try:
        body = await request.read()
        headers = {
            k: v for k, v in request.headers.items()
            if k.lower() not in ("host", "content-length")
        }

        async with aiohttp.ClientSession() as session:
            async with session.request(
                method=request.method,
                url=upstream_url,
                headers=headers,
                data=body,
                allow_redirects=False,
            ) as resp:
                resp_body = await resp.read()
                return web.Response(
                    status=resp.status,
                    headers={
                        k: v for k, v in resp.headers.items()
                        if k.lower() not in ("transfer-encoding", "content-encoding")
                    },
                    body=resp_body,
                )
    except aiohttp.ClientConnectorError as e:
        return web.Response(status=502, text=f"[pbm-proxy] Could not reach upstream: {e}")


# ── Control API handlers ──────────────────────────────────────────────────────

async def get_rules(request: web.Request) -> web.Response:
    return web.json_response(rules)


async def set_rule(request: web.Request) -> web.Response:
    key = request.match_info["endpoint"]
    if key not in ENDPOINTS:
        return web.Response(status=404, text=f"Unknown endpoint key '{key}'")
    body = await request.json()
    rules[key] = {
        "outage":      bool(body.get("outage", False)),
        "latency_ms":  int(body.get("latency_ms", 0)),
        "failure_pct": int(body.get("failure_pct", 0)),
    }
    return web.json_response({"ok": True, "key": key, "rule": rules[key]})


async def reset_rules(request: web.Request) -> web.Response:
    for key in ENDPOINTS:
        rules[key] = DEFAULT_RULE.copy()
    return web.json_response({"ok": True})


async def get_endpoints(request: web.Request) -> web.Response:
    """Return the list of known endpoint keys so the frontend can build its UI."""
    return web.json_response(list(ENDPOINTS.keys()))


# ── App factory ───────────────────────────────────────────────────────────────

def make_proxy_app() -> web.Application:
    app = web.Application()
    app.router.add_route("*", "/{path_info:.*}", proxy_handler)
    return app


def make_control_app() -> web.Application:
    app = web.Application()
    app.router.add_get("/endpoints", get_endpoints)
    app.router.add_get("/rules", get_rules)
    app.router.add_post("/rules/{endpoint}", set_rule)
    app.router.add_post("/reset", reset_rules)
    return app


# ── Entry point ───────────────────────────────────────────────────────────────

async def main():
    proxy_app = make_proxy_app()
    control_app = make_control_app()

    proxy_runner = web.AppRunner(proxy_app)
    control_runner = web.AppRunner(control_app)

    await proxy_runner.setup()
    await control_runner.setup()

    proxy_site = web.TCPSite(proxy_runner, "0.0.0.0", PROXY_PORT)
    control_site = web.TCPSite(control_runner, "0.0.0.0", CONTROL_PORT)

    await proxy_site.start()
    await control_site.start()

    print(f"[pbm-proxy] Proxy  listening on :{PROXY_PORT}  -> {UPSTREAM}")
    print(f"[pbm-proxy] Control listening on :{CONTROL_PORT}")

    await asyncio.Event().wait()  # run forever


if __name__ == "__main__":
    asyncio.run(main())
