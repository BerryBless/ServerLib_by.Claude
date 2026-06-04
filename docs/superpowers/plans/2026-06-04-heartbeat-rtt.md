# Heartbeat / RTT 측정 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 클라이언트가 자동으로 주기적 PING을 보내고 서버가 자동 PONG을 회신하며 클라이언트가 RTT를 자동 계산하여 `client.Rtt`로 노출한다.

**Architecture:** PING/PONG을 예약 패킷 ID(0xFFFE/0xFFFF) struct로 정의하고, 핑/퐁 직렬화·RTT 계산을 소켓과 무관한 순수 정적 헬퍼 `HeartbeatProtocol`로 추출하여 단위 테스트한다. `SocketPipelineSession`은 수신 디스패치에서 PING을 가로채 PONG을 회신(앱 콜백 미호출)하고, `SocketPipelineClient`는 `PeriodicTimer`로 PING을 송신하고 PONG을 가로채 RTT를 계산한다.

**Tech Stack:** .NET 10, xUnit 2.9.0, `PeriodicTimer`, `BinaryPacketSerializer`, `ArrayPool`, `Volatile`

---

## File Map

| 경로 | 유형 | 역할 |
|------|------|------|
| `ServerLib/Core/Serialization/Packets/PingPacket.cs` | 신규 | struct, Id=0xFFFE, body=long ClientTicks |
| `ServerLib/Core/Serialization/Packets/PongPacket.cs` | 신규 | struct, Id=0xFFFF, body=long ClientTicks |
| `ServerLib/Core/Transport/HeartbeatProtocol.cs` | 신규 | 순수 정적 헬퍼: BuildPing/TryBuildPong/TryComputeRtt |
| `ServerLib/Interface/IClientConnection.cs` | 수정 | `PingInterval`, `Rtt` 추가 |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | PING 가로채 PONG 회신 |
| `ServerLib/Core/Transport/SocketPipelineClient.cs` | 수정 | PingInterval/Rtt 구현 + PING 타이머 + PONG 가로채 RTT |
| `ServerLib.Tests/HeartbeatTests.cs` | 신규 | 패킷 라운드트립 + 프로토콜 헬퍼 단위 테스트 |
| `Client/Program.cs` | 수정 | `client.Rtt` 출력 예제 |

> `internal` 헬퍼는 기존 `InternalsVisibleTo("ServerLib.Tests")`(csproj 설정 완료) 덕분에 테스트에서 접근 가능.

---

## Task 1: PingPacket / PongPacket struct + 라운드트립 테스트

**Files:**
- Create: `ServerLib/Core/Serialization/Packets/PingPacket.cs`
- Create: `ServerLib/Core/Serialization/Packets/PongPacket.cs`
- Create: `ServerLib.Tests/HeartbeatTests.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

`ServerLib.Tests/HeartbeatTests.cs` 신규 생성:

```csharp
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Tests;

public sealed class HeartbeatTests
{
    private static readonly BinaryPacketSerializer Serializer = new();

    [Fact]
    public void PingPacket_RoundTrip_PreservesClientTicks()
    {
        var ping = new PingPacket { ClientTicks = 1234567890L };
        Span<byte> buf = stackalloc byte[64];
        int written = Serializer.Serialize(ping, buf);
        var decoded = Serializer.Deserialize<PingPacket>(buf.Slice(0, written));

        Assert.Equal(PingPacket.Id, ping.PacketId);
        Assert.Equal(1234567890L, decoded.ClientTicks);
    }

    [Fact]
    public void PongPacket_RoundTrip_PreservesClientTicks()
    {
        var pong = new PongPacket { ClientTicks = 9876543210L };
        Span<byte> buf = stackalloc byte[64];
        int written = Serializer.Serialize(pong, buf);
        var decoded = Serializer.Deserialize<PongPacket>(buf.Slice(0, written));

        Assert.Equal(PongPacket.Id, pong.PacketId);
        Assert.Equal(9876543210L, decoded.ClientTicks);
    }

