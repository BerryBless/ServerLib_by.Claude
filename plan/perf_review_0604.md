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
| P2 | 패킷마다 `async ValueTask` 상태머신 박싱 | `SocketPipelineSession.cs:172` | 정상부하 GC 압력 (~1 alloc/packet) — **TCP live path 최상위 GC** | 🔴 |
| P3 | `_sendGate`를 송신 전체 구간 점유 → 브로드캐스트 HOL·PONG 지연·죽은 피어에 무한 대기 | `SocketPipelineSession.cs:234-236` | 꼬리지연·가용성 (MED) — **실측 재현됨(③)** | 🔴🔴 |
| P5 | `BroadcastAsync` 호출마다 `Values.ToArray()` 스냅샷 할당 | `SessionRegistry.cs:39` | 브로드캐스트 GC (MED/LOW) | 🔴🔴 |
| P1 | 디스패치 오프로드 부재 — 핸들러를 IO 루프에서 패킷마다 inline await | `SocketPipelineSession.cs:119-123` | **조건부**: 핸들러가 no-blocking 계약을 지키면 정상. 위반 시 처리량·스레드풀 점유 | 🔴🔴 |
| ⚠️ | P4·P6·C5(RUDP 풀 누수/할당/use-after-return) | `RudpChannel.cs` | **현재 dead code — `new RudpChannel` 호출처 0개**. 잠재 결함, live 영향 없음 | 🔴🔴 |

핵심 결론: **프레이밍/직렬화 레이어는 매우 잘 설계됨** — 실측에서 직렬화 hot path **Allocated = 0 B** 확인(②). live TCP 경로에서 남은 실질 GC 압력은 **패킷당 async 상태머신 박싱(P2)** 이며, 가용성 리스크는 **송신 게이트의 무한 점유(P3, ③에서 실측 재현)** 다. RUDP 서브시스템(P4·P6·C5)은 **어떤 진입점에서도 인스턴스화되지 않는 미연결 코드**라 현재 영향이 없으므로 우선순위에서 제외했다(아래 ⚠️ 참조).

> **⚠️ RUDP 미연결(dead code) 주의:** `RudpChannel`/`RudpSendQueue`는 `ServerLib` 내 어디서도 `new`되지 않는다(테스트 포함 grep 0건). 따라서 P4(취소 경로 풀 누수), P6(`new byte[]`/수신), C5(죽은 재전송→use-after-return)는 **모두 실행되지 않는 코드의 잠재 결함**이다. RUDP를 실제 배선하기 전에 수정하면 충분하며, "지금 즉시" 적용 대상이 아니다. (RUDP를 곧 활성화할 계획이면 P4를 최우선으로 올릴 것.)

---

## 실측 (Empirical)

> 환경: BenchmarkDotNet v0.14.0, **.NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2**, Workstation Concurrent GC, ShortRun(Warmup=3, Iter=3), `InProcessEmitToolchain`. 마이크로벤치는 *기준선·검증* 용도.

### ① 인프라 이슈 — 벤치 자체가 실행 불가 (선행 단계에서 실측 확인) 🔴🔴
`PacketSerializerBenchmark`/`SessionSendBenchmark` 둘 다 `[SimpleJob(RuntimeMoniker.Net90)]`을 박아두었으나 프로젝트는 `net10.0`. BDN이 net9 자동생성 프로젝트를 빌드하려다 **실제로 실패**:
```
error NU1201: Benchmark 프로젝트가 net9.0(.NETCoreApp,v9.0)과 호환되지 않습니다.
              Benchmark 프로젝트는 net10.0을 지원합니다.
// BenchmarkDotNet has failed to build the auto-generated boilerplate code.
```
→ **모니커 수정 없이는 벤치가 0개 실행**됨(리뷰 시작 시점 상태). 본 실측은 `--inProcess`로 net10 호스트에서 직접 실행하여 우회했고 결과는 ②. **권장 수정:** `RuntimeMoniker.Net90` → `Net10_0` 상향, 또는 `[SimpleJob]` 제거 후 in-process 고정.

### ② 직렬화 microbench 결과 — zero-alloc 주장 실측 검증 ✅
`Benchmark/PacketSerializerBenchmark.cs` (메모리 진단 포함):

