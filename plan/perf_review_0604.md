# 성능 우선 코드 리뷰 — ClaudeCodeStudy ServerLib (2026-06-04)

**우선순위:** ① 성능 ② 확정성(correctness)·편의성(API)
**방식:** 실측(BenchmarkDotNet) + 정적 분석 5개 에이전트 병렬(힙할당·풀링·성능·데드락·lock-free) + 수동 API 감사
**범위:** Hot path 집중 — `ServerLib/Core/Transport/*`, `Serialization/*`, `SessionRegistry`, `Memory/PacketPool`, `ServerMetrics`, `Rudp/*`, `Interface/*`

> **신뢰도 표기:** 🔴🔴 = 복수 에이전트가 독립적으로 동일 지적(고신뢰), 🔴 = 단일 에이전트.
> **상태:** 빌드 통과(exit 0). 실측은 마이크로벤치 2종(직렬화·헤더파싱·세션송신)으로 *기준선·검증* 용도이며, 진짜 hot path(FillPipe/ReadPipe IO 루프)는 마이크로벤치 불가 — 실행 가능한 per-line 발견은 정적 패스에서 확보.

---

## TL;DR — 영향 순 Top 5

| # | 발견 | 위치 | 영향 | 신뢰 |
|---|------|------|------|------|
| P1 | 디스패치 오프로드 부재 — IO 루프에서 핸들러를 패킷마다 inline await | `SocketPipelineSession.cs:119-123` | 처리량·스레드풀 점유 (HIGH) | 🔴🔴 |
| P2 | 패킷마다 `async ValueTask` 상태머신 박싱 | `SocketPipelineSession.cs:172` | 정상부하 GC 압력 (~1 alloc/packet) | 🔴 |
| P3 | `_sendGate`를 송신 전체 구간 점유 → 브로드캐스트 HOL·PONG 지연·죽은 피어에 무한 대기 | `SocketPipelineSession.cs:234-236` | 꼬리지연·가용성 (MED) | 🔴🔴 |
| P4 | RUDP 송신 취소 경로 ArrayPool 누수 → 풀 고갈 | `RudpChannel.cs:45-53` | 정확성→성능 퇴화 (HIGH) | 🔴🔴 |
| P5 | `BroadcastAsync` 호출마다 `Values.ToArray()` 스냅샷 할당 | `SessionRegistry.cs:39` | 브로드캐스트 GC (MED/LOW) | 🔴🔴 |

핵심 결론: **프레이밍/직렬화 레이어는 매우 잘 설계됨**(진짜 zero-copy 수신, 올바른 `AdvanceTo`, lock-free 상태, pooled 헤더, NoDelay). 남은 성능 리스크는 대부분 **핸들러를 IO 루프에서 분리하지 않은 것(P1)** 에서 파생되며, 이것이 #1 처리량 이슈이자 #1 GC 이슈(P2)의 뿌리다.

---

## 실측 (Empirical)

> *(벤치 실행 완료 후 채워짐 — 아래 섹션 참조)*

### Microbenchmark — `Benchmark/PacketSerializerBenchmark.cs`
- **인프라 이슈 (선행 단계에서 발견):** 소스에 `[SimpleJob(RuntimeMoniker.Net90)]`가 박혀 있으나 프로젝트는 `net10.0`. BDN 0.14.0이 net9 툴체인 프로젝트를 생성하면 net10 어셈블리 참조에 실패할 수 있음. 본 실측은 소스 미수정 + `--inProcess --job short`로 net10 호스트에서 직접 실행하여 우회. **권장 수정:** 모니커를 `Net10_0`로 상향하거나 `[SimpleJob]` 제거 후 in-process 고정.

| Method | Mean | Allocated |
|--------|------|-----------|
| _(채워짐)_ | | |

### LoadTest — `LoadTest/DummyClient.cs`
- **실행 보류 (정직한 보고):** `DummyClient`는 Increment(Id=1) 패킷을 전송하고 **에코 수신을 기대**(`received == 0`이면 종료)하나, 예제 서버는 에코하지 않음 → `ReceiveAsync`에서 무한 대기하거나 송신량만 측정되어 **오해를 주는 수치**가 됨. 의미 있는 부하 수치를 내려면 (a) 서버 예제에 에코 경로 추가, 또는 (b) DummyClient를 송신-전용 측정으로 명시 필요. 가짜 처리량 수치를 보고하지 않기 위해 미실행 처리.

---

## 우선순위 ① — 성능 / GC (영향 순)

