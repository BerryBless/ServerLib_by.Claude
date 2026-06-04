# 설계: RUDP 신뢰성 코어 (ACK + 재전송) + P4/P6/C5

**날짜:** 2026-06-05
**출처:** "RUDP 실제 배선" 요청 + 리뷰 권장 P4·P6·C5(`plan/perf_review_0604.md`)
**상태:** 설계 승인됨 → 구현 계획 대기
**범위(슬라이스 1/N):** 두 `RudpChannel` 간 신뢰 전송(누적 ACK + 재전송)을 실제 구현하고 P4/P6/C5를 해결한다. 서버 리스너·세션 통합·핸드셰이크는 **다음 사이클**(슬라이스 2).

## 배경 및 목적

현재 RUDP는 골격만 있고 "신뢰성"이 미구현이다(전부 dead code, `new RudpChannel` 호출처 0):
- ✅ 순서 재조립·중복 제거(`RudpRecvWindow`), 송신 큐(`RudpSendQueue`)
- ❌ ACK 처리: 헤더에 AckSeq를 *쓰기만* 하고 수신 루프가 피어 AckSeq를 안 읽음 → 전달 확인 없음
- ❌ 재전송: `SendLoop`이 첫 송신 후 버퍼 즉시 반납. `WithRetry`/`MaxRetries`/`RetransmitInterval`/`BuildAckBitmap` 미사용
- ❌ unacked 저장소 없음

리뷰 항목 위치: **P4**(`SendReliableAsync` 취소 경로 풀 누수), **P6**(`ReceiveLoop`의 수신당 `new byte[]`), **C5**(재전송 배선 시 버퍼 use-after-return).

**목표:** `RudpChannel`을 누적 ACK + 재전송으로 실제 신뢰성 있게 만들고, 그 과정에서 P4/P6/C5를 구조적으로 해결한다. 두 채널을 loopback UDP로 연결해 손실 주입 하에 E2E 검증한다.

**비목표(슬라이스 2로 이연):** 서버 `RudpListener`, `ISession` 통합, 핸드셰이크/연결 수립, `UdpHolePuncher` 통합, 선택적 ACK(SACK 비트맵), 혼잡 제어/RTO 추정.

## 설계 결정

| 항목 | 채택 | 근거 |
|------|------|------|
| ACK 방식 | 누적(cumulative) ExpectedSeq | 단순·정확, RecvWindow가 이미 ExpectedSeq 관리. SACK 비트맵은 슬라이스 보류 |
| 버퍼 소유 | `_unacked` 딕셔너리가 ACK까지 소유 | C5 해소(SendLoop이 반납 안 함) |
| 재전송 상태 갱신 | `ConcurrentDictionary.TryUpdate` CAS | 동시 ack 제거와의 경합에서 재삽입(이중반납/누수) 방지 |
| MaxRetries 소진 | **채널 fault/종료** | 순서 보장 채널 — 영구 손실 세그먼트는 채널을 "구멍"에서 wedge → 죽은 피어로 간주, fault |
| AckSeq 재기록 | 안 함(데이터 헤더에 작성 시점 값 유지) | 표준 ACK를 매 데이터 수신마다 보내므로 piggyback은 불필요한 최적화(YAGNI), 버퍼 불변 유지 |
| 순수 ACK 전송 | best-effort `TryWrite` | 수신 진행이 송신 백프레셔에 묶이지 않음. 누적 ACK은 다음 것이 커버하므로 드롭 무해 |

## 와이어 포맷

8B 헤더 `[Seq(4 LE)][AckSeq(4 LE)]` (변경 없음).
- **DATA**: `received > 8`. Seq=세그먼트 시퀀스, AckSeq=작성 시점 송신측 ExpectedSeq, 이어서 페이로드(≥1B). `SendReliableAsync`는 빈 페이로드를 거부(`ArgumentException`).
- **순수 ACK**: `received == 8`(헤더만). AckSeq=수신측 ExpectedSeq, Seq는 무시.

판별자: `received == HeaderSize` ⇒ 순수 ACK, `received > HeaderSize` ⇒ DATA.

## 컴포넌트 구조

