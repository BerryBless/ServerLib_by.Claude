# 보안 감사 리포트 — 해킹 / DDoS 공격 표면

- **대상:** `ServerLib` (.NET 10 고성능 TCP 서버 라이브러리)
- **일자:** 2026-06-09
- **범위:** A(원격 크래시 / 악성 입력), B(자원 고갈 / DDoS) 전체. **코드 변경 없는 감사 전용 리포트.**
- **방법:** 네트워크 경계(리스너·세션·역직렬화·디스패치) 정적 분석 + 코드 인용 검증.

---

## 1. 요약 (Executive Summary)

**한 줄 결론:** 메모리·성능 최적화는 우수하나, **네트워크 경계의 악성 입력 검증과 자원 고갈 방어가 사실상 부재**하여 현재 상태로는 인터넷 노출 프로덕션에 부적합하다. 단일 악성 클라이언트가 (1) 패킷 하나로 세션 처리 루프를 죽이거나 (2) 연결·trickle 공격으로 메모리/세션을 고갈시킬 수 있다.

| 위험도 | 개수 | 항목 |
|--------|------|------|
| 매우 높음 | 3 | B1 동시연결 무제한, B2 IP·rate 제한 없음, B3 slowloris 회피 |
| 높음 | 4 | A1 디스패처 OOB, A2 SpanReader 무검증, A3 핸들러 예외 미처리, B4 인증 전 자원 선할당 |
| 양호/주의 | C | 백프레셔·송신 타임아웃·헤더 상한·RUDP 바운드 큐는 방어됨 |

**프로덕션 배포 전 필수 차단 항목:** A1, A2, A3, B1, B3.

---

## 2. 범위 및 방법

- **감사 대상 파일**
  - `ServerLib/Core/Transport/SocketPipelineListener.cs` (accept·idle sweep·종료)
  - `ServerLib/Core/Transport/SocketPipelineSession.cs` (수신/송신 루프·프레이밍)
  - `ServerLib/Core/Serialization/SpanReader.cs` (역직렬화 디코더)
  - `ServerLib/Core/Rpc/RpcDispatcher.cs` (패킷 ID 라우팅)
  - `ServerLib/Core/Memory/PacketPool.cs` (헤더 파싱·검증)
  - `ServerLib/Core/SessionRegistry.cs`, `ServerLib/Core/Rudp/RudpSendQueue.cs` (참조)
- **분석 관점:** (1) 공격자가 보낸 바이트가 예외/크래시를 일으키는가, (2) 적은 비용으로 서버 자원(메모리·세션·소켓)을 고갈시킬 수 있는가.
- **제외:** TLS/암호화·인증 프로토콜 설계, 애플리케이션 로직(`GameContext` 등), 실제 침투 테스트. 본 리포트는 **코드를 수정하지 않는다.**

---

## 3. A. 원격 크래시 / 악성 입력

> 공통 영향: 아래 A1·A2는 모두 **A3(예외 미처리)** 와 결합하면 단일 악성 패킷으로 해당 세션의 수신 루프가 영구 정지(좀비 세션)된다.

### A1. RpcDispatcher 배열 인덱스 범위 초과 — `IndexOutOfRangeException`

- **위치:** `ServerLib/Core/Rpc/RpcDispatcher.cs:12,20,28-29`

```csharp
public RpcDispatcher(int maxPacketId = 256)
{
    _handlers = new Func<...>?[maxPacketId];   // 기본 256칸
}
...
var packetId = (ushort)(payload.Span[0] | (payload.Span[1] << 8)); // 0~65535
var handler = _handlers[packetId];            // packetId >= 256 → 범위 초과
if (handler == null) return;
```

- **재현:** 공격자가 패킷 ID = 256~65535 범위의 패킷을 **1개** 전송. `_handlers[packetId]` 접근 시 `IndexOutOfRangeException`. `Register(ushort, ...)`(라인 20)도 `packetId >= maxPacketId`면 동일하게 던지지만 이는 등록 시점(서버 측)이라 공격 표면은 `DispatchAsync`다.
- **영향:** RpcDispatcher를 사용하는 경로에서 디스패치 호출자가 예외를 잡지 않으면(→ A3) 해당 세션 수신 루프 종료. 세션 단위.
- **위험도:** 높음.
- **권장 방향(구현 아님):** 디스패치 진입부에 `if (packetId >= _handlers.Length) return;` 가드 추가(미등록 ID와 동일하게 무시). 또는 알 수 없는 ID 로깅 후 세션 종료 정책 선택.

### A2. SpanReader 바운드 체크 부재 — `IndexOutOfRange/ArgumentOutOfRange`

- **위치:** `ServerLib/Core/Serialization/SpanReader.cs:38,102-108,120-129`

