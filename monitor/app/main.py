"""
FastAPI monitoring server.

Endpoints
---------
GET /                     Mini web dashboard (HTML, auto-refreshes every 2s)
GET /api/metrics          All servers — list of snapshots (JSON)
GET /api/metrics/{name}   Single server snapshot (JSON)
GET /healthz              Monitor health check

Startup / Shutdown
------------------
The FastAPI lifespan hook reads servers.json, initialises shared state,
and launches one asyncio collector Task per game server.
On shutdown it cancels all collectors cleanly.
"""
from __future__ import annotations

import json
import logging
import asyncio
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import HTMLResponse, JSONResponse

from . import state as _state
from .collector import start_all_collectors

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

# servers.json lives in the monitor/ root (one level above monitor/app/)
_CONFIG_PATH  = Path(__file__).parent.parent / "servers.json"
_DASHBOARD_PATH = Path(__file__).parent / "dashboard.html"

_collector_tasks: list[asyncio.Task] = []


def _load_config() -> dict:
    with open(_CONFIG_PATH, encoding="utf-8") as f:
        return json.load(f)


@asynccontextmanager
async def lifespan(app: FastAPI):
    cfg = _load_config()
    servers      = cfg.get("servers", [])
    poll_interval = float(cfg.get("pollIntervalSec", 5.0))

    _state.init_servers(servers)

    global _collector_tasks
    _collector_tasks = await start_all_collectors(servers, poll_interval)
    logger.info("[Monitor] Started %d collector(s) (poll interval=%.1fs)",
                len(_collector_tasks), poll_interval)

    yield   # ← server is running

    logger.info("[Monitor] Shutting down collectors…")
    for task in _collector_tasks:
        task.cancel()
    await asyncio.gather(*_collector_tasks, return_exceptions=True)
    logger.info("[Monitor] All collectors stopped")


app = FastAPI(title="Game Server Monitor", lifespan=lifespan)


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------

@app.get("/", response_class=HTMLResponse)
async def dashboard() -> HTMLResponse:
    """Serve the mini monitoring dashboard."""
    return HTMLResponse(_DASHBOARD_PATH.read_text(encoding="utf-8"))


@app.get("/api/metrics")
async def metrics_all() -> JSONResponse:
    """Return stats snapshots for all configured servers."""
    return JSONResponse({"servers": _state.get_all()})


@app.get("/api/metrics/{name}")
async def metrics_one(name: str) -> JSONResponse:
    """Return stats snapshot for a single server by name."""
    data = _state.get_one(name)
    if data is None:
        raise HTTPException(status_code=404, detail=f"Server '{name}' not found")
    return JSONResponse(data)


@app.get("/healthz")
async def healthz() -> dict:
    """Monitor self health-check."""
    return {"status": "ok"}