    [Fact]
    public void PingPacket_BodySize_Is8()
    {
        Assert.Equal(8, new PingPacket().GetBodySize());
        Assert.Equal(8, new PongPacket().GetBodySize());
    }
}
```

- [ ] **Step 2: 빌드 오류 확인 (Red — PingPacket 미존재)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ServerLib.Tests
```
Expected: `error CS0246: 'PingPacket' 형식 또는 네임스페이스 이름을 찾을 수 없습니다.`

- [ ] **Step 3: PingPacket 작성**

`ServerLib/Core/Serialization/Packets/PingPacket.cs`:

```csharp
namespace ServerLib.Core.Serialization.Packets;

/// <summary>하트비트 PING 패킷입니다. 클라이언트가 송신 시각(ticks)을 담아 보냅니다. 예약 ID 0xFFFE.</summary>
// struct 선택: 8바이트 본문의 초고빈도 제어 패킷이므로 역직렬화 시 new T()가 무할당(class였다면 매 핑 Gen0 압력).
public struct PingPacket : IPacket
{
    /// <summary>예약 패킷 ID. 앱 패킷과 충돌하지 않도록 상위 영역을 사용합니다.</summary>
    public const ushort Id = 0xFFFE;

    /// <summary>클라이언트가 PING을 송신한 시각(DateTimeOffset.UtcNow.UtcTicks).</summary>
    public long ClientTicks;

    public ushort PacketId => Id;
    public int GetBodySize() => 8;
    public void Serialize(ref SpanWriter writer) => writer.WriteInt64(ClientTicks);
    public void Deserialize(ref SpanReader reader) => ClientTicks = reader.ReadInt64();
}
```

- [ ] **Step 4: PongPacket 작성**

`ServerLib/Core/Serialization/Packets/PongPacket.cs`:

```csharp
namespace ServerLib.Core.Serialization.Packets;

/// <summary>하트비트 PONG 패킷입니다. 서버가 PING의 ticks를 그대로 반사합니다. 예약 ID 0xFFFF.</summary>
// struct 선택: 8바이트 본문의 초고빈도 제어 패킷이므로 역직렬화 시 new T()가 무할당.
public struct PongPacket : IPacket
{
    /// <summary>예약 패킷 ID.</summary>
    public const ushort Id = 0xFFFF;

    /// <summary>PING이 담아 보낸 클라이언트 송신 시각을 그대로 반사한 값입니다.</summary>
    public long ClientTicks;

    public ushort PacketId => Id;
    public int GetBodySize() => 8;
    public void Serialize(ref SpanWriter writer) => writer.WriteInt64(ClientTicks);
    public void Deserialize(ref SpanReader reader) => ClientTicks = reader.ReadInt64();
}
```

- [ ] **Step 5: 테스트 통과 확인 (Green)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --filter "Heartbeat" --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 6: 커밋**

```bash
git add ServerLib/Core/Serialization/Packets/PingPacket.cs ServerLib/Core/Serialization/Packets/PongPacket.cs ServerLib.Tests/HeartbeatTests.cs
git commit -m "추가: 하트비트 PingPacket/PongPacket 예약 패킷 struct"
```

---

## Task 2: HeartbeatProtocol 순수 헬퍼 + 테스트

**Files:**
- Create: `ServerLib/Core/Transport/HeartbeatProtocol.cs`
- Modify: `ServerLib.Tests/HeartbeatTests.cs`

- [ ] **Step 1: 실패하는 테스트 추가**

`ServerLib.Tests/HeartbeatTests.cs`의 클래스 끝(마지막 `}` 앞)에 추가. 또한 파일 상단 using에 `using ServerLib.Core.Transport;` 추가:

```csharp
    [Fact]
    public void BuildPing_ThenTryBuildPong_ProducesPongWithSameTicks()
    {
        Span<byte> pingBuf = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int pingLen = HeartbeatProtocol.BuildPing(5000L, pingBuf);

        Span<byte> pongBuf = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int pongLen = HeartbeatProtocol.TryBuildPong(pingBuf.Slice(0, pingLen), pongBuf);

        Assert.True(pongLen > 0);
        var pong = Serializer.Deserialize<PongPacket>(pongBuf.Slice(0, pongLen));
        Assert.Equal(5000L, pong.ClientTicks);
    }

    [Fact]
    public void TryBuildPong_NonPingPacket_ReturnsZero()
    {
        // IncrementPacket(Id=3) 직렬화 후 ping이 아님을 확인
        var inc = new IncrementPacket();
        Span<byte> buf = stackalloc byte[64];
        int len = Serializer.Serialize(inc, buf);

        Span<byte> pong = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int written = HeartbeatProtocol.TryBuildPong(buf.Slice(0, len), pong);

        Assert.Equal(0, written);
    }

    [Fact]
    public void TryComputeRtt_PongPacket_ReturnsElapsedTicks()
    {
        var pong = new PongPacket { ClientTicks = 1000L };
        Span<byte> buf = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int len = Serializer.Serialize(pong, buf);

        bool ok = HeartbeatProtocol.TryComputeRtt(buf.Slice(0, len), nowTicks: 1500L, out long rttTicks);

        Assert.True(ok);
        Assert.Equal(500L, rttTicks);
    }

    [Fact]
    public void TryComputeRtt_NonPongPacket_ReturnsFalse()
    {
        var inc = new IncrementPacket();
        Span<byte> buf = stackalloc byte[64];
        int len = Serializer.Serialize(inc, buf);

        bool ok = HeartbeatProtocol.TryComputeRtt(buf.Slice(0, len), nowTicks: 1500L, out _);

        Assert.False(ok);
    }
```

또한 파일 상단 using에 `using ServerLib.Core.Serialization.Packets;`가 이미 있는지 확인하고, `IncrementPacket` 사용을 위해 필요하면 추가한다(Task 1에서 이미 추가됨).

- [ ] **Step 2: 빌드 오류 확인 (Red — HeartbeatProtocol 미존재)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ServerLib.Tests
```
Expected: `error CS0103` 또는 `CS0246: 'HeartbeatProtocol'` 미존재.

- [ ] **Step 3: HeartbeatProtocol 작성**

`ServerLib/Core/Transport/HeartbeatProtocol.cs`:

```csharp
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Core.Transport;

/// <summary>
/// 하트비트 PING/PONG의 직렬화·RTT 계산을 소켓과 무관하게 처리하는 순수 정적 헬퍼입니다.
/// 세션(서버)과 클라이언트가 공유하며, 소켓 없이 단위 테스트 가능합니다.
/// </summary>
internal static class HeartbeatProtocol
{
    // PING/PONG은 헤더(4B) + long(8B) = 12B 고정 — 스택 버퍼 크기 상한으로 사용
    public const int MaxPacketSize = PacketPool.HeaderSize + 8;

    // 무상태 직렬화기 공유 (내부 상태 없음 → thread-safe)
    private static readonly BinaryPacketSerializer Serializer = new();

    /// <summary>PING 패킷을 dest에 직렬화하고 기록 바이트 수를 반환합니다.</summary>
    public static int BuildPing(long clientTicks, Span<byte> dest)
    {
        var ping = new PingPacket { ClientTicks = clientTicks };
        return Serializer.Serialize(ping, dest);
    }

    /// <summary>
    /// <paramref name="packet"/>이 PING이면 동일 ticks의 PONG을 <paramref name="dest"/>에 직렬화하고
    /// 기록 바이트 수를 반환합니다. PING이 아니면 0을 반환합니다.
    /// </summary>
    public static int TryBuildPong(ReadOnlySpan<byte> packet, Span<byte> dest)
    {
        if (!PacketPool.TryParseHeader(packet, out ushort id, out _) || id != PingPacket.Id)
            return 0;
        var ping = Serializer.Deserialize<PingPacket>(packet);
        var pong = new PongPacket { ClientTicks = ping.ClientTicks };
        return Serializer.Serialize(pong, dest);
    }

