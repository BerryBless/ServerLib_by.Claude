## 1. 인터페이스 명세 (Interface Specifications)

ServerLib이 노출하는 공개 계약 전체입니다. 모든 인터페이스는 고성능 서버 라이브러리 설계 원칙(Zero-allocation hot path, `ValueTask` 반환, Thread-safety 명시)을 계약으로 강제합니다.

> 네임스페이스: `ServerLib.Interface` (단, 직렬화 계약인 `IPacket`·`IPacketSerializer`은 `ServerLib.Core.Serialization` — Interface 레이어가 Core를 역참조하지 않도록 직렬화 서브시스템에 함께 둔다)

---

### `ISession` — 클라이언트 세션
서버에 연결된 클라이언트 1개의 생명주기(연결 → 수신/송신 → 해제)를 정의합니다. `IAsyncDisposable`을 상속하며 Zero-allocation 설계를 계약으로 강제합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `Guid SessionId { get; }` | 세션 전역 고유 식별자 (불변) | Thread-safe, Zero-allocation |
| `EndPoint? RemoteEndPoint { get; }` | 클라이언트 원격 IP:포트 (UDP·로컬 파이프는 null 가능) | 연결 수립 후 불변 |
| `DateTimeOffset ConnectedAt { get; }` | 세션 수립 UTC 시각 (만료 판정·모니터링용) | Thread-safe, 불변 |
| `Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }` | 데이터 수신 시 호출 콜백 | I/O 스레드에서 호출; 반환 후 메모리 슬라이스 무효화; 동기 블로킹 금지 |
| `Func<ValueTask>? OnDisconnected { get; set; }` | 연결 종료 시 호출 콜백 | 세션 생명주기 동안 정확히 1회 발화 |
| `ValueTask SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | 데이터 비동기 전송 | Thread-safe, Zero-allocation, Non-blocking |

> 편의 오버로드: `session.SendAsync(packet)` (`PacketSendExtensions`, `ServerLib.Core.Serialization`) — `IPacket`을 직접 받아 직렬화·풀 버퍼 관리를 캡슐화한다. 송신 동기 완료 시 무할당. `IClientConnection`에도 동일 오버로드 제공. (hot loop는 1회 직렬화 후 `ReadOnlyMemory` 직접 송신이 유리.)

---

### `IServerListener` — TCP 서버 리스너
지정 포트에서 클라이언트 연결을 수락하고 세션 이벤트(연결·수신·해제)를 콜백으로 전달합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `bool IsRunning { get; }` | accept 루프 구동 여부 | Thread-safe 스냅샷 |
| `Func<ISession, ValueTask>? OnClientConnected { get; set; }` | 클라이언트 접속 시 콜백 | OnClientDisconnected보다 항상 먼저 발화 |
| `Func<ISession, ValueTask>? OnClientDisconnected { get; set; }` | 세션 해제 시 콜백 | 세션당 정확히 1회; 반환 후 세션 해제 |
| `Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }` | 데이터 수신 시 콜백 | 다수 세션 병렬 호출 가능; 메모리 슬라이스는 반환 후 무효화 |
| `void Start(int port)` | 지정 포트에서 연결 수락 시작 | Non-blocking; Not thread-safe |
| `void Stop()` | accept 루프 중지 및 소켓 종료 | Non-blocking; Not thread-safe |

---

### `IRpcHandler` — RPC 디스패처
수신된 패킷 페이로드를 패킷 ID 기준으로 적절한 RPC 핸들러로 라우팅합니다. `Rpc.Generator`의 Source Generator가 `[RpcService]` 인터페이스를 분석하여 구현체를 자동 생성합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `ValueTask DispatchAsync(ISession, ReadOnlyMemory<byte>, CancellationToken)` | 패킷 ID 앞 2바이트로 라우팅 | I/O 스레드 호출; 디스패치 자체는 Zero-allocation; Thread-safe |

---

### `IClientConnection` — 클라이언트 연결
원격 서버에 대한 단일 TCP 연결을 나타냅니다. `IAsyncDisposable`을 상속하며 System.IO.Pipelines 기반 Zero-copy 수신을 강제합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `bool IsConnected { get; }` | 서버 연결 여부 (스냅샷) | Thread-safe; 확인 직후 끊길 수 있으므로 방어적 처리 필요 |
| `Func<ValueTask>? OnConnected { get; set; }` | 연결 수립 직후 콜백 | OnDisconnected보다 항상 먼저 발화; 연결 실패 시 미발화 |
| `Func<ValueTask>? OnDisconnected { get; set; }` | 연결 종료 시 콜백 | 연결 수립 후 반드시 1회 발화 |
| `Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }` | 데이터 수신 시 콜백 | I/O 스레드 호출; 메모리는 반환 후 무효화 |
| `Task ConnectAsync(string host, int port, CancellationToken)` | 비동기 TCP 연결 수립 | 성공 시 수신 루프 자동 시작; Non-blocking; Not thread-safe |
| `ValueTask SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | 서버에 데이터 비동기 전송 | Zero-allocation; Thread-safe; Non-blocking |
| `void Disconnect()` | 연결 즉시 동기 종료 | Non-blocking; Thread-safe; 진행 중 I/O 강제 취소 |

---

### `IPacketSerializer` — 패킷 직렬화기
패킷 객체와 바이트 버퍼 사이의 직렬화/역직렬화를 정의합니다. 모든 연산이 `Span`/`ReadOnlySpan` 기반으로 동작하여 힙 할당 없는 Zero-copy를 보장합니다.