### P1 🔴🔴 HIGH — 디스패치 오프로드 부재 (inline 핸들러)
**위치:** `SocketPipelineSession.cs:119-123` (`ReadPipeAsync` → `await DispatchPacketAsync` → `await OnReceived`)

각 세션이 수신 루프 스레드에서 패킷을 **엄격히 순차** 처리한다. 다음 패킷 파싱이 현재 핸들러 완료까지 대기 → (a) 세션 내 파이프라이닝 0, (b) 핸들러 실행 동안 스레드풀 스레드 점유(다수 세션이 느린 핸들러면 스레드풀 고갈), (c) `FlushAsync` 백프레셔(`:93`)로 느린 reader가 결국 `ReceiveAsync`를 멈춰 TCP 흐름제어로 피어를 throttle. 핸들러에 5ms DB 호출이 있으면 해당 세션은 망 용량과 무관하게 ~200 pkt/s로 캡.

이 항목은 **P2(패킷당 async 박싱)의 근본 원인**이기도 하다.

**권장 수정:** 프레이밍과 핸들러 사이에 세션별 `Channel<T>`(bounded, `SingleReader=true`)를 둔다. 읽기 루프는 프레이밍+enqueue만, 전용 소비자가 drain.
```csharp
// Channel<T>: lock-free SPSC 경로 — 단일 IO 리더가 쓰고 단일 소비자가 읽어 락 없이 핸들러를 IO 루프에서 분리.
//             Bounded + FullMode.Wait로 백프레셔를 핸들러 지연 → 수신 throttle로 자연 전파.
private readonly Channel<PooledPacket> _inbound =
    Channel.CreateBounded<PooledPacket>(new BoundedChannelOptions(1024){ SingleReader = true, SingleWriter = true });
```
**트레이드오프:** 버퍼링 재도입(반드시 bound), 세션 내 순서는 단일 reader가 보존. IO 처리량을 핸들러 지연과 분리. 이 라이브러리 성격(범용 서버)상 가장 큰 성능 개선.

### P2 🔴 MED~HIGH — 패킷당 `async ValueTask` 상태머신 박싱
**위치:** `SocketPipelineSession.cs:172` (`DispatchPacketAsync`), await 지점 `:121`

`ValueTask`는 동기 완료 시에만 무할당. `SendAsync`(PONG 경로)가 `_sendGate.WaitAsync()`에서 suspend하거나 앱 `OnReceived`가 비동기로 suspend하면 상태머신이 힙 `IValueTaskSource` 박스로 승격 → **정상부하에서 패킷당 ~1 할당**.

**권장 수정:** (1) P1의 Channel 오프로드를 적용하면 자연 완화. (2) 또는 동기 fast-path 분리 — PING이 아니고 단일 세그먼트이며 핸들러가 동기 완료하는 경로를 `if (vt.IsCompletedSuccessfully) return;`로 처리해 박싱 회피. (3) `PoolingAsyncValueTaskMethodBuilder` 적용 검토(핸들러가 항상 비동기일 때).

### P3 🔴🔴 MED — `_sendGate`를 송신 전체 구간 점유
**위치:** `SocketPipelineSession.cs:234-236` (`SendAsync`), PONG 송신 `:179`

세마포어를 `await _socket.SendAsync(...)` 전 구간 점유. 세마포어 자체는 저렴하나, TCP 송신 버퍼가 가득(느린 피어)이면 진행 중인 앱 송신이 게이트를 잡고 **하트비트 PONG을 막아** RTT가 앱 송신 지연에 결합 → 거짓 유휴/타임아웃 유발. 더 나아가 **죽은 피어**에 대해 소켓 `SendAsync` 취소는 best-effort라 무한 대기 가능, 그 세션 대상 모든 송신이 블록되고 `BroadcastAsync`(`SessionRegistry.cs:55-60`)는 각 송신을 순차 await하므로 **한 세션이 막히면 브로드캐스트 전체 정지**(가장 현실적인 hang).

**권장 수정:** 송신에 타임아웃 부여(linked CTS `CancelAfter`).
```csharp
using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
sendCts.CancelAfter(SendTimeout);                 // 죽은 피어가 게이트를 영구 점유하는 것을 차단
await _sendGate.WaitAsync(sendCts.Token);
try { await _socket.SendAsync(data, SocketFlags.None, sendCts.Token); }
finally { _sendGate.Release(); }
```
선택: 세션별 송신 큐(`Channel<ReadOnlyMemory<byte>>` + 단일 writer)로 게이트 제거 + 쓰기 배칭 + PONG 우선순위. **NoDelay=true는 유지**(게임 서버 저지연), 배칭은 앱 레벨 coalescing이지 Nagle 복원 아님.