```
ServerLib/Core/Rudp/
├─ RudpChannel.cs       재작성: _unacked 저장소, SendLoop(유일 UDP writer),
│                       RetransmitLoop, ProcessAck, ReceiveLoop(P6 풀링), 채널 fault
├─ RudpSendQueue.cs     RudpSegment 대신 SendRequest 운반; 버퍼-드레인 Dispose 제거
│                       (RudpSegment struct는 유지 — _unacked에서 사용)
└─ RudpRecvWindow.cs    변경 없음(OnReceive/ExpectedSeq 그대로 사용)
```

### SendRequest (신규, 작은 값 타입)
```csharp
internal readonly record struct SendRequest(uint Seq, bool IsAck);
```

### 송신측 상태·흐름 (RudpChannel)
- `private readonly ConcurrentDictionary<uint, RudpSegment> _unacked = new();` — seq→세그먼트(버퍼 소유). ACK/MaxRetries까지 보관.
- `_sendSeqRaw`(기존 Interlocked 카운터), `_sendQueue`(이제 `Channel<SendRequest>` 래핑).
- **`SendReliableAsync(payload, ct)`**:
  1. `if (payload.Length == 0) throw new ArgumentException(...)`.
  2. `seq = (uint)Interlocked.Increment(ref _sendSeqRaw) - 1`.
  3. `buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + payload.Length)`; 헤더 작성([seq, ExpectedSeq]); 페이로드 복사.
  4. `var seg = new RudpSegment(seq, buffer, total); _unacked.TryAdd(seq, seg);`
  5. **P4**: `bool handedOff = false; try { await _sendQueue.EnqueueAsync(new SendRequest(seq, false), ct); handedOff = true; } finally { if (!handedOff) { _unacked.TryRemove(seq, out _); ArrayPool<byte>.Shared.Return(buffer); } }`
- **`SendLoopAsync`**(유일 UDP 송신점): `SendRequest` dequeue.
  - `IsAck`: 8B 헤더 `[0, ExpectedSeq]`(stackalloc) UDP 송신.
  - 데이터: `if (_unacked.TryGetValue(seq, out var seg)) await _udp.SendAsync(seg.Buffer.AsMemory(0, seg.Length), ...);` (버퍼 미반납·미변경). 이미 acked면 skip.
  - 테스트 심: 송신 직전 `if (SendDropHookForTest?.Invoke(seq, isAck) == true) continue;`로 손실 주입.
- **`RetransmitLoopAsync`**(`PeriodicTimer(RetransmitInterval=100ms)`): `_unacked` 스냅샷 순회.
  - `now - seg.SentAt > RetransmitInterval`인 세그먼트:
    - `seg.RetryCount >= MaxRetries` → **FaultChannel**(아래).
    - 아니면 `if (_unacked.TryUpdate(seq, seg.WithRetry(), seg)) _sendQueue.TryEnqueue(new SendRequest(seq, false));` (CAS 성공 시에만 재전송 요청; 실패=동시 ack/변경 → skip). `WithRetry()`가 SentAt을 갱신해 타이머 재무장.

### 수신측 흐름
- **`ReceiveLoopAsync`**: 재사용 64KB 버퍼로 `ReceiveFromAsync`. `received < HeaderSize` → skip.
  - `ackSeq = ReadUint32(buf, 4); ProcessAck(ackSeq);` (항상)
  - `received > HeaderSize`(DATA): `seq = ReadUint32(buf, 0); if (_recvWindow.OnReceive(seq, out _) && OnReceived != null) { /* P6 */ }` 후 순수 ACK `TryEnqueue`(best-effort).
  - **P6**: `int len = received - HeaderSize; var payload = ArrayPool<byte>.Shared.Rent(len); try { buf.AsSpan(HeaderSize, len).CopyTo(payload); await OnReceived(payload.AsMemory(0, len)); } finally { ArrayPool<byte>.Shared.Return(payload); }`
- **`ProcessAck(uint ackSeq)`**: `_unacked.Keys` 스냅샷에서 `(int)(key - ackSeq) < 0`(wraparound-safe, 즉 key < ackSeq)인 키마다 `if (_unacked.TryRemove(key, out var seg)) ArrayPool<byte>.Shared.Return(seg.Buffer);` (exactly-once 승자 반납).

