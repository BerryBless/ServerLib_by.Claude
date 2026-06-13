"""
Per-server asyncio TCP collector.

Each game server gets a persistent polling Task:
  1. Open TCP connection to host:adminPort (e.g. 9100)
  2. Every poll_interval_s seconds: send StatsRequest, read StatsResponse JSON
  3. Update shared state on success
  4. On any error: mark server offline, wait RECONNECT_DELAY_S, retry

The collector never dies — it keeps reconnecting so the dashboard shows
the moment a server comes back online.
"""
from __future__ import annotations

import asyncio
import logging

from .protocol import encode_stats_request, recv_stats_response
from . import state as _state

logger = logging.getLogger(__name__)

RECONNECT_DELAY_S = 5.0  # seconds between reconnect attempts after a failure


async def _poll_server(
    name: str, host: str, port: int, interval_s: float
) -> None:
    """
    Persistent polling loop for one game server admin port.

    Never returns normally — only exits on asyncio.CancelledError.
    """
    request_bytes = encode_stats_request()   # pre-encode once, reuse every tick

    while True:
        try:
            logger.info("[Collector] Connecting to %s (%s:%d)", name, host, port)
            reader, writer = await asyncio.open_connection(host, port)
            logger.info("[Collector] Connected to %s", name)

            try:
                while True:
                    # Send StatsRequest (4 bytes)
                    writer.write(request_bytes)
                    await writer.drain()

                    # Receive StatsResponse and cache it
                    snapshot = await recv_stats_response(reader)
                    _state.update_snapshot(name, snapshot)

                    await asyncio.sleep(interval_s)

            finally:
                writer.close()
                try:
                    await writer.wait_closed()
                except Exception:
                    pass  # ignore close errors

        except asyncio.CancelledError:
            # Propagate cancellation — this task is being shut down
            raise

        except Exception as exc:
            logger.warning(
                "[Collector] %s error: %r — retrying in %.0fs",
                name, exc, RECONNECT_DELAY_S,
            )
            _state.mark_offline(name)
            await asyncio.sleep(RECONNECT_DELAY_S)


async def start_all_collectors(
    servers_cfg: list[dict], poll_interval_s: float
) -> list[asyncio.Task]:
    """
    Start one background Task per server entry from servers.json.

    Returns the list of tasks so the caller can cancel them on shutdown.
    """
    tasks: list[asyncio.Task] = []
    for s in servers_cfg:
        task = asyncio.create_task(
            _poll_server(s['name'], s['host'], s['port'], poll_interval_s),
            name=f"collector-{s['name']}",
        )
        tasks.append(task)
    return tasks