    /// <summary>
    /// <paramref name="packet"/>이 PONG이면 <paramref name="nowTicks"/> - 에코된 ClientTicks로
    /// RTT(ticks)를 계산해 true를 반환합니다. PONG이 아니면 false.
    /// </summary>
    public static bool TryComputeRtt(ReadOnlySpan<byte> packet, long nowTicks, out long rttTicks)
    {
        rttTicks = 0;
        if (!PacketPool.TryParseHeader(packet, out ushort id, out _) || id != PongPacket.Id)
            return false;
        var pong = Serializer.Deserialize<PongPacket>(packet);
        rttTicks = nowTicks - pong.ClientTicks;
        return true;
    }
}
```

- [ ] **Step 4: 테스트 통과 확인 (Green)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --filter "Heartbeat" --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/Core/Transport/HeartbeatProtocol.cs ServerLib.Tests/HeartbeatTests.cs
git commit -m "추가: HeartbeatProtocol 순수 헬퍼 (BuildPing/TryBuildPong/TryComputeRtt)"
```

---

## Task 3: IClientConnection 인터페이스 확장

**Files:**
- Modify: `ServerLib/Interface/IClientConnection.cs`

- [ ] **Step 1: PingInterval + Rtt 멤버 추가**

`IClientConnection.cs`의 `bool IsConnected { get; }` 프로퍼티 정의 다음(빈 줄 뒤, `OnConnected` XML 주석 앞)에 삽입:

```csharp

    /// <summary>
    /// 자동 하트비트 PING 송신 주기입니다. <see langword="null"/>이면 하트비트를 비활성화합니다(기본값).
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not thread-safe. <see cref="ConnectAsync"/> 호출 전에 설정해야 합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan? PingInterval { get; set; }

    /// <summary>
    /// 마지막으로 측정된 왕복 지연(RTT)입니다. 측정 전에는 <see cref="TimeSpan.Zero"/>입니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Volatile read로 최신값을 반환합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation (값 타입).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. PONG 수신 시마다 갱신됩니다.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan Rtt { get; }
```

- [ ] **Step 2: 빌드 (SocketPipelineClient 미구현으로 ServerLib 컴파일 실패 예상)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ServerLib
```
Expected: `error CS0535: 'SocketPipelineClient'이(가) 'IClientConnection.PingInterval'을(를) 구현하지 않습니다` (Task 5에서 구현). 이 오류는 정상이며 Task 5에서 해소된다.

> 주의: 이 태스크는 단독으로 컴파일되지 않는다. Task 5와 함께 커밋되어야 한다. 따라서 여기서는 커밋하지 않고 Task 5로 이어서 진행한다.

---

## Task 4: SocketPipelineSession — PING 가로채 PONG 회신

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineSession.cs`

- [ ] **Step 1: using 확인 및 DispatchPacketAsync 수정**

`SocketPipelineSession.cs`의 `DispatchPacketAsync` 메서드를 찾는다. 현재 형태:

```csharp
    private async ValueTask DispatchPacketAsync(ReadOnlySequence<byte> packet)
    {
        if (OnReceived == null) return;

        // Fast-path: ...
        if (packet.IsSingleSegment)
        {
            await OnReceived(packet.First);
        }
        else
        {
            ...
        }
    }
```

메서드 본문 맨 앞(`if (OnReceived == null) return;` 위)에 PING 가로채기 분기를 추가하고, PONG 빌드용 동기 헬퍼를 새로 추가한다. 수정 후 메서드와 헬퍼:

```csharp
    private async ValueTask DispatchPacketAsync(ReadOnlySequence<byte> packet)
    {
        // 예약 ID 가로채기: PING이면 PONG을 회신하고 앱 OnReceived는 호출하지 않는다.
        // (stackalloc이 await를 넘지 못하므로 동기 헬퍼에서 풀 버퍼로 빌드 후 여기서 송신)
        var pongBuf = TryBuildPongBuffer(packet, out int pongLen);
        if (pongBuf != null)
        {
            try { await SendAsync(pongBuf.AsMemory(0, pongLen)); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            finally { ArrayPool<byte>.Shared.Return(pongBuf); }
            return;
        }

        if (OnReceived == null) return;

        // Fast-path: 대부분의 패킷은 단일 세그먼트(Pipe 버퍼 내 연속 메모리)이므로
        // ArrayPool 대여 없이 First 슬라이스를 그대로 콜백에 넘긴다(무할당).
        if (packet.IsSingleSegment)
        {
            await OnReceived(packet.First);
        }
        else
        {
            // 세그먼트 경계에 걸친 드문 경우만 연속 버퍼로 병합 필요 → 영구 배열 할당 대신 ArrayPool 임대로 GC 압력 억제.
            var length = (int)packet.Length;
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                packet.CopyTo(rented);
                await OnReceived(rented.AsMemory(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented); // 풀에 반납하여 다음 멀티세그먼트 패킷이 재사용
            }
        }
    }

    // 동기 헬퍼: packet이 PING이면 PONG을 풀 버퍼에 빌드해 반환(written>0), 아니면 null.
    // stackalloc을 async 메서드(await 경계)에서 분리하기 위해 동기로 둔다.
    private static byte[]? TryBuildPongBuffer(ReadOnlySequence<byte> packet, out int written)
    {
        written = 0;
        if (packet.Length > HeartbeatProtocol.MaxPacketSize) return null; // 하트비트는 12B 고정, 더 크면 일반 패킷
        Span<byte> tmp = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int len = (int)packet.Length;
        packet.CopyTo(tmp);
        Span<byte> pong = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int w = HeartbeatProtocol.TryBuildPong(tmp[..len], pong);
        if (w == 0) return null;
        var buf = ArrayPool<byte>.Shared.Rent(w);
        pong[..w].CopyTo(buf);
        written = w;
        return buf;
    }
```

> 참고: `HeartbeatProtocol`은 같은 네임스페이스 `ServerLib.Core.Transport`에 있으므로 추가 using 불필요. `System.Buffers`(ArrayPool), `System.Net.Sockets`(SocketException)는 파일 상단에 이미 존재.

- [ ] **Step 2: 빌드 (ServerLib — 단, IClientConnection 미구현으로 여전히 실패 예상)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ServerLib
```
Expected: 여전히 `SocketPipelineClient`의 IClientConnection 미구현 오류만 남음(Task 5에서 해소). 세션 변경 자체에는 컴파일 오류가 없어야 함.

> 이 태스크도 단독 커밋하지 않고 Task 5와 함께 빌드를 통과시킨다.

---

## Task 5: SocketPipelineClient — PingInterval/Rtt + 타이머 + PONG 가로채기

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineClient.cs`

- [ ] **Step 1: 필드 + 프로퍼티 추가**

`SocketPipelineClient.cs`의 필드 영역(`private int _disposed;` 다음 줄)에 추가:

```csharp
    private long _rttTicks; // 마지막 RTT(ticks) — Volatile로 갱신/읽기
```

`IsConnected` 프로퍼티 정의 다음 줄에 추가:

```csharp
    public TimeSpan? PingInterval { get; set; }
    // Volatile.Read: 수신 루프(writer)와 앱(reader) 간 최신 RTT 가시성 보장
    public TimeSpan Rtt => new TimeSpan(Volatile.Read(ref _rttTicks));
```

- [ ] **Step 2: ConnectAsync에서 PING 루프 시작**

`ConnectAsync`에서 두 수신 루프를 시작하는 부분:

```csharp
        // fill/read 두 루프는 _cts로 자체 수명·취소를 관리하므로 await 없이 분리 구동(fire-and-forget)
        _ = FillPipeAsync(_cts.Token);
        _ = ReadPipeAsync(_cts.Token);
```

다음 줄에 추가:

```csharp
        // PingInterval이 설정된 경우에만 하트비트 루프 시작
        if (PingInterval.HasValue)
            _ = PingLoopAsync(PingInterval.Value, _cts.Token);
```

- [ ] **Step 3: PING 루프 + PONG 가로채기 메서드 추가**

`ReadPipeAsync` 메서드 정의 바로 앞에 두 메서드를 추가:

```csharp
    // 주기적으로 PING을 송신한다. 송신 버퍼는 1회 대여해 재사용(steady-state 무할당).
    private async Task PingLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        var buf = ArrayPool<byte>.Shared.Rent(HeartbeatProtocol.MaxPacketSize);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                int written = HeartbeatProtocol.BuildPing(DateTimeOffset.UtcNow.UtcTicks, buf);
                try { await SendAsync(buf.AsMemory(0, written), ct); }
                catch (ObjectDisposedException) { break; }
                catch (System.Net.Sockets.SocketException) { }
            }
        }
        catch (OperationCanceledException) { }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // 동기 헬퍼: packet이 PONG이면 RTT를 계산해 _rttTicks를 갱신하고 true. 아니면 false.
    private bool TryHandlePong(ReadOnlySequence<byte> packet)
    {
        if (packet.Length > HeartbeatProtocol.MaxPacketSize) return false;
        Span<byte> tmp = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int len = (int)packet.Length;
        packet.CopyTo(tmp);
        if (HeartbeatProtocol.TryComputeRtt(tmp[..len], DateTimeOffset.UtcNow.UtcTicks, out long rtt))
        {
            Volatile.Write(ref _rttTicks, rtt);
            return true;
        }
        return false;
    }
```

- [ ] **Step 4: ReadPipeAsync 수신 루프에 PONG 가로채기 삽입**

`ReadPipeAsync`의 패킷 처리 루프:

```csharp
                while (TryReadPacket(ref buffer, out var packet))
                {
                    if (OnReceived != null)
                    {
```

를 다음으로 교체(맨 앞에 PONG 가로채기 분기 추가):

```csharp
                while (TryReadPacket(ref buffer, out var packet))
                {
                    // 예약 ID 가로채기: PONG이면 RTT만 갱신하고 앱 OnReceived는 호출하지 않는다.
                    if (TryHandlePong(packet))
                    {
                        consumed = buffer.Start;
                        continue;
                    }
                    if (OnReceived != null)
                    {
```

> 참고: `HeartbeatProtocol`은 동일 네임스페이스라 using 불필요. `System.Buffers`(ArrayPool)는 파일 상단에 이미 존재. `PeriodicTimer`는 `System.Threading`(암시적 using).

- [ ] **Step 5: 전체 솔루션 빌드 (이제 Task 3·4·5가 모두 모여 컴파일 성공)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: 전체 테스트 (회귀 + 하트비트)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 36` (기존 29 + 하트비트 7)

- [ ] **Step 7: 커밋 (Task 3·4·5 통합)**

```bash
git add ServerLib/Interface/IClientConnection.cs ServerLib/Core/Transport/SocketPipelineSession.cs ServerLib/Core/Transport/SocketPipelineClient.cs
git commit -m "추가: 하트비트 자동화 — 서버 PONG 회신, 클라이언트 PING 타이머·RTT 계산"
```

---

## Task 6: Client/Program.cs 예제 + 최종 검증

**Files:**
- Modify: `Client/Program.cs`

- [ ] **Step 1: PingInterval 설정 추가**

`Client/Program.cs`의 `await using var conn = new SocketPipelineClient();` 줄을 다음 두 줄로 교체한다:

```csharp
    await using var conn = new SocketPipelineClient();
    conn.PingInterval = TimeSpan.FromSeconds(1); // 1초마다 자동 PING → RTT 측정
```

- [ ] **Step 2: 첫 스레드에서 RTT 주기 출력 추가**

같은 파일에서 `await conn.ConnectAsync(Host, Port, ct);` 줄을 찾아, 그 다음에 RTT 출력 백그라운드 태스크를 삽입한다(교체):

```csharp
    await conn.ConnectAsync(Host, Port, ct);

    if (i == 0)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
                Console.WriteLine($"  [T0] RTT={conn.Rtt.TotalMilliseconds:F1}ms");
            }
        });
    }
```

- [ ] **Step 3: 빌드 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\Client
```
Expected: `Build succeeded.`

- [ ] **Step 4: 커밋**

```bash
git add Client/Program.cs
git commit -m "수정: Client 예제에 PingInterval 설정 및 RTT 출력 추가"
```

- [ ] **Step 5: 최종 전체 검증**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Build succeeded. 0 Error(s)` + `Passed! - Failed: 0, Passed: 36`

- [ ] **Step 6: 수동 동작 확인 (선택)**

터미널 1: `dotnet run --project Server`
터미널 2: `dotnet run --project Client -- 1 0` (무한 송신)
클라이언트 콘솔에 `[T0] RTT=..ms`가 2초마다 출력되는지 확인.