```csharp
public byte ReadByte() => _buffer[_position++];          // 범위 검사 없음
...
public ReadOnlySpan<byte> ReadBytes(int length)
    => _buffer.Slice(_position, length);                  // length가 Remaining 초과면 예외
...
public string ReadString()
{
    ushort byteCount = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
    _position += 2;
    var value = Encoding.UTF8.GetString(_buffer.Slice(_position, byteCount)); // byteCount > Remaining → 예외
    ...
}
```

- **재현:** 헤더의 `BodyLength`만큼은 채워 보내되, **본문 내부의 필드 길이(예: `ReadString`의 2바이트 `byteCount`)를 실제 남은 바이트보다 크게** 선언. `_buffer.Slice(_position, byteCount)`가 범위를 벗어나 예외. `ReadString`은 정상 경로에서도 힙 할당이 있어(주석 명시) 작은 본문에 다수 문자열을 욱여넣는 할당 증폭도 가능.
- **참고:** 패킷 **전체 길이**는 `TryReadPacket`이 `buffer.Length < totalLength`로 보장하므로 "헤더 길이 < 실제 본문" 절단은 막힌다. 그러나 **본문 내부 가변 길이 필드**는 SpanReader가 검증하지 않는다 — 이것이 실제 구멍이다.
- **영향:** A3와 결합 시 세션 루프 종료. 세션 단위.
- **위험도:** 높음.
- **권장 방향:** 각 `Read*`에서 `if (Remaining < n) throw new EndOfStreamException();`류 명시적 가드, 또는 `TryRead*` 패턴 도입. 가변 길이(`ReadBytes`/`ReadString`)는 `length <= Remaining` 선검사 필수.

### A3. 수신/디스패치 루프의 예외 미처리 — 좀비 세션

- **위치:** `ServerLib/Core/Transport/SocketPipelineSession.cs:155-188`

```csharp
while (!ct.IsCancellationRequested)
{
    var result = await reader.ReadAsync(ct);
    ...
    while (TryReadPacket(ref buffer, out var packet, out var packetId))
    {
        await DispatchPacketAsync(packet, packetId);   // 여기서 던지면 루프 탈출
        consumed = buffer.Start;
    }
    reader.AdvanceTo(consumed, examined);
    if (result.IsCompleted) break;
}
}
catch (OperationCanceledException) { }   // 취소만 처리, 나머지 예외는 통과 안 됨
finally { await reader.CompleteAsync(); if (OnDisconnected != null) await OnDisconnected(); }
```

- **재현:** A1·A2 또는 애플리케이션 `OnReceived`/RPC 핸들러가 던지는 모든 비-취소 예외. `catch`는 `OperationCanceledException`만 받으므로 그 외 예외는 `ReadPipeAsync`(fire-and-forget `_ = ReadPipeAsync(...)`) 밖으로 전파 → **관측되지 않는 Task 예외**가 되고, 해당 세션의 수신은 영구 중단된다.
- **영향:** `finally`에서 `OnDisconnected`는 발화하나, 핸들러가 소켓을 닫지 않으면 좀비 연결이 idle sweep 전까지 잔존(B3와 결합 시 sweep도 회피 가능). 세션 단위지만, 핸들러 예외가 광범위하면 다수 세션 동시 사망 가능.
- **위험도:** 높음.
- **권장 방향:** `DispatchPacketAsync`(또는 개별 패킷 처리) 주위에 try/catch를 두어 **패킷 단위로 예외를 격리**하고, 정책에 따라 (a) 해당 패킷만 버리고 계속 또는 (b) 세션을 명시적·정상 종료(소켓 Dispose 포함). 어떤 경우든 루프 자체가 미관측 예외로 죽지 않게 한다.

---

## 4. B. 자원 고갈 / DDoS

### B1. 애플리케이션 레벨 동시 연결 수 상한 없음

- **위치:** `SocketPipelineListener.cs:106`(`Listen(512)`), `AcceptLoopAsync`(accept 후 무조건 `_activeSessions[id]=session`)

```csharp
_listenSocket.Listen(backlog: 512);   // 커널 SYN 큐 — 앱 큐가 아님
...
_activeSessions[session.SessionId] = session;  // 상한 검사 없음
```

- **재현:** 클라이언트가 연결을 계속 수립. backlog(512)는 커널의 수락 대기 큐일 뿐, accept된 연결은 모두 `SocketPipelineSession`(+ Pipe 버퍼)로 메모리에 적재된다. N만 연결 → N×(세션 객체 + Pipe 버퍼) 메모리.
- **영향:** 메모리·핸들 고갈 → 서버 전체 OOM/불안정. 프로세스 단위.
- **위험도:** 매우 높음.
- **권장 방향:** accept 직후 `if (_activeSessions.Count >= MaxConnections) { clientSocket.Close(); continue; }` 형태의 상한, 설정 노출(`ServerConfig`).

