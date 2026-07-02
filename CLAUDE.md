# ClaudeCodeStudy 프로젝트

## 프로젝트 개요

**목표:** ProudNet과 같은 고성능 서버 라이브러리 개발 (.NET 10 기반)

**원칙:** Interface는 순수 추상화만, Core는 구현만 포함. 의존성 방향은 Core → Interface (역방향 금지).

**예제 코드 위치:** 각 프로젝트의 `Program.cs`가 라이브러리 사용 예제 역할을 한다.
- `Server/Program.cs` — 보스 몹 전투 호스트 예제: `MobManager`(HP 100,000), `DamagePacket`(Id=5) 수신 → `ApplyDamage`(`RequireAuth` 토글로 미인증 드롭 가능), 200ms 주기 `MobHpPacket`(Id=6) 브로드캐스트, 사망 시 `MobDeathPacket`(Id=7) 즉시 브로드캐스트·리스폰, `ISessionRegistry` 강제 활성, `[STATS]` hp/gen 신호 출력; `LoginRequestPacket`(Id=10)·`AuthTokenPacket`(Id=12) 병행 처리·`[AUTH+]`/`[GATE+]` 로그; `EnableTicketing` 토글로 티켓팅 모드 전환 — 더미 로그인(Id=10)·`SeatMapRequestPacket`(Id=16) 좌석맵 조회·`TicketReserveRequestPacket`(Id=13, **배치 포맷**: `[Count][Row,Col 쌍...]` N좌석 동시 예약)·`TicketPayRequestPacket`(Id=14) **일괄 결제**·`TicketResultPacket`(Id=15, `[Status][Count][Slots...][Remaining]`) 응답(SeatTaken·RateLimited 포함), 이탈 시 자동 슬롯 전체 반납, 1초 TTL 스위퍼 Task, 기본 2×3 그리드(6석); **배치 예약:** `Ticket.MaxSeatsPerSession`(기본 4)으로 세션 상한 설정, `TicketContext.Slots[]`(MaxSeatsPerSession 길이)가 per-seat CAS anchor, All-or-nothing 예약·결제 실패(ABA-safe 롤백 포함); **Reserve 속도 제한:** 세션별 60초/10회 슬라이딩 윈도우 (`TicketContext.RateLimitWindowMs`/`MaxReserveAttemptsPerWindow`), 초과 시 `TicketStatus.RateLimited`(Id=8) 응답; **모니터링:** `[TICKET]` 콘솔 신호(free/reserved/sold + 누적 KPI 6종)·`:9100` JSON `snapshot.ticket` 섹션(`TicketInventory.ProjectSeatStates()` — seats=int[]·byte[]→Base64 함정 회피)으로 실시간 좌석맵·KPI를 웹 대시보드에 전달; **관리 포트 보안:** `adminListener`를 루프백(127.0.0.1) 전용으로 기동(`IServerListener.Start(port, IPAddress.Loopback)`), MaxConnections=10, IdleTimeout=60s
- `Client/Program.cs` — 다중 공격자 예제: 스레드별 고정 딜(10·15·20·25·30 사이클) `DamagePacket` 반복 송신(1회 직렬화 후 버퍼 재사용 무할당 패턴), `conn.OnReceived`로 HP 바(T0만 출력)·처치 알림 수신, `[CLIENTSTATS]` 측정 신호 유지; `EnableAuthGating` 토글로 T0가 AuthServer(9200)→토큰→게임서버 `AuthTokenPacket` 제시 데모; `EnableTicketing` 토글로 티켓팅 데모 전환 — 7클라 동시 **배치 좌석지정** 예약(로그인→좌석맵조회→**K석 배치** 지정(`SeatsPerClient` 기본 2)→예약·SeatTaken 시 재선택 최대5회·`RateLimited`(Id=8) 수신 시 즉시 중단, `FailingClientIndex`=0이 결제 실패 후 K석 배치 재조회·재예약·재결제 시연), `Channel<byte[]>` inbox로 `OnReceived` 콜백 비동기 처리, 최종 `ConfirmedSeats ≤ min(ClientCount, floor(TotalSeats/SeatsPerClient))*SeatsPerClient` 상한 출력
- `AuthServer/Program.cs` — 독립 인증 서버 예제(port 9200): `ServerNet.CreateListener()`(registry 생략) + `LoginService` 전담 → `LoginRequestPacket`(Id=10)만 처리, Redis 토큰 발급
- `ServerLib.Examples/` — **전 public API 자체완결 예제 모음**: 11개 예제(`01_EchoBasics`~`11_Packets`)가 127.0.0.1 루프백으로 서버+클라를 한 프로세스에서 구동. `dotnet run -- all`로 전체 스모크 테스트 가능. 모든 코드에 프로젝트 주석 규칙 전적용(XML 문서 + 네트워크/메모리 선언부 내부동작 인라인 주석)
- `EchoServer/Program.cs` — **학습용 에코 서버 예제**(포트 9000): `ServerNet.CreateListener()` → `OnReceived`에서 `EchoPacket`(Id=1) 역직렬화 → 동일 메시지 재전송. 모든 ServerLib 사용 지점에 XML 문서 + 네트워크/메모리 내부동작 인라인 주석 전적용. 종료: 아무 키.
- `EchoClient/Program.cs` — **학습용 에코 클라이언트 예제**(인터랙티브 콘솔): `ServerNet.CreateClient()` → `ConnectAsync("127.0.0.1", 9000)` → 콘솔 입력을 `EchoPacket`으로 전송, 에코 응답 출력. `await using` 패턴으로 `IAsyncDisposable` 정리. 종료: `exit`.
- `EchoWeb/Program.cs` — **브라우저용 웹 에코 데모**(ASP.NET Core, `http://127.0.0.1:8080`): 브라우저가 raw TCP를 직접 말할 수 없으므로 `/ws` WebSocket ↔ 기존 `EchoServer.exe`(9000) TCP 간 프로토콜 변환 브리지만 담당(에코 로직은 재사용, 별도 프로세스 구성). WebSocket 연결 1개 = `ServerNet.CreateClient()` 세션 1개(per-session 격리); `Channel<string>` 단일 소비자 펌프로 `WebSocket.SendAsync` 동시호출 금지 제약 충족; 단일 `CancellationTokenSource`로 브라우저 종료·9000 드롭 두 실패원을 수렴시켜 Cancel→채널종료→펌프대기→WS close→`echo` dispose 순서 고정; `wwwroot/index.html`은 `textContent` 렌더로 XSS 방지. 설계 문서: `plan/echoweb_0702.md`.