| Method | Mean | Gen0 | Allocated | vs baseline |
|--------|-----:|-----:|----------:|------------:|
| `new byte[] + Encoding.GetBytes` (baseline) | 21.77 ns | 0.0068 | **128 B** | 1.00× |
| `ArrayPool.Rent + Span` (Zero-Allocation) | 5.36 ns | – | **0 B** | **0.246× 시간, 0 할당** |
| `HeaderParse from Span` (Zero-Allocation) | 0.195 ns | – | **0 B** | empty-method과 구분 불가 |

**해석:** 정적 스캔이 zero-alloc로 판정한 `SpanWriter`/`ArrayPool`/헤더 파싱 경로가 실측에서도 **Allocated = 0 B**로 확인됨(baseline 128B 대비). ArrayPool+Span 직렬화는 `new byte[]`+`Encoding.GetBytes` 대비 **약 4배 빠르고 할당 0**. 직렬화 레이어는 성능 목표를 충족.

### ③ `SessionSendBenchmark` — 측정 불가(hang), 그러나 P3를 실측 재현 🔴
`SessionSendBenchmark`는 파일럿 단계에서 **무한 정지**했다. 원인: 벤치가 `_clientSocket`(수신측)을 전혀 drain하지 않아 ~512 ops(≈64KB) 후 **TCP 송신 버퍼가 가득 차 `SendAsync`가 영구 블록**된다. 이는 (a) 벤치 설계 결함이자, (b) **finding P3("드레인하지 않는/죽은 피어에 대해 `_sendGate` 점유 송신이 무한 대기")를 실측으로 재현**한 것이다. 송신 타임아웃(P3 수정안)이 있었다면 이 hang은 발생하지 않는다. (해당 hang 프로세스는 PID 지정으로 정리함.)

### ④ LoadTest — 실행 보류 (정직한 보고)
`LoadTest/DummyClient.cs`는 Increment(Id=1) 패킷을 전송하고 **에코 수신을 기대**(`received == 0`이면 종료)하나, 예제 서버는 에코하지 않음 → `ReceiveAsync` 무한 대기 또는 송신량만 측정되어 **오해를 주는 수치**가 됨. 가짜 처리량 수치 보고를 피하기 위해 미실행. 의미 있는 부하 측정을 하려면 (a) 서버 예제에 에코 경로 추가, 또는 (b) DummyClient를 송신-전용으로 명시 필요.

---

## 우선순위 ① — 성능 / GC (영향 순)

### P1 🔴🔴 조건부 — 디스패치 오프로드 경로 부재 (inline 핸들러)
**위치:** `SocketPipelineSession.cs:119-123` (`ReadPipeAsync` → `await DispatchPacketAsync` → `await OnReceived`)

> **먼저 — 이것은 의도된 설계다.** `ISession.OnReceived` 문서는 "콜백 내부에서 동기 블로킹을 절대 수행하지 말 것(블로킹 시 전체 수신 루프 정지)"을 **명시 계약**으로 둔다. 핸들러가 이 계약(빠른·non-blocking)을 지키면 inline 디스패치가 **오히려 최적**이다(디스패치 홉·버퍼링 없음). 따라서 이 항목은 "지배적 성능 결함"이 아니라 **"계약 위반 시 완충 장치가 없다"**는 견고성 갭이다.

핸들러가 계약을 위반(DB·File I/O 등)하면: (a) 세션 내 파이프라이닝 0, (b) 핸들러 실행 동안 스레드풀 스레드 점유 → 다수 세션이 느린 핸들러면 스레드풀 고갈, (c) `FlushAsync` 백프레셔(`:93`)로 느린 reader가 `ReceiveAsync`를 멈춰 TCP 흐름제어로 피어 throttle. 5ms DB 호출이면 해당 세션은 ~200 pkt/s로 캡(=문서가 경고하는 바로 그 안티패턴).

**권장 (선택적):** 모든 핸들러를 강제로 `Channel`에 태우지 말 것 — fast-handler 공통 경로를 **퇴화**시킨다. 대신 **옵트인** 오프로드를 제공: 느린 핸들러를 쓰는 소비자만 세션별 `Channel<T>`(bounded, `SingleReader=true`)를 켜서 프레이밍과 처리를 분리. 읽기 루프는 프레이밍+enqueue만, 전용 소비자가 drain.
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