### B2. IP당 연결 제한 · rate limiting 전무

- **위치:** 리스너 전반 (필터링 로직 없음). `OnClientConnected`는 사후 콜백.
- **재현:** 단일 출발지 IP에서 수만 연결 또는 초당 수천 패킷 폭주. 어떤 계층에서도 거부되지 않는다.
- **영향:** B1을 단일 IP로도 달성. 패킷 폭주 시 디스패치/스레드풀 포화. 프로세스 단위.
- **위험도:** 매우 높음.
- **권장 방향:** IP→카운터 `ConcurrentDictionary`로 IP당 동시연결·신규연결 속도 제한, 또는 명시적으로 **네트워크 계층(LB/WAF/iptables/방화벽)에 위임**한다는 결정과 문서화. 라이브러리 단독 방어는 한계가 있으므로 계층 선택을 명확히 할 것.

### B3. Slowloris / Slow-read — idle 타임아웃 회피

- **위치:** `SocketPipelineSession.cs:138` + `SocketPipelineListener.cs:110-111,134-176`

```csharp
// FillPipeAsync: 수신 바이트가 1바이트라도 있으면 갱신
Volatile.Write(ref _lastReceivedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
...
// 리스너: idle sweep은 IdleTimeout이 설정된 경우에만 시작
if (_idleTimeout.HasValue)
    _ = IdleSweepLoopAsync(_idleTimeout.Value, _cts.Token);
// sweep 기준: now - LastReceivedAt > timeout
```

- **재현:**
  1. `IdleTimeout` 미설정 시(`_idleTimeout`은 nullable, 기본 null) sweep 루프 자체가 시작되지 않아 **좀비 세션이 무한 잔존**한다. (예제는 `appsettings.json`에서 30초를 주입하지만, 라이브러리 기본은 무방비.)
  2. `IdleTimeout` 설정 시에도 `LastReceivedAt`이 **모든 수신 바이트마다** 갱신되므로, 공격자가 `timeout`보다 짧은 주기로 **1바이트씩** 흘리면 sweep 기준을 영원히 통과한다. 동시에 미완성 패킷이 Pipe에 계속 점유된다. (라이브러리 XML 주석도 "최소 1바이트 전송으로 타임아웃 회피 가능"을 명시.)
  3. accept→첫 바이트 도착 타임아웃이 없어, 연결만 맺고 침묵하는 연결도 (1)의 경우 무한 잔존.
- **영향:** 적은 대역폭으로 세션 슬롯·메모리 고갈. 프로세스 단위.
- **위험도:** 매우 높음.
- **권장 방향:**
  - **절대 수명/무진척 기준** 도입: `LastReceivedAt`(바이트 기준) 대신 또는 추가로 `ConnectedAt` 기반 절대 타임아웃, 그리고 "마지막 **완전한 패킷** 수신 시각" 기준의 진척(progress) 타임아웃.
  - accept→첫 패킷 타임아웃(handshake deadline) 추가.
  - `IdleTimeout`의 안전한 기본값 제공 또는 미설정 시 경고.

### B4. 인증/검증 이전에 자원 선할당

- **위치:** `SocketPipelineListener.AcceptLoopAsync` — accept 직후 `new SocketPipelineSession(...)` → `_pipe = new Pipe(...)` 생성·등록 후에야 `OnClientConnected` 발화.
- **재현:** 인증 콜백이 거부하더라도 그 시점엔 이미 세션 객체·Pipe 버퍼가 할당·등록됨. 미인증 연결 폭주 시 거부 비용보다 할당 비용이 먼저 든다.
- **영향:** B1/B2 증폭. 프로세스 단위.
- **위험도:** 높음.
- **권장 방향:** accept 직후 IP/연결수 등 **저비용 선검사 → 통과분만 세션 생성**. 인증 전 제한 상태(half-open: 핸드셰이크 패킷만 허용, 버퍼 최소)와 인증 후 정상 상태를 분리.

---

## 5. C. 양호 / 주의 (이미 방어되는 항목)

오탐과 중복 작업 방지를 위해 **이미 안전한** 부분을 명시한다.

