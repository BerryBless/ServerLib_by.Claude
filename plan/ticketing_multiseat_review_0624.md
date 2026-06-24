# 종합 코드 리뷰 리포트
**생성:** 2026-06-24  |  **대상:** commit `05a7a0b` — 배치 멀티 좌석 티켓팅 (세션당 N좌석 All-or-nothing 예약·결제)  
**베이스:** `cba2d94` (티켓팅 모니터링 코드리뷰 수정 완료)

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | 63 / 100 | 0 | 2 | 4 | 1 |
| 🔒 보안 | 68 / 100 | 0 | 0 | 3 | 2 |
| ⚡ 성능 | 83 / 100 | 0 | 0 | 1 | 5 |
| 🎨 스타일 | 72 / 100 | — | 1 | 3 | 4 |
| **종합** | **71 / 100** | **0** | **3** | **11** | **12** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [아키텍처] HIGH — ARCH-BATCH-01: 배치 경로에서 Row/Col→seatId 변환이 컨트롤러로 누출

**위치:** `Server/Program.cs:343-360`  
**문제:** 단일 좌석 경로(`TryReserveByRowCol`)는 경계 검증과 좌표 변환(`seatId = row * _cols + col`)을 `TicketInventory` 도메인 내에서 수행한다. 그런데 배치 경로는 `TryReserveBatch`가 이미 변환된 `seatId[]`를 받기 때문에 `Server/Program.cs`가 경계 검증과 변환을 직접 구현한다. 동일한 도메인 규칙이 인프라 레이어에 중복 존재하며, `Cols` 변경 시 두 경로를 모두 수정해야 한다.  
**수정:** `TryReserveBatchByRowCol(TicketContext ctx, ReadOnlySpan<byte> rows, ReadOnlySpan<byte> cols, Span<int> reservedOut)`을 `TicketInventory`에 추가하고 내부에서 경계 검증·변환 후 `TryReserveBatch`에 위임. `Server/Program.cs`는 raw rows/cols 전달만 담당.

---

### [아키텍처] HIGH — ARCH-BATCH-02: Rate-limit 집행 로직이 컨트롤러 인라인 — SRP 위반

**위치:** `Server/Program.cs:304-321`, `Ticketing/TicketContext.cs:56-65`  
**문제:** Rate-limiting 상태(`RateLimitWindowStart`, `RateLimitAttempts`)는 `TicketContext`(도메인 객체)에 공개 가변 필드로 선언되어 있고, 슬라이딩 윈도우 교체(`Interlocked.CompareExchange`) 및 초과 판단 로직은 `Server/Program.cs:304-311`에 13줄 인라인으로 구현된다. 정책 상수는 도메인에, 집행 코드는 컨트롤러에 있어 도메인만 단위 테스트할 수 없다.  
**수정:** `TicketInventory` 또는 별도 `RateLimiter` 타입에 `bool TryConsumeRateLimit(TicketContext ctx)` 메서드를 추가하여 캡슐화. `RateLimitWindowStart`, `RateLimitAttempts`를 비공개로 전환.

---

### [스타일] HIGH — STYLE-BATCH-01: public 필드 RateLimitWindowStart·RateLimitAttempts에 XML 문서 주석 누락

**위치:** `Ticketing/TicketContext.cs:56-59`  
**문제:** 두 필드는 `public`임에도 `// inline` 주석만 보유. CLAUDE.md 규칙은 모든 public 멤버에 XML `<summary>` 및 Thread Safety `<remarks>` 요구. `Interlocked` 연산으로만 접근하는 필드이므로 동시성 의미론 기술 필수.  
**수정:** `///` XML 문서 주석으로 교체. `<remarks>`에 "쓰기는 `Interlocked.CompareExchange(long)`으로만 수행", "Interlocked.Increment로만 증가" 명시.

---

## Medium 발견사항 ← 권장 수정

### [아키텍처]

