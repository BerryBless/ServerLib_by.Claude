# Heartbeat / RTT 측정 설계 문서

**날짜:** 2026-06-04
**상태:** 검토 대기

---

## 1. 배경 및 목적

현재 세션 관리는 `LastReceivedAt` 기반 수동적 유휴 감지만 제공한다. 앱 레벨에서 연결 지연(RTT)을 측정하거나 NAT 타임아웃을 능동적으로 회피할 수단이 없다. ProudNet급 게임서버는 클라이언트별 RTT를 상시 노출해 지연 보상·서버 선택·연결 품질 표시에 활용한다.

**해결 목표:**
- 클라이언트가 자동으로 주기적 PING을 송신하고, 서버가 자동으로 PONG을 회신하며, 클라이언트가 RTT를 자동 계산한다.
- 앱은 `client.Rtt`만 읽으면 된다. PING/PONG의 존재를 몰라도 된다.
- 하트비트 트래픽은 앱의 `OnReceived` 콜백에 노출되지 않는다(라이브러리 내부에서 가로챔).

**비목표(이번 범위 제외):**
- 서버 주도 핑, 양방향 핑
- 무응답 세션의 능동적 강제 종료 → 기존 `IdleTimeout`이 담당(PONG 미수신 시 LastReceivedAt 미갱신 → 유휴 타임아웃 자연 발동)
- RTT 평활값(EWMA), 서버 측 RTT 노출 → 향후 확장

---

## 2. 설계 결정

| 항목 | 채택안 | 비고 |
|------|--------|------|
| 주도 주체 | 클라이언트 주도 | 클라이언트가 PING, 서버는 PONG만 회신 |
| 자동화 수준 | 완전 자동 | 타이머·회신·RTT 계산 모두 라이브러리 내부 |
| 프로토콜 | 타임스탬프 에코 | PONG이 PING의 ClientTicks를 그대로 반사 → 서버 stateless |
| 가로채기 | 예약 패킷 ID | 서버/클라이언트 모두 수신 디스패치 단계에서 가로채 앱 콜백 미전달 |
| RTT 저장 | `long` ticks + Volatile | 기존 `_lastReceivedAtTicks` 패턴 일치, thread-safe |
| 무응답 처리 | 기존 IdleTimeout 재사용 | 별도 종료 로직 추가 안 함 |

---

## 3. 프로토콜 & 패킷

예약 패킷 ID (앱 패킷과 충돌 방지 — 상위 영역 예약):

```
PingPacket  Id = 0xFFFE  — 본문: long ClientTicks (송신 시각 DateTimeOffset.UtcNow.UtcTicks)
PongPacket  Id = 0xFFFF  — 본문: long ClientTicks (PING 값 그대로 에코)
```

**흐름:**
```
[Client] PingInterval 타이머 만료
   → PingPacket{ClientTicks = UtcNow.UtcTicks} 송신
   │
[Server] SocketPipelineSession 디스패치에서 PacketId == PingPacket.Id 감지
   → PongPacket{동일 ClientTicks} 즉시 회신, OnReceived 미호출(앱에 미노출)
   │
[Client] SocketPipelineClient 디스패치에서 PacketId == PongPacket.Id 감지
   → Rtt = UtcNow.UtcTicks - ClientTicks, _rttTicks 갱신, OnReceived 미호출
```

**stateless 보장:** 서버는 PING의 타임스탬프를 그대로 반사만 하므로 세션별 핑 상태를 저장하지 않는다.

---

## 4. 컴포넌트 구조

```
ServerLib/
├── Interface/
│   └── IClientConnection.cs          ← MOD: Rtt, PingInterval 추가
└── Core/
    ├── Serialization/Packets/
    │   ├── PingPacket.cs              ← NEW (struct, body = long)
    │   └── PongPacket.cs              ← NEW (struct, body = long)
    └── Transport/
        ├── SocketPipelineSession.cs   ← MOD: PING 가로채 PONG 회신
        └── SocketPipelineClient.cs    ← MOD: PING 타이머 + PONG 가로채 RTT 계산

ServerLib.Tests/
└── HeartbeatTests.cs                 ← NEW: 직렬화·RTT 계산·가로채기 테스트

Client/Program.cs / Server/Program.cs ← MOD: Rtt 출력 예제(선택)
```

---

## 5. 핵심 API

### IClientConnection 추가 멤버

```csharp
/// <summary>자동 하트비트 PING 송신 주기입니다. null이면 하트비트 비활성(기본값).</summary>
/// <remarks>ConnectAsync 호출 전에 설정해야 합니다.</remarks>
TimeSpan? PingInterval { get; set; }

/// <summary>마지막으로 측정된 왕복 지연(RTT)입니다. 측정 전에는 TimeSpan.Zero.</summary>
/// <remarks>Thread-safe (Volatile read). PONG 수신 시마다 갱신됩니다.</remarks>
TimeSpan Rtt { get; }
```

### PingPacket / PongPacket (struct, zero-alloc)