- **Pipe 백프레셔:** `SocketPipelineSession.cs:142` `await writer.FlushAsync(ct)`가 reader가 느리면 수신을 멈춰 단일 세션 Pipe의 무한 증가를 막는다(기본 ~64KB). → 단일 느린 reader發 메모리 폭주는 방어됨. 단 B3의 trickle은 미완성 패킷을 무기한 점유하므로 별개.
- **송신 경로:** `SemaphoreSlim` 송신 게이트 + 세션별 재사용 `SendTimeout` CTS(예제 30초)로 느린 클라이언트의 송신 게이트 영구 점유를 차단. `SessionRegistry.BroadcastAsync`는 `SocketException`/`ObjectDisposedException`을 격리해 1세션 실패가 브로드캐스트 전체를 막지 않는다.
- **헤더 길이 상한:** `PacketPool`의 `BodyLength`는 `ushort`(≤65535)로 강제되고 `WriteHeader`/`Deserialize`에 가드가 있어 2GB류 거대 길이 공격은 불가. (단 본문 내부 필드 길이는 A2에서 별도 미검증.)
- **RUDP 송신 큐:** `RudpSendQueue`는 `Channel.CreateBounded`(용량 1024, `FullMode.Wait`)로 무한 적재를 방지. 단, RUDP 경로는 dead code일 수 있음(`memory/perf_review_2026-06-04` 참고) — 활성 경로인지 별도 확인 권장.
- **종료/정리:** `Stop()`은 CTS 취소 + 활성 세션 동기 정리. `DisposeAsync`는 `Interlocked.Exchange` 멱등 + 컨텍스트 null화. idle sweep은 콜백 예외를 격리(`H1/H7`).

---

## 6. 종합 위험 매트릭스

| ID | 항목 | 상태 | 위험도 | 영향 범위 |
|----|------|------|--------|-----------|
| A1 | RpcDispatcher 인덱스 OOB | ✗ 미방어 | 높음 | 세션 |
| A2 | SpanReader 본문 필드 무검증 | ✗ 미방어 | 높음 | 세션 |
| A3 | 수신/디스패치 예외 미처리 | ✗ 미방어 | 높음 | 세션(다발 가능) |
| B1 | 동시 연결 수 상한 없음 | ✗ 미방어 | 매우 높음 | 프로세스 |
| B2 | IP당 제한·rate limit 없음 | ✗ 미방어 | 매우 높음 | 프로세스 |
| B3 | slowloris/idle 회피 | ✗ 미방어 | 매우 높음 | 프로세스 |
| B4 | 인증 전 자원 선할당 | ✗ 미방어 | 높음 | 프로세스 |
| C-1 | Pipe 백프레셔 | ✓ 방어 | — | — |
| C-2 | 송신 타임아웃·게이트·브로드캐스트 격리 | ✓ 방어 | — | — |
| C-3 | 헤더 길이 상한 | ✓ 방어 | — | — |
| C-4 | RUDP 바운드 큐 | ✓ 방어(주의) | — | — |

---

## 7. 우선순위 권고

> 본 리포트는 감사 전용이다. 아래는 다음 사이클에서 수정 여부·방법을 결정할 때의 권고 순서이며, 코드는 변경하지 않았다.

1. **프로덕션 배포 전 필수(차단 수준):**
   - **A1** 디스패처 ID 범위 가드 — 한 줄 수정, 즉시 효과.
   - **A2** SpanReader 가변 길이 필드 바운드 체크.
   - **A3** 패킷 단위 예외 격리(루프 사망 방지) — A1·A2의 안전망이자 독립적으로도 필수.
   - **B1** 동시 연결 상한.
   - **B3** 진척/절대 수명 타임아웃 + accept 핸드셰이크 데드라인.
2. **권장:** **B2**(IP당 제한·rate limit; 라이브러리 vs 네트워크 계층 책임 분담 결정), **B4**(인증 전 선검사·half-open 상태).
3. **선택/심층 방어:** Pipe `pauseWriterThreshold` 명시 설정, per-handler 타임아웃, 알 수 없는 패킷 ID 로깅/차단 정책.

---

## 8. 부록 — 검증 파일·라인 인덱스

| 항목 | 파일 | 라인 |
|------|------|------|
| A1 | `ServerLib/Core/Rpc/RpcDispatcher.cs` | 12, 20, 28-29 |
| A2 | `ServerLib/Core/Serialization/SpanReader.cs` | 38, 102-108, 120-129 |
| A3 | `ServerLib/Core/Transport/SocketPipelineSession.cs` | 155-188 |
| B1 | `ServerLib/Core/Transport/SocketPipelineListener.cs` | 106, 192-227 (AcceptLoop) |
| B3 | `SocketPipelineSession.cs` / `SocketPipelineListener.cs` | 138 / 110-111, 134-176 |
| B4 | `SocketPipelineListener.cs` | AcceptLoop(accept 직후 세션 생성) |
| C-1 | `SocketPipelineSession.cs` | 142 |
| C-3 | `ServerLib/Core/Memory/PacketPool.cs` | 헤더 파싱/`WriteHeader` 가드 |
| C-4 | `ServerLib/Core/Rudp/RudpSendQueue.cs` | 31, 42-44 |