- **ARCH-BATCH-03** `TicketInventory.cs:409-416` — `Confirm()` 래퍼 문서가 "첫 번째 슬롯만 확정"으로 기술되나 구현은 `ConfirmAll`을 호출하여 모든 슬롯을 확정. 테스트에서 `maxSeatsPerSession=1`만 사용해 드러나지 않음. 문서를 "모든 슬롯 확정 후 첫 번째 반환"으로 수정하거나 `[Obsolete]` 추가.
- **ARCH-BATCH-04** `Server/Program.cs:326` — `req.Count > tctx.Slots.Length` 암묵적 의존으로 설계 의도 불명확. `TicketContext`에 `public int MaxSeats => Slots.Length;` 추가 후 `req.Count > tctx.MaxSeats`로 교체.
- **ARCH-BATCH-05** `TicketInventory.cs:340-441` — 하위호환 래퍼 5종(`TryReserve`, `TryReserveByRowCol`, `Confirm`, `Release`, `ReleaseByContext`)이 프로덕션에서 호출되지 않으나 `[Obsolete]` 미표시. 외부 소비자가 배치 API 대신 래퍼를 선택할 위험.
- **ARCH-BATCH-06** `TicketReserveRequestPacket.cs:49-63` — `Count`, `Rows.Length`, `Cols.Length` 병렬 배열 공동 제약 미강제. `Serialize`에서 `Math.Min`으로 조용히 처리되어 선언-실제 크기 불일치. 생성자 또는 팩토리 메서드로 불변식 강제.

### [보안]

- **SEC-BATCH-01** `TicketContext.cs:56-59` — 세션 재연결로 슬라이딩 윈도우 속도 제한 완전 우회 가능 (CWE-307). `new TicketContext` 호출 시 카운터 초기화. 서버 수준 `ConcurrentDictionary<string, RateLimitState>`로 username/IP 기반 관리 필요.
- **SEC-BATCH-02** `AppConfig/ServerConfig.cs:118-119` — MySQL root 계정 기본 비밀번호 `password123` 하드코딩 (CWE-798). 기본값을 빈 문자열 또는 `REPLACE_ME`로 변경, 비어 있으면 기동 중단.
- **SEC-BATCH-03** `AppConfig/ServerConfig.cs:137-140` — 시드 사용자 `admin/password123` 하드코딩 (CWE-798). `SeedTestUser=true` + 기본 비밀번호 그대로면 `InvalidOperationException` throw.

### [성능]

- **PERF-MON-01** `Server/Program.cs Reserve/Pay 핸들러` — per-request 힙 할당 (`new int[req.Count]` 3-4개). `ArrayPool<T>.Shared.Rent(MaxSeatsPerSession)` + try/finally `Return` 패턴으로 교체. 버스트 시 GEN0 GC 누적 압박 완화.

### [스타일]

- **STYLE-BATCH-02** `TicketInventory.cs:290-312` — `ReleaseAll`에 `<remarks>` 동시성 블록 누락. `ConfirmAll`·`ReleaseAllByContext`와 문서화 수준 불일치.
- **STYLE-BATCH-03** `TicketInventory.cs:184-257` — `TryReserveBatch` ~70줄·6단계 책임 혼재(검증·예약 루프·롤백·KPI). 검증 단계를 `private TryValidateSeatIds`로 분리하여 ~35줄로 축소.
- **STYLE-BATCH-04** `Server/Program.cs` 5개 지점 — 실패 응답 `TicketResultPacket` 생성 패턴 5회 중복. `static FailResult(TicketStatus s, int free)` 헬퍼로 통일.

---

## Low / 정보성 ← 검토 권장