```csharp
public struct PingPacket : IPacket
{
    public const ushort Id = 0xFFFE;
    public long ClientTicks;
    public ushort PacketId => Id;
    public int GetBodySize() => 8;
    public void Serialize(ref SpanWriter w) => w.WriteInt64(ClientTicks);
    public void Deserialize(ref SpanReader r) => ClientTicks = r.ReadInt64();
}
// PongPacket: Id = 0xFFFF, 동일 구조
```

### SocketPipelineSession (서버 측 가로채기)

`DispatchPacketAsync`에서 본문 디스패치 전에:
```csharp
// 예약 ID 가로채기: PING이면 PONG 회신 후 종료(앱 OnReceived 미호출)
if (packetId == PingPacket.Id)
{
    await SendPongAsync(pingBody); // ClientTicks 에코
    return;
}
```

### SocketPipelineClient (클라이언트 측)

- `ConnectAsync` 성공 후 `PingInterval` 설정 시 `PingLoopAsync` fire-and-forget 시작.
- 수신 디스패치에서 `PongPacket.Id` 가로채 `Rtt` 계산, 앱 미전달.

```csharp
private long _rttTicks;
public TimeSpan Rtt => new TimeSpan(Volatile.Read(ref _rttTicks));

private async Task PingLoopAsync(TimeSpan interval, CancellationToken ct)
{
    using var timer = new PeriodicTimer(interval);
    while (await timer.WaitForNextTickAsync(ct))
        await SendPingAsync(DateTimeOffset.UtcNow.UtcTicks);
}
// PONG 수신 시: Volatile.Write(ref _rttTicks, DateTimeOffset.UtcNow.UtcTicks - echoedTicks)
```

---

## 6. 동시성 & 메모리

- **RTT 저장:** `long _rttTicks` + `Volatile.Read/Write` (단일 writer=수신 루프, 다중 reader=앱). 기존 `_lastReceivedAtTicks` 패턴 일치.
- **PING/PONG 직렬화:** struct 패킷이라 역직렬화 zero-alloc. 8바이트 본문은 `ArrayPool` 또는 기존 `PacketPool.RentSendBuffer`로 송신 버퍼 대여.
- **타이머:** `PeriodicTimer`(커널 스케줄러 기반), fire-and-forget + `_cts` 취소로 수명 관리.
- **가로채기 경로:** 예약 ID 비교는 헤더 파싱 직후 `ushort` 비교 1회 — hot path 비용 무시 가능.

---

## 7. 테스트 케이스

| 테스트명 | 검증 내용 |
|---------|----------|
| `PingPacket_RoundTrip_PreservesTicks` | PING 직렬화→역직렬화 시 ClientTicks 보존 |
| `PongPacket_RoundTrip_PreservesTicks` | PONG 동일 |
| `Server_OnPing_RepliesPongWithSameTicks` | 서버가 PING 받으면 동일 ticks PONG 회신 |
| `Server_OnPing_DoesNotInvokeAppOnReceived` | PING은 앱 OnReceived로 전달 안 됨 |
| `Client_OnPong_ComputesRtt` | PONG 수신 시 Rtt > 0 계산 |
| `Client_OnPong_DoesNotInvokeAppOnReceived` | PONG은 앱 OnReceived로 전달 안 됨 |
| `PingInterval_Null_NoPingSent` | 비활성 시 PING 미송신 |

> 서버/클라이언트 가로채기 테스트는 실제 소켓 통합 하네스가 없으므로, 디스패치 로직을 테스트 가능한 형태(헬퍼 메서드 또는 stub 주입)로 노출해 단위 검증한다.

---

## 8. 변경 파일 목록

| 파일 | 유형 | 핵심 변경 |
|------|------|----------|
| `ServerLib/Core/Serialization/Packets/PingPacket.cs` | 신규 | struct, Id=0xFFFE, long body |
| `ServerLib/Core/Serialization/Packets/PongPacket.cs` | 신규 | struct, Id=0xFFFF, long body |
| `ServerLib/Interface/IClientConnection.cs` | 수정 | `PingInterval`, `Rtt` 추가 |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | PING 가로채 PONG 회신 |
| `ServerLib/Core/Transport/SocketPipelineClient.cs` | 수정 | PING 타이머 + PONG 가로채 RTT 계산 |
| `ServerLib.Tests/HeartbeatTests.cs` | 신규 | 단위 테스트 |
| `Client/Program.cs` | 수정(선택) | `client.Rtt` 주기 출력 예제 |

---

## 9. 빌드 검증

```bash
dotnet build ClaudeCodeStudy.sln
dotnet test ServerLib.Tests --logger "console;verbosity=normal"
```

수동: Client 연결 후 콘솔에 `RTT=..ms`가 주기적으로 갱신되는지 확인.

---

## 10. 향후 확장 포인트

- **EWMA 평활 RTT** — 순간값 외 이동평균(`SmoothedRtt`) 제공
- **서버 측 RTT 노출** — 서버가 PONG 왕복을 측정하려면 양방향 핑으로 확장
- **무응답 강제 종료** — 연속 N회 PONG 누락 시 즉시 kick (현재는 IdleTimeout 의존)
- **하트비트 통계** — per-session 핑 송수신 카운트를 세션별 통계와 통합