**캡슐화(v1.1.0~):** Transport 구현체(`SocketPipelineListener`/`~Client`/`~Session`)와 `SessionRegistry`는 `internal`. 외부 소비자는 `ServerNet` 팩토리가 반환하는 인터페이스로만 사용한다. 직렬화 빌딩블록(`IPacket`·`IPacketSerializer`·`BinaryPacketSerializer`·패킷 타입·`PacketPool`)은 public. 새 Transport 진입점을 추가하면 `ServerNet` 팩토리에도 생성 메서드를 노출할 것.

새 기능을 추가할 때 Program.cs의 예제도 함께 업데이트할 것.

## 하네스: Git 자동 커밋 & 푸시 (Git Automator)

**목표:** 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋 & 푸시를 파이프라인으로 자동화한다.

**트리거:** `/commitandpush`, 커밋해줘, 푸시해줘, 변경사항 올려줘, 깃 커밋 요청 시 `commitandpush` 스킬을 사용하라.

**자동 커밋 메시지 전달 (필수 행동 규칙):**
코드·파일 변경을 완료하고 턴을 마치기 직전, WHY 중심 한국어 커밋 메시지를 **`.git/auto_commit_msg.txt`** 에 UTF-8로 작성한다.
- 형식: `{접두사}: {제목}` (접두사: 추가/수정/버그수정/리팩토링/문서/테스트/의존성)
- 제목: 50자 이내, 파일명 나열 금지, WHY 중심
- 본문(선택): `- ` 항목 나열
- 마지막 줄(필수): `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>`

Stop 훅(`auto-commit.ps1`)이 이 파일을 읽어 커밋하고 즉시 삭제한다. 파일을 남기지 않으면 접두사 기반 폴백 메시지로 커밋된다(안전망).

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-03 | 초기 구성 | 전체 | Git 자동 커밋&푸시 파이프라인 구축 |
| 2026-06-03 | 파일 기반 메시지 전달로 재설계 | auto-commit.ps1 | nested claude -p 콜드스타트/stdin 취약성으로 폴백 빈발 |