> 네임스페이스: `ServerLib.Core.Serialization` (`IPacket`과 함께 직렬화 서브시스템에 위치)

> 패킷 구조: `PacketId(2B) | BodyLength(2B) | Body(NB)` — LittleEndian

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `int Serialize<T>(T packet, Span<byte> destination) where T : IPacket` | 패킷을 destination에 직접 기록, 기록 바이트 수 반환 | Zero-allocation; Thread-safe; Non-blocking |
| `T Deserialize<T>(ReadOnlySpan<byte> source) where T : IPacket, new()` | 전체 패킷 버퍼 역직렬화 | T가 struct면 Zero-allocation; class면 1회 힙 할당; Thread-safe |
| `bool TryReadPacketLength(ReadOnlySpan<byte> header, out int totalLength)` | 헤더 파싱으로 전체 패킷 길이 반환 (부분 수신 판단용) | Zero-allocation; Thread-safe |

---

### `ISessionRegistry` — 세션 레지스트리 (읽기)
서버에 연결된 활성 세션 전체를 추적합니다. 외부 소비자용 읽기 전용 인터페이스입니다. 등록·해제는 `ISessionRegistrar`를 통해 수행됩니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `int Count { get; }` | 현재 연결된 세션 수 | Thread-safe; Zero-allocation |
| `bool TryGet(Guid sessionId, out ISession? session)` | SessionId로 세션 조회 (없으면 false) | Thread-safe; Zero-allocation |
| `IReadOnlyCollection<ISession> GetAll()` | 호출 시점 활성 세션 스냅샷 반환 | Thread-safe; 호출마다 배열 할당 — hot path 반복 호출 금지 |
| `ValueTask BroadcastAsync(ReadOnlyMemory<byte>, CancellationToken)` | 전체 세션 병렬 전송 (개별 실패는 무시, `OperationCanceledException`은 전파) | Thread-safe; 호출마다 스냅샷·ValueTask[] 할당 |

---

### `ISessionRegistrar` — 세션 레지스트리 (쓰기)
세션 등록·해제 전용 인터페이스입니다. `SocketPipelineListener` 내부에서만 사용하며 외부 코드는 `ISessionRegistry`만 참조해야 합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `void Register(ISession session)` | 세션을 레지스트리에 등록 | Thread-safe; Zero-allocation; Non-blocking |
| `void Unregister(Guid sessionId)` | 세션을 레지스트리에서 제거 (없는 ID여도 예외 없음) | Thread-safe; Zero-allocation; Non-blocking |

---

### `IPacket` — 네트워크 패킷 계약
바이너리 직렬화 가능한 패킷의 최소 계약입니다. 구현체는 헤더를 제외한 본문(body)만 직렬화·역직렬화하며, 헤더 기록은 `BinaryPacketSerializer`가 담당합니다.

> 네임스페이스: `ServerLib.Core.Serialization`

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `ushort PacketId { get; }` | 패킷 식별자 — RpcDispatcher 라우팅 키, 헤더 첫 2바이트에 기록 | 불변 |
| `void Serialize(ref SpanWriter writer)` | 패킷 본문 필드를 순서대로 기록 (헤더 미포함) | 문자열 외 Zero-allocation; Non-blocking |
| `void Deserialize(ref SpanReader reader)` | reader에서 본문 필드를 순서대로 읽어 채움 | Serialize와 동일 순서 유지 필수 |
| `int GetBodySize()` | 직렬화에 필요한 본문 바이트 수 반환 (헤더 제외) | Zero-allocation; 사전 버퍼 대여에 사용 |

---

## 2. 하네스 & 플러그인

### 하네스 (Harnesses)
도메인별 에이전트 팀을 조율하는 자동화 파이프라인입니다. 트리거 키워드를 입력하면 오케스트레이터 스킬이 자동 실행됩니다.

| 이름 | 오케스트레이터 스킬 | 기능 요약 |
|------|-------------------|----------|
| **Git 자동 커밋 & 푸시** | `commitandpush` | 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋·푸시 파이프라인. 민감 정보 감지 시 커밋 차단 |
| **종합 코드 리뷰** | `code-review-orchestrator` | 아키텍처·보안·성능·스타일 4개 에이전트 병렬 감사 → 단일 통합 리포트 생성 |
| **동시성 가드** | `concurrency-guard-orchestrator` | Lock-Free 설계 강제 · 락 정당화 주석 감사 · 데드락 정적 분석(생성-검증) → 단일 동시성 리포트 |
| **GC 가드** | `gc-guard-orchestrator` | 힙 할당 스캐너 · 풀링 강제자 병렬 감사 → 교차 검증으로 GC 압력 유발 패턴 제거 |
| **파이프라인 아키텍처** | `pipeline-architect-orchestrator` | Pipelines 기반 Zero-copy IO 루프 + Channel\<T\> 락-프리 디스패처 설계 및 부하 테스트 감사 |
| **TDD** | `tdd-orchestrator` | Red(실패 테스트) → Green(최소 구현) → Refactor(검증) 사이클을 에이전트 팀으로 완주 + 진화 델타 포착 |

### 플러그인 (Plugins)

| 이름 | 마켓플레이스 | 기능 요약 |
|------|------------|----------|
| **harness** | harness-marketplace | 전문 에이전트 정의 + 해당 에이전트용 스킬 생성 — 도메인 하네스 구축·점검·동기화를 위한 메타 도구 |
| **superpowers** | claude-plugins-official | `brainstorming` · `test-driven-development` · `writing-plans` · `requesting-code-review` · `using-git-worktrees` 등 범용 개발 워크플로 스킬 모음 |