- [아키텍처] `TicketResultPacket.cs:44` — `NoSlot = 0xFF` 레거시 상수, 배치 규약과 혼용 가능. `[Obsolete]` 또는 `internal` 전환.
- [보안] `TicketReserveRequestPacket.cs:86-97` — Count-배열 길이 불일치 시 세션 종료 (CWE-130). 방어적 `ArgumentException` 추가.
- [보안] `AppConfig/ServerConfig.cs:109` — `MaxSeatsPerSession` 상한 미검증 `stackalloc` (CWE-1188). `Math.Min(MaxSeatsPerSession, byte.MaxValue)` cap.
- [보안] `Server/Program.cs` — 오류 응답에 잔여 좌석 수 노출 (CWE-209). 실패 시 `Remaining=0` 고정 고려.
- [성능] `TicketInventory.cs:219-221` — `TryReserveBatch` 빈 슬롯 탐색 inner 루프 dead work. `int entry = i;`로 대체.
- [성능] `Server/Program.cs` Reserve/Pay — LINQ + `string.Join` 로깅 성공 경로마다 실행. 조건부 실행 또는 `Span<char>` 패턴.
- [성능] `TicketInventory.cs ConfirmAll/ReleaseAll` — 독점 소유권 확보 후 `Interlocked.Exchange(_states)` 대신 `Volatile.Write` 충분. ARM 배포 시 우선순위 상승.
- [스타일] `TicketInventory.cs` 전체 — 빈 슬롯 센티넬 `-1` 약 15곳 리터럴. `private const int EmptySlot = -1;` 상수화.
- [스타일] `TicketInventoryConcurrencyTests.cs` — 폐기된 설계 탐색 주석 6줄 잔존.
- [스타일] `TicketInventoryConcurrencyTests.cs` — `"SlotIndex 오염 없음"` 레거시 주석이 `Slots[0]` 기반 코드로 수정 후에도 갱신 안 됨.
- [스타일] `TicketInventory.cs:211` — `entryForIndex` 이름 불명확. `ctxSlotPerSeat` 또는 `slotEntryBySeat`로 교체.
- [스타일] STYLE-03 잔존 — 신규 테스트 8건에 `row=0/col=0` 최소 경계 케이스 미포함.

---

## 이전 리뷰 추적

| ID | 이전 심각도 | 상태 |
|----|------------|------|
| ARCH-NEW-01 | Medium | ✅ 해소 — `ProjectSeatStates()` 캡슐화 완료 |
| SEC-MON-01 | High | ✅ 해소 — 관리 포트 루프백 바인딩 완료 |
| SEC-MON-02/03 | Medium | ✅ 해소 — MaxConnections/XSS 수정 완료 |
| ARCH-01 | Medium | ❌ 미해소 — `TicketStatus`가 여전히 Serialization 레이어에 위치 |
| SEC-NEW-03 | Medium | ⚠️ 부분 해소 — 세션 내 Rate Limit 구현됐으나 재연결 우회 가능 (→ SEC-BATCH-01) |
| STYLE-01 | Medium | ✅ 해소 — `RunConcurrentAsync` 헬퍼 추출 완료 |

---

## 총평 및 판정

배치 멀티 좌석 티켓팅의 핵심 lock-free 설계(`TryReserveBatch` All-or-nothing, `Slots[]` per-element CAS 앵커, ABA-safe 롤백, `SweepExpired` exactly-one-winner)는 정확하고 견고하다. `stackalloc` 기반 중복 좌석 검증, `SpanReader` 경계 검사, `Count` 상한 사전 차단 등 보안 핵심 경로도 올바르게 구현되었다.

주요 개선 우선순위는 두 가지다: (1) 단일 좌석 경로에서 도메인이 소유하던 Row/Col→seatId 변환·경계 검증이 배치 경로에서 `Server/Program.cs`로 이전된 레이어 퇴행(ARCH-BATCH-01), (2) Rate-limit 집행 로직이 도메인 상태와 컨트롤러 코드에 분산된 SRP 위반(ARCH-BATCH-02). 보안 측면에서는 세션 재연결로 슬라이딩 윈도우를 우회할 수 있는 설계 갭(SEC-BATCH-01)과 설정 파일 기본 자격증명 하드코딩(SEC-BATCH-02/03)이 다음 스프린트 목표다. 성능은 버스트 구간 per-request 힙 할당(PERF-MON-01)을 ArrayPool로 교체하면 GC 스파이크가 감소한다.

**판정: REQUEST CHANGES**
- Critical 0건, High 3건 발견 (머지 전 수정 권장)
- 종합 점수 71/100 (60–79 범위)

---

## 상세 산출물

| 파일 | 내용 |
|------|------|
| `_workspace/02_architecture_findings.json` | 아키텍처 감사 원본 (score: 63) |
| `_workspace/02_security_findings.json` | 보안 감사 원본 (score: 68) |
| `_workspace/02_performance_findings.json` | 성능 감사 원본 (score: 83) |
| `_workspace/02_style_findings.json` | 스타일 감사 원본 (score: 72) |