---

## 플랜 문서화 규칙

기능 설계나 아키텍처 결정이 완료되면 `plan/` 디렉토리에 설계 문서를 작성한다.

### 파일 명명 규칙
```
plan/<기능명>_<MMDD>.md
예) plan/packet_serialization_0602.md
    plan/rudp_channel_0603.md
    plan/rpc_generator_0610.md
```

### 문서 필수 포함 항목
1. **배경 및 목적** — 왜 이 기능이 필요한가, 어떤 문제를 해결하는가
2. **설계 결정** — 채택한 방식과 후보 대안 비교 (표 형식 권장)
3. **컴포넌트 구조** — 디렉토리 트리, 의존 관계 다이어그램
4. **핵심 API** — 주요 사용 패턴 코드 예시
5. **변경 파일 목록** — 신규/수정 파일과 내용 요약
6. **빌드 검증** — 실행 명령어
7. **향후 확장 포인트** — 다음 사이클 추천 항목

### 현재 플랜 문서 목록

| 파일 | 날짜 | 내용 |
|------|------|------|
| `plan/output_0602_224508.txt` | 2026-06-02 | 4단계 아키텍처 구현 빌드 출력 로그 |
| `plan/packet_serialization_0602.md` | 2026-06-02 | 패킷 직렬화 설계 (SpanWriter/SpanReader/BinaryPacketSerializer) |
| `plan/security_audit_0609.md` | 2026-06-09 | 해킹·DDoS 공격 표면 보안 감사 (원격 크래시 A1~A3·자원 고갈 B1~B4, 감사 전용) |
| `plan/nuget_distribution_0611.md` | 2026-06-11 | ServerLib NuGet 배포 설계 (소스 비공개, DLL+XML 동봉, 로컬 피드 소비) |
| `plan/interface_encapsulation_0611.md` | 2026-06-11 | 인터페이스 전용 노출 (Transport 구현체 internal화 + ServerNet 팩토리, v1.1.0) |
| `plan/mob_combat_0612.md` | 2026-06-12 | 보스 몹 전투 컨텐츠 (DamagePacket·MobHpPacket·MobDeathPacket, MobManager lock-free 설계) |
| `plan/auth_server_separation_0616.md` | 2026-06-16 | 인증 서버 독립 프로세스 분리 (AuthServer.exe 9200 + Auth 공유 라이브러리 + AuthTokenPacket·RequireAuth 게이팅) |
| `plan/token_username_recovery_0617.md` | 2026-06-17 | 토큰 게이팅 시 Username 복원 (ITokenStore.TryResolveAsync·TokenInfo, Redis delimited String, AuthContext.Username 완성) |
| `plan/ticketing_0618.md` | 2026-06-18 | 선착순 티켓팅 시스템 (lock-free TicketInventory·더미 로그인/결제·reserve-then-pay·TTL 스위퍼, 22개 신규 테스트) |
| `plan/ticketing_review_0618.md` | 2026-06-18 | 티켓팅 7차원 종합 코드 리뷰 (SEC-01 결제전 검증누락·ARCH-01 도메인오염·GAP-01 SweepExpired 미테스트 등 15건) |
| `plan/soak_test_0618.md` | 2026-06-18 | 소크 테스트 하네스 설계 (N개 클라 연결 churn·[STATS] 파싱·Hard 판정·child 프로세스 아키텍처) |
| `plan/ticketing_seat_designation_0619.md` | 2026-06-19 | 좌석지정 예약 (2D 좌석·SeatMapRequest/Response·SeatTaken, TryReserve(seatId), SnapshotStates, 144개 테스트) |
| `plan/ticketing_seat_designation_review_0620.md` | 2026-06-20 | 좌석지정 예약 7차원 종합 코드 리뷰 (ARCH-02 별칭버그·SEC-NEW-01 SimulateFailure노출·STYLE-03 2D테스트누락 등 High 4건, 종합 77점) |
| `plan/ticketing_monitoring_0622.md` | 2026-06-22 | 티켓팅 모니터링 (lock-free 누적 카운터·MetricsSnapshot·[TICKET] 콘솔 라인·JSON ticket 섹션·대시보드 좌석맵+KPI) |
| `plan/ticketing_monitoring_review_0623.md` | 2026-06-23 | 티켓팅 모니터링 7차원 종합 코드 리뷰 (SEC-MON-01~03·ARCH-NEW-01·SEC-NEW-03·STYLE-01 등 High 1건+Medium 5건 → 리뷰 당일 전량 수정 완료, 종합 86→92점 예상) |
| `plan/ticketing_multiseat_0624.md` | 2026-06-24 | 배치 멀티 좌석 티켓팅 (TicketContext.Slots[]·TryReserveBatch·ConfirmAll·ReleaseAll All-or-nothing, 배치 와이어 포맷, MaxSeatsPerSession 설정, 172 테스트) |
| `plan/dbperf_test_0627.md` | 2026-06-27 | DB 포함 성능 테스트 하네스 (closed-loop login·token-resolve, [DBSTATS] 순수 DB 지연 분리, docker-compose) |
| `plan/test_review_0628.md` | 2026-06-28 | ServerLib.Tests 종합 코드 리뷰 (품질 감사+커버리지 갭, QUALITY-I 4건 수정·GAP-C/I 22건 신규 추가, 210 테스트) |
| `plan/echoweb_0702.md` | 2026-07-02 | 웹 기반 에코 데모 서버 (EchoWeb: WebSocket↔EchoClient(TCP) 브리지, 별도 프로세스 구성, Channel 단일소비자 펌프, linkCts 통합 teardown) |