### P4 🔴🔴 HIGH — RUDP 송신 취소 경로 ArrayPool 누수
**위치:** `RudpChannel.cs:45-53` (`SendReliableAsync`)

버퍼를 Rent 후 **try/finally 없음**. 채널이 `BoundedChannelFullMode.Wait`라 가득 차고 `ct`가 발화하면 `EnqueueAsync`가 `RudpSegment` 진입 전 OCE를 던져 → `SendLoopAsync`의 finally도 `Dispose` drain도 버퍼를 보지 못함 → **영구 누수**(반복 시 풀 고갈 → 모든 Rent가 새 할당으로 퇴화). 주석 "반납 책임은 SendLoop으로 이전"은 *enqueue 성공 시에만* 참.

**권장 수정:** `handedOff` 플래그 + try/finally. `EnqueueAsync` 반환과 플래그 설정 사이 await 없음 → race-free.
```csharp
var buffer = ArrayPool<byte>.Shared.Rent(totalSize);
bool handedOff = false;
try {
    /* ... write ... */
    await _queue.EnqueueAsync(segment, ct);   // 성공해야 소유권이 SendLoop으로 이전
    handedOff = true;
}
finally { if (!handedOff) ArrayPool<byte>.Shared.Return(buffer); }
```

### P5 🔴🔴 MED/LOW — `BroadcastAsync` 스냅샷 배열 할당
**위치:** `SessionRegistry.cs:39` (`_sessions.Values.ToArray()`)

`ValueTask[]` 결과 배열은 이미 `ArrayPool` 대여(`:43`)인데 세션 스냅샷은 매 호출 새 배열 → 같은 메서드 내 정책 불일치. 다만 `ConcurrentDictionary.Values` 스냅샷 풀링은 비자명(`Count`가 근사값 → 오버플로 시 재대여 필요)하므로 **의식적 LOW**. `IdleSweepLoopAsync:89`가 쓰는 재사용 List 패턴 참고.

### P6 🔴🔴 MED — RUDP 수신 패킷마다 `new byte[]`
**위치:** `RudpChannel.cs:103-105` (`new byte[received - HeaderSize]`)

`OnReceived`가 await되므로 수명 계약은 TCP 멀티세그먼트 경로(이미 `Rent→await→Return`)와 동일. 두 전송 경로가 불일치(TCP는 풀링, RUDP는 raw alloc). 고PPS에서 Gen0 압력. **수정:** ArrayPool 대여 + 반납 계약, 또는 P1식 오프로드.

### P7 🔴🔴 LOW(성능) / HIGH(조건부 데드락) — `new Pipe()` 기본 옵션
**위치:** `SocketPipelineSession.cs:47`, `SocketPipelineClient.cs:41`

`PipeOptions.Default`는 `useSynchronizationContext: true`. **클라이언트** Pipe가 문제: `ConnectAsync`를 UI 스레드에서 await하면 모든 `FlushAsync`/`ReadAsync` continuation이 UI 스레드에 고정 → 데드락 위험. **`ConfigureAwait(false)`로는 못 고침**(Pipe가 스케줄링을 제어). 서버 Pipe는 `AcceptLoopAsync`가 ThreadPool에서 루프를 시작하므로 안전.

**권장 수정:** 공유 `static readonly PipeOptions`(+ pause/resume 임계값 튜닝).
```csharp
// useSynchronizationContext:false — continuation을 캡처된 SyncContext가 아닌 ThreadPool에서 실행해 UI 스레드 고정/데드락 방지.
private static readonly PipeOptions s_pipeOptions =
    new(useSynchronizationContext: false, pauseWriterThreshold: 64*1024, resumeWriterThreshold: 32*1024);
```

### P8 🔴 LOW~MED — 패킷당 PONG 프로브 + 헤더 3중 파싱 (CPU only, 무할당)
**위치:** `SocketPipelineSession.cs:154`(`out _`로 id 폐기) → `:176` `TryBuildPongBuffer` 재파싱 → `Deserialize` 3차 파싱

