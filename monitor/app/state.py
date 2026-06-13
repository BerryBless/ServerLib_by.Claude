"""
Shared in-memory state for all server snapshots.

Single asyncio event loop — no locking needed.
All reads and writes happen on the same loop, so dict operations are safe.
"""
from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Optional

# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------

@dataclass
class ServerState:
    name: str
    host: str
    port: int
    online: bool = False
    last_updated_ms: int = 0
    snapshot: Optional[dict] = field(default=None)


# ---------------------------------------------------------------------------
# Global registry: name -> ServerState
# (module-level dict — single asyncio loop, no lock required)
# ---------------------------------------------------------------------------
_servers: dict[str, ServerState] = {}


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def init_servers(servers_cfg: list[dict]) -> None:
    """Initialize state from the servers.json 'servers' list."""
    _servers.clear()
    for s in servers_cfg:
        _servers[s['name']] = ServerState(
            name=s['name'],
            host=s['host'],
            port=s['port'],
        )


def update_snapshot(name: str, snapshot: dict) -> None:
    """Mark server as online and store its latest stats snapshot."""
    st = _servers.get(name)
    if st:
        st.online = True
        st.snapshot = snapshot
        st.last_updated_ms = int(time.time() * 1000)


def mark_offline(name: str) -> None:
    """Mark server as offline (connection lost / unreachable)."""
    st = _servers.get(name)
    if st:
        st.online = False


def get_all() -> list[dict]:
    """Return all server states as a list of dicts (for JSON response)."""
    return [_to_dict(st) for st in _servers.values()]


def get_one(name: str) -> Optional[dict]:
    """Return a single server state dict, or None if the name is unknown."""
    st = _servers.get(name)
    return _to_dict(st) if st else None


def _to_dict(st: ServerState) -> dict:
    return {
        'name':          st.name,
        'host':          st.host,
        'port':          st.port,
        'online':        st.online,
        'lastUpdatedMs': st.last_updated_ms,
        'snapshot':      st.snapshot,
    }