---

## 인터페이스 및 API 문서화(주석) 규칙

모든 인터페이스, public 클래스의 메서드, 대리자(Delegate), RPC 정의 코드를 생성하거나 수정할 때는 반드시 표준 XML 문서 주석(C# `///`)을 매우 상세히 작성해야 한다. 단순 기능 설명을 넘어 **고성능 시스템 프로그래밍 관점의 제약 조건**을 주석에 반드시 포함할 것.

### 주석 필수 포함 항목 (`<remarks>` 활용)

- **Thread Safety:** `Thread-safe` 또는 `Not Thread-safe` 명시. 콜백이면 어느 스레드 컨텍스트(I/O Thread, 호출 스레드 등)에서 실행되는지 명시.
- **Memory Allocation:** 힙 할당 발생 여부(`Zero-allocation guaranteed` 혹은 내부 할당량 명시). `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>` 버퍼의 **소유권(Ownership)과 생명주기** 명시.
- **Blocking 여부:** 즉시 반환인지, 동기 블로킹인지, 비동기(Non-blocking)인지 명시.

### 이상적인 주석 예시

```csharp
/// <summary>수신된 로우 패킷 버퍼를 역직렬화하여 내부 이벤트 파이프라인으로 라우팅합니다.</summary>
/// <param name="sessionId">패킷을 송신한 클라이언트 세션의 고유 식별자</param>
/// <param name="packetBuffer">수신된 원시 바이트 데이터 세그먼트</param>
/// <returns>패킷 라우팅 및 처리 성공 여부</returns>
/// <exception cref="InvalidPacketException">패킷 헤더가 손상되었거나 프로토콜 구조와 맞지 않을 때</exception>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 고성능 네트워크 I/O 스레드 풀에서 직접 호출됩니다.
/// 내부에서 동기 블로킹(DB, File I/O)을 수행하면 전체 수신 루프가 정지됩니다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="packetBuffer"/> 소유권은 메서드 실행 동안만 유효합니다.
/// 반환 후에도 참조하려면 복사본을 생성해야 합니다.</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe. 내부적으로 ConcurrentQueue 및 Interlocked로 락 경합을 최소화합니다.</description></item>
/// </list>
/// </remarks>
bool OnPacketReceived(long sessionId, ReadOnlySpan<byte> packetBuffer);
```

### 네트워크·메모리 관련 선언부 인라인 주석 규칙

네트워크 또는 메모리 관련 **함수·변수·필드를 선언할 때**는, 그것을 선택한 이유를 반드시 **해당 타입/API의 내부 동작**을 근거로 인라인 주석(`//`)으로 달아야 한다.

- 대상: `Socket`, `Pipe`, `PipeReader/Writer`, `Channel<T>`, `ArrayPool<T>`, `MemoryPool<T>`, `IMemoryOwner<T>`, `Memory<T>`, `Span<T>`, `NetworkStream`, `SocketAsyncEventArgs`, `ValueTask`, `SemaphoreSlim`, `ConcurrentQueue/Dictionary` 등 네트워크·메모리 관련 모든 타입의 선언
- 주석 내용: "왜 이 타입/API를 골랐는가" → 반드시 **내부 동작 메커니즘**을 이유로 삼을 것 (단순 기능 설명 금지)

**예시:**

```csharp
// Channel<T>: lock-free MPSC 큐로 구현되어 있어 다수 IO 스레드 → 단일 디스패처 경로에서 락 경합 없이 메시지를 전달
private readonly Channel<IPacket> _dispatchChannel = Channel.CreateUnbounded<IPacket>();

// ArrayPool<byte>.Shared: 고정 크기 버킷 풀로 TLS(Thread-Local Storage) 슬롯을 우선 확인하므로
// 동일 스레드에서 반환·재사용 시 힙 할당 없이 O(1) 반환
private readonly byte[] _recvBuffer = ArrayPool<byte>.Shared.Rent(4096);

// SemaphoreSlim: 커널 전환 없이 스핀-대기 후 관리형 대기로 전환하는 경량 세마포어.
// 짧은 임계 구간에서 Mutex보다 컨텍스트 스위치 비용이 낮아 고빈도 송신 제한에 적합
private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
```

---

## 하네스: 종합 코드 리뷰

**목표:** 아키텍처·보안·성능·스타일 4개 에이전트가 병렬로 코드를 감사하고 단일 리포트로 통합한다.

**트리거:** 코드 리뷰, PR 검토, 코드 감사, 종합 리뷰 요청 시 `code-review-orchestrator` 스킬을 사용하라. 단순 질문(개념 설명 등)은 직접 응답 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | 종합 코드 리뷰 하네스 구축 |
| 2026-06-09 | 보안 가드 감사 | security-reviewer | 해킹·DDoS 공격 표면 점검 (리포트 plan/security_audit_0609.md) |

---

## 하네스: 동시성 가드 (.NET 10 고성능 서버)

**목표:** Lock-Free 설계 강제·락 정당화 주석 감사·데드락 정적 분석(생성-검증)을 에이전트 팀으로 조율하고 단일 동시성 리포트를 생성한다.

**트리거:** 동시성 검사, 락 감사, 데드락 분석, Lock-Free 검증, async 데드락, 컨텐션 분석 요청 시 `concurrency-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 서버 동시성 하네스 구축 |

---

## 하네스: GC 가드 (.NET 10 메모리 최적화)

**목표:** 힙 할당 스캐너·풀링 강제자 병렬 감사 → 교차 검증으로 GC 압력 유발 패턴을 제거하고 ValueTask·Span·ArrayPool을 올바르게 적용한다.

**트리거:** GC 억제, 힙 할당 감사, 메모리 최적화, ArrayPool 검사, ValueTask 검증, boxing 탐지, GC 압력 분석 요청 시 `gc-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 서버 GC 억제 메모리 최적화 하네스 구축 |

---

## 하네스: 파이프라인 아키텍처 (.NET 10 고성능 IO)

**목표:** System.IO.Pipelines 기반 Zero-copy IO 루프와 Channel<T> 락-프리 디스패처를 감독자 패턴으로 설계하고 부하 테스트 감사까지 수행한다.

**트리거:** Pipelines 설계, IO 루프 구현, 디스패처 설계, Zero-copy 서버, PipeReader 설계, Channel 디스패처 요청 시 `pipeline-architect-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 IO 파이프라인 아키텍처 하네스 구축 |

---

## 하네스: TDD (테스트 주도 개발)

**목표:** 요구사항 입력 시 Red(실패 테스트)→Green(최소 구현)→Refactor(검증·리팩토링) 사이클을 에이전트 팀으로 완주하고, harness-evolve로 명세 대비 최종 코드의 진화 델타를 포착한다.

**트리거:** TDD, 테스트 먼저 작성, Red-Green-Refactor, TDD 사이클, 기능 구현(TDD) 요청 시 `tdd-orchestrator` 스킬을 사용하라. 진화 리포트는 `/harness-evolve`로 수동 실행 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | TDD Red-Green-Refactor 하네스 구축 (harness-evolve 포함) |
