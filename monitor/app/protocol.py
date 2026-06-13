"""
Binary framing protocol for the game server admin port (port 9100).

Wire format: [PacketId (2B LE)] [BodyLength (2B LE)] [Body (N bytes)]

StatsRequest  (Id=8): PacketId=8, BodyLength=0  → 4 bytes total, no body
StatsResponse (Id=9): PacketId=9, BodyLength=N  → 4+N bytes, body = UTF-8 JSON

Protocol contract (mirrors C# PacketPool / StatsResponsePacket):
  - All integers: little-endian
  - Body is raw UTF-8 JSON bytes with NO length prefix
  - BodyLength (ushort, max 65535) in the 4-byte header covers the entire body
"""
import asyncio
import json
import struct

STATS_REQUEST_ID  = 8
STATS_RESPONSE_ID = 9
HEADER_SIZE       = 4   # 2B PacketId + 2B BodyLength


def encode_stats_request() -> bytes:
    """
    Encode a StatsRequest packet (Id=8, no body) as 4 bytes.

    Equivalent to C#:
        struct.pack('<HH', StatsRequestPacket.Id, 0)
    """
    return struct.pack('<HH', STATS_REQUEST_ID, 0)


async def read_exact(reader: asyncio.StreamReader, n: int) -> bytes:
    """
    Read exactly n bytes from reader, handling TCP fragmentation.

    Raises EOFError if the connection closes before n bytes are received.
    """
    buf = bytearray()
    while len(buf) < n:
        chunk = await reader.read(n - len(buf))
        if not chunk:
            raise EOFError(
                f"Connection closed while reading {n} bytes (received {len(buf)} so far)"
            )
        buf.extend(chunk)
    return bytes(buf)


async def recv_stats_response(reader: asyncio.StreamReader) -> dict:
    """
    Read one StatsResponse packet (Id=9) from the stream.

    Returns:
        Decoded JSON dict (the server stats snapshot).

    Raises:
        ValueError  — if the received packet ID is not StatsResponsePacket.Id (9).
        EOFError    — if the connection closes mid-packet.
        json.JSONDecodeError — if the body is not valid UTF-8 JSON.
    """
    header = await read_exact(reader, HEADER_SIZE)
    pkt_id, body_len = struct.unpack('<HH', header)

    if pkt_id != STATS_RESPONSE_ID:
        raise ValueError(
            f"Unexpected packet id {pkt_id} (expected StatsResponse={STATS_RESPONSE_ID})"
        )

    body = await read_exact(reader, body_len)
    return json.loads(body.decode('utf-8'))