**권장 수정 — 단, 할당에 주의(성능 1순위):** "linked CTS + `CancelAfter`"를 **매 송신마다** 만들면 `CancellationTokenSource` + 타이머 + 등록이 송신당 할당되어 → **P2의 패킷당 박싱보다 더 큰 정상부하 GC를 새로 유발**한다(GC-first 라이브러리에선 순손해). 다음 중 택1:
- **(선호) 세션별 송신 큐**(`Channel<ReadOnlyMemory<byte>>` + 단일 writer task): 게이트 자체를 제거하고, writer task가 `Socket.SendAsync`에 `CancelAfter`를 **재사용 타이머 1개**로 적용 + 쓰기 배칭 + PONG 우선순위. 송신당 추가 할당 0.
- **(경량) 소켓 `SendTimeout`/`SendBufferSize`** 기반 차단: CTS 없이 OS 레벨 타임아웃으로 무한 점유만 끊기.
- 송신당 CTS는 **피하거나**, 부득이하면 풀링/재사용 패턴으로.

**NoDelay=true는 유지**(게임 서버 저지연), 배칭은 앱 레벨 coalescing이지 Nagle 복원 아님.

> **실측 근거:** 이 무한-점유 시나리오는 `SessionSendBenchmark`가 수신측을 drain하지 않아 송신 버퍼가 가득 찰 때 **그대로 hang**하는 것으로 재현됨(실측 ③).

### P4 🔴🔴 ~~HIGH~~ → 잠재(미연결) — RUDP 송신 취소 경로 ArrayPool 누수
**위치:** `RudpChannel.cs:45-53` (`SendReliableAsync`) · **현재 dead code(호출처 0개) — 잠재 결함, live 영향 없음**

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

### P6 🔴🔴 ~~MED~~ → 잠재(미연결) — RUDP 수신 패킷마다 `new byte[]`
**위치:** `RudpChannel.cs:103-105` (`new byte[received - HeaderSize]`) · **현재 dead code**

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

### C5 🔴 잠재(미연결) — 죽은 RUDP 재전송 코드 → 향후 use-after-return
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

**Live 경로 (지금 실행되는 코드):**
1. **P3**(송신 무한-점유 차단) — hang/가용성, 실측 재현됨. 단 송신당 CTS 할당 회피(세션 송신 큐 또는 소켓 타임아웃). 작은 변경, 높은 ROI.
2. **P7·C2·C3**(Pipe `useSynchronizationContext:false` + dispose 체인 `ConfigureAwait(false)`) — 묶어서, 클라이언트 데드락 차단. 안전·저위험.
3. **P2**(패킷당 async 박싱) — live TCP 경로 최상위 GC. 동기 fast-path 분리 또는 P1 옵트인 오프로드와 함께.
4. **P5·P8**(브로드캐스트 스냅샷·헤더 재파싱) — 미세 최적화, 측정 후.
5. **P1**(옵트인 디스패치 오프로드) — *강제 적용 금지*(fast-handler 퇴화). 느린 핸들러 지원이 필요할 때만. 설계 변경이라 별도 사이클(`thread-dispatch-design`/`io-loop-design` 하네스 활용).
6. **C1**(32-bit torn read) — x64/ARM64 전용 배포면 주석만 정정, 32-bit 타깃 시 `Interlocked.Exchange`.

**선결 인프라:** 벤치 `RuntimeMoniker.Net90`→`Net10_0`(또는 in-process) — 측정 가능화.

**미연결(RUDP) — 활성화 전 처리:** P4 → P6 → C5. 지금은 실행되지 않으므로 즉시성 없음. RUDP 배선 직전에 P4를 최우선으로.

**API 안정화 시점:** E1(패킷 레벨 `SendAsync<T>`) · E7(`IPacket` Interface 레이어로 이동, 의존성 역전 해소) · E3·E4·E5·E6.

*실제 코드 수정은 본 리포트 승인 후 별도 단계로 진행한다.*