`TryReadPacket`이 이미 파싱한 `packetId`를 버리고, 프로브가 stackalloc+CopyTo로 헤더를 재파싱. >12B 앱 패킷은 길이 비교 1회로 early-out이라 저렴하나 ≤12B 패킷엔 낭비. **수정:** `TryReadPacket`에서 파싱한 `packetId`를 밖으로 넘겨 `id == PingPacket.Id` 직접 분기 → 프로브의 stackalloc/CopyTo/재파싱 제거.

### P9 🔴🔴 MED — Accept 루프 inline 콜백 await + 예외 삼킴
**위치:** `SocketPipelineListener.cs:159-160`(`await OnClientConnected`), 루프 catch가 OCE/SocketException만

느린 connect 콜백(auth/DB)이 **accept 속도를 직접 캡** → 연결 폭주 시 커널 backlog(512) 초과로 SYN 드롭. 또한 콜백이 그 외 예외를 던지면 루프 태스크가 unobserved fault → **서버가 조용히 accept 중단**(IsRunning은 true). **수정:** 루프에서 콜백을 await하지 않거나(또는 bounded 오프로드) + `catch(Exception)`로 관찰.

---

## 우선순위 ② — 확정성 (Correctness / Determinism)

### C1 🔴 MED — `_lastReceivedAtTicks` 32-bit 환경 torn read
**위치:** `SocketPipelineSession.cs:89`(writer `Volatile.Write`) vs `:28`(reader `Interlocked.Read`)

plain `Volatile.Write(ref long)`은 **32-bit 런타임에서 원자성 미보장**(64-bit 원자성은 Interlocked만 보장). 비대칭 → 32-bit/ARM32에서 reader가 반쪽 갱신된 타임스탬프를 읽어 살아있는 세션을 evict할 수 있음. 주석 "single writer라 Volatile.Write 충분"은 32-bit에서 거짓. **수정:** writer를 `Interlocked.Exchange`로(또는 64-bit 전용 배포 확정 시 reader의 Interlocked.Read를 Volatile.Read로 통일 — 한쪽 모델로). x64/ARM64 전용이면 실제 영향은 사실상 0이나 주석은 수정 필요.

### C2 🔴🔴 HIGH(조건부) — `Stop()` sync-over-async
**위치:** `SocketPipelineListener.cs:67` (`DisposeAsync().AsTask().GetAwaiter().GetResult()`) + dispose 체인 `ConfigureAwait(false)` 부재

`Stop()`이 SyncContext 있는 스레드(UI 호스트 임베딩, 단일 스레드 SyncContext 테스트)에서 호출되면 continuation이 이미 블록된 스레드로 post → 고전적 데드락. 주석 "종료 경로라 데드락 위험 없음"은 SyncContext 없을 때만 참. **수정:** dispose 체인 전반 `ConfigureAwait(false)`, 또는 `Stop`을 async로.

### C3 🔴 LOW(위생) — 라이브러리 전반 `ConfigureAwait(false)` 누락
`SessionRegistry.cs:57`만 보유. 대부분 ThreadPool에서 context-free로 돌아 잠재적이나, C2/P7을 통해 실제 문제화. 라이브러리 코드 전반 적용 권장.

### C4 🔴 LOW — `_sendGate.Dispose()` 경합
**위치:** `SocketPipelineSession.cs:248`. `SemaphoreSlim.Dispose()`는 대기 중 `WaitAsync` waiter를 release/fault하지 않음 → 큐된 waiter(예: BroadcastAsync 송신)가 **영구 hang**하거나, dispose 후 새 WaitAsync는 ODE, 점유자의 `finally{Release()}`는 ODE. 저빈도 race.

### C5 🔴 MED(잠재) — 죽은 RUDP 재전송 코드 → 향후 use-after-return
`WithRetry`/`MaxRetries`/`RetransmitInterval`/`RudpRecvWindow.BuildAckBitmap` 모두 미참조(dead code). `SendLoop`은 첫 송신 후 버퍼를 즉시 Return. 현재는 fire-and-forget UDP라 안전하나 **재전송을 배선하는 순간 이미 반납된 버퍼 재송신 = use-after-return**. **결정 필요:** (A) 죽은 재전송 코드 삭제, 또는 (B) 버퍼 소유권을 unacked-segment 저장소로 옮기고 ACK/MaxRetries 시점에만 Return.