### 채널 fault (MaxRetries 소진)
`FaultChannel(Exception)`: 멱등(`Interlocked.Exchange(ref _faulted, 1)`). `await _cts.CancelAsync()`(루프 정지) → `_unacked` 전체 `TryRemove`+버퍼 반납 → `OnFaulted?.Invoke(ex)` 발화. 신규 멤버 `public Action<Exception>? OnFaulted { get; set; }`.

### Dispose
`DisposeAsync`: 기존 + `_unacked` 잔여 버퍼 전체 반납(딕셔너리 TryRemove 순회). `RudpSendQueue.Dispose`의 버퍼-드레인 제거(이제 버퍼 미보유 → `Writer.TryComplete()`만).

## P4/P6/C5 해결 매핑

- **P4**: `SendReliableAsync`의 `try/finally + handedOff` — enqueue 취소/예외 시 `_unacked`에서 제거하고 버퍼 반납. (race-free: EnqueueAsync 반환과 플래그 설정 사이 await 없음.)
- **P6**: `ReceiveLoop`이 `new byte[]` 대신 `ArrayPool` 대여 후 콜백 await 종료 시 반납(TCP 멀티세그먼트 경로와 동일 계약).
- **C5**: `SendLoop`이 버퍼를 반납하지 않음 — `_unacked`가 ACK/MaxRetries까지 소유. 재전송은 동일 버퍼를 다시 송신(use-after-return 불가능). 반납은 `ProcessAck`/`FaultChannel`/`Dispose`의 `TryRemove` 승자만.

## 테스트 (`RudpReliabilityTests`)

loopback UDP 두 `RudpChannel`(A↔B), 각자 상대 포트로 송신. `SendDropHookForTest`로 손실 주입.

1. **무손실 신뢰 전달**: A가 N개 페이로드 `SendReliableAsync` → B의 `OnReceived`가 순서대로 N개 수신.
2. **손실 후 재전송 회복**: B의 첫 수신 또는 A의 첫 송신(seq=0)을 1회 드롭 → 재전송으로 결국 전달(데드라인 내). 순서 보장 확인.
3. **ACK로 unacked 비워짐**: 전달·ack 완료 후 `_unacked.Count == 0`(테스트용 `internal` 노출) → 누수 없음.
4. **순서 재조립**: 비순차 도착(드롭+재전송) 시에도 `OnReceived`는 순서대로.
5. **MaxRetries fault**: 특정 seq를 영구 드롭하도록 훅 설정 → 재시도 소진 후 `OnFaulted` 발화 + `_unacked` 비워짐(버퍼 반납). (재시도 5회×100ms이므로 데드라인 ≥2s.)

각 테스트는 `.WaitAsync(deadline)`로 hang 방지. `internal` 멤버는 기존 `InternalsVisibleTo("ServerLib.Tests")` 활용.

## 변경 파일 목록

| 파일 | 종류 | 내용 |
|------|------|------|
| `ServerLib/Core/Rudp/RudpChannel.cs` | 재작성 | _unacked, SendLoop, RetransmitLoop, ProcessAck, ReceiveLoop(P6), FaultChannel, OnFaulted, 테스트 심 |
| `ServerLib/Core/Rudp/RudpSendQueue.cs` | 수정 | `Channel<SendRequest>`로 전환, `TryEnqueue` 추가, 버퍼-드레인 Dispose 제거 |
| `ServerLib.Tests/RudpReliabilityTests.cs` | 신규 | 신뢰 전달·재전송·ack·순서·fault 테스트 |

비변경: `RudpRecvWindow.cs`, 다른 transport/serialization, 인터페이스.

## 빌드 검증

```
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release
dotnet test  E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release   # 기존 52 + 신규 통과
```

## 향후 확장 포인트 (슬라이스 2+)

- 서버 `RudpListener`: UDP 소켓에서 원격 엔드포인트별로 datagram을 demux해 `RudpChannel` 생성·관리, `ISession` 구현/통합(레지스트리·브로드캐스트·디스패치).
- 핸드셰이크/연결 수립(SYN/SYN-ACK 또는 `UdpHolePuncher` 통합), 클라 `ConnectAsync`.
- 선택적 ACK(SACK, `BuildAckBitmap` 활용), RTO 동적 추정, 혼잡 제어.
