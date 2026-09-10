# ClaudeCodeStudy 프로젝트

## 프로젝트 개요

**목표:** ProudNet과 같은 고성능 서버 라이브러리 개발 (.NET 10 기반)

**원칙:** Interface는 순수 추상화만, Core는 구현만 포함. 의존성 방향은 Core → Interface (역방향 금지).

**구성(2026-09-11 라이브러리 핵심만 남기고 정리):** 이 저장소는 `ServerLib` 라이브러리 본체와, 이를 실소켓으로 검증하며 사용 예제 역할을 겸하는 통합 테스트 `EchoExample.Tests`로 구성된다. 데모 앱(과거 Echo/Counter 서버·클라이언트, EchoWeb, AuthServer, Mob·티켓팅 호스트 등)은 이 정리에서 제거됐다.
- `EchoExample.Tests/` — **ServerLib 통합 테스트 겸 사용 예제**: 127.0.0.1 루프백 실소켓으로 `ServerNet.CreateListener()`/`CreateClient()`를 구동해 `EchoPacket`(Id=1) 왕복·유니코드/빈 문자열·최대 프레임(65,539B) 데드락 회귀·연속 N개 순서 보존을 검증한다. 라이브러리의 실제 사용 패턴(콜백 배선, `SendAsync<T>` 직렬화, 프레이밍, `await using` 정리)을 그대로 보여주므로 예제 문서 역할을 겸한다.

**캡슐화(v1.1.0~):** Transport 구현체(`SocketPipelineListener`/`~Client`/`~Session`)와 `SessionRegistry`는 `internal`. 외부 소비자는 `ServerNet` 팩토리가 반환하는 인터페이스로만 사용한다. 직렬화 빌딩블록(`IPacket`·`IPacketSerializer`·`BinaryPacketSerializer`·패킷 타입·`PacketPool`)은 public. 새 Transport 진입점을 추가하면 `ServerNet` 팩토리에도 생성 메서드를 노출할 것.

**패킷 타입(2026-09-11 데모 패킷 정리 완료):** `ServerLib/Core/Serialization/Packets/`에는 코어에 필요한 `EchoPacket`(예제·테스트)·`PingPacket`·`PongPacket`(하트비트)만 남는다. 데모 전용 패킷은 모두 제거됐다. 데모/애플리케이션 전용 패킷은 `IPacket`을 구현해 소비 측에 두는 것을 권장한다.

새 public API를 추가하면 `EchoExample.Tests`에 사용·검증 케이스를 함께 추가할 것.

## 하네스: Git 자동 커밋 & 푸시 (Git Automator)

**목표:** 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋 & 푸시를 파이프라인으로 자동화한다.

**트리거:** `/commitandpush`, 커밋해줘, 푸시해줘, 변경사항 올려줘, 깃 커밋 요청 시 `commitandpush` 스킬을 사용하라.

**커밋 규칙 (Codex 세션 전용 — 필수 행동 규칙):**
`auto-commit.ps1` Stop 훅은 **Claude Code 전용**이라 Codex 세션에서는 실행되지 않는다. 커밋 요청 시 Codex는 아래 형식으로 **직접 `git commit`** 한다.
- 형식: `{접두사}: {제목}` (접두사: 추가/수정/버그수정/리팩토링/문서/테스트/의존성)
- 제목: 50자 이내, 파일명 나열 금지, WHY 중심
- 본문(선택): `- ` 항목 나열
- 마지막 줄(필수): `Co-Authored-By: Codex <noreply@openai.com>`

**주의:** `.git/auto_commit_msg.txt`는 Claude Code Stop 훅의 전달 채널이므로 Codex는 이 파일을 절대 생성·수정하지 않는다 (남겨두면 다음 Claude 세션의 훅이 엉뚱한 메시지로 커밋한다).

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-03 | 초기 구성 | 전체 | Git 자동 커밋&푸시 파이프라인 구축 |
| 2026-06-03 | 파일 기반 메시지 전달로 재설계 | auto-commit.ps1 | nested claude -p 콜드스타트/stdin 취약성으로 폴백 빈발 |
| 2026-09-10 | Codex 직접 커밋 규칙으로 분기 | AGENTS.md | Stop 훅은 Claude Code 전용이라 Codex 세션에서 메시지 파일이 잔류하는 문제 방지 |

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
| `plan/contention_counter_0910.md` | 2026-09-10 | 경합 카운터 서버 (CounterServer 9300 루프백·CounterClient·CounterExample.Tests 신규 3개 + sln 등록, Interlocked `CounterState`(Value·AppliedOps), CounterQueryPacket Id=18(0B)/CounterValuePacket Id=19(16B), 연결별 조회 배리어 후 정지 상태 검증, 26개 신규 테스트) |

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