### ✅ 검증 결과 "정상" (수정 불필요 — false positive 방지)
- `TransitionTo` CAS 루프 — 종착 상태 보존·ABA 무관, 교과서적 정확.
- `ServerMetrics` — 4개 64-bit 카운터 read/write 모두 Interlocked, 전 플랫폼 원자.
- `ConcurrentDictionary` check-then-act — `TryRemove` 성공으로 게이트하여 이중 발화 차단(sweep vs disconnect).
- `_sendGate` 존재 자체 — `Socket.SendAsync`는 동일 소켓 동시 쓰기 미지원(커널 계약). 송신 직렬화 **필수**, lock-free 대안 없음. 1-permit async 게이트는 표준 패턴. **유지.**
- `BroadcastAsync`의 `Array.Clear` before Return — 필수(죽은 ValueTask 박스가 풀 배열에서 객체 rooting 방지).
- 단일 세그먼트 fast-path(`OnReceived(packet.First)`로 raw Pipe 메모리 전달) — 3개 Interface 문서가 "콜백 반환 후 메모리 무효, 보관 시 복사" 명시 → **계약상 안전**.
- `_sendGate`는 예외/취소 시 누수 없음(WaitAsync는 try 밖, Release는 inner finally).

---

## 우선순위 ② — 편의성 (API Ergonomics)

> 인터페이스 XML 문서화는 매우 우수(Thread Safety·Memory Policy·Blocking 모두 명시). 아래는 *사용 편의* 관점 개선점.

### E1 🔴 MED — 패킷 레벨 `SendAsync<T>(T packet)` 부재
소비자가 직접 rent→헤더 작성→직렬화→Send→Return을 손으로 관리해야 함(에러 유발, "편의성" 목표와 상충). 직렬화 레이어는 있으나 송신에 ergonomic하게 연결 안 됨. **권장:** `ISession`/`IClientConnection`에 `ValueTask SendAsync<T>(T packet) where T : IPacket` 오버로드 추가 — 내부에서 ArrayPool 대여·직렬화·반납 캡슐화.

### E2 🔴 LOW — `Context`가 `object?`
접근마다 캐스팅, 값 타입은 박싱(문서화됨). `ISession<TContext>` 제네릭 변형 검토 또는 현 문서 유지.

### E3 🔴 MED — 콜백이 mutable settable `Func` 프로퍼티
단일 구독자만 가능, Start/Connect **이후 설정 시 unsafe**(IdleTimeout만 throw로 강제, 나머지는 조용히 위험). lock-free 에이전트도 `OnReceived` non-volatile 필드를 지적(현 배선은 안전하나 재할당 시 fragile). **권장:** 생성자 주입 또는 init-only, 혹은 "Start 전 설정만" 명문 강제.

### E4 🔴 LOW — `OnReceived`가 `ISession`·`IServerListener` 양쪽에 존재
수신 경로 2개의 우선순위/관계가 불명확 — 문서 보강 필요.

### E5 🔴 LOW — `TransitionTo`가 `ISession` public
내부 상태머신 변경을 소비자에 노출 → 상태 오염 가능. 노출 범위 축소 검토.

### E6 🔴 LOW — `Start(int port)` 한정
bind 주소/backlog/IPv6/dual-mode 미지원.

### E7 🔴🔴 MED — `IPacketSerializer` → Core 레이어 역전 (아키텍처)
**위치:** `Interface/IPacketSerializer.cs:1` `using ServerLib.Core.Serialization;` → `IPacket`(`Core/Serialization/IPacket.cs`) 참조.

프로젝트 규칙("Interface는 순수 추상화, 의존성은 Core→Interface, 역방향 금지")을 **정면 위반**. 추상화인 `IPacket`이 Core에 있고 Interface가 Core를 역참조. **수정:** `IPacket`(및 `SpanReader/SpanWriter` 계약)을 `Interface` 레이어로 이동.

---

## 권장 적용 순서 (성능 우선)

1. **P4**(풀 누수, 정확성→성능 퇴화) — 작고 안전, 즉시.
2. **P3**(송신 타임아웃) — hang/가용성, 작은 변경.
3. **P1**(Channel 디스패치 오프로드) — 최대 성능 개선, P2 동반 완화. 설계 변경이라 별도 사이클 권장(이 저장소엔 `thread-dispatch-design`/`io-loop-design` 하네스 존재).
4. **P7·C2·C3**(Pipe 옵션 + ConfigureAwait) — 묶어서, 클라이언트 데드락 차단.
5. **P5·P6·P8**(할당·CPU 미세 최적화) — 측정 후.
6. **E1·E7**(편의성·레이어링) — API 안정화 시점.

*실제 코드 수정은 본 리포트 승인 후 별도 단계로 진행한다.*
