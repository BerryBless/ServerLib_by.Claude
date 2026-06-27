# ServerLib.Tests 종합 코드 리뷰 구현 플랜

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ServerLib.Tests 176개 테스트의 품질 감사 + 커버리지 갭 분석을 수행하고, Critical/Important 항목을 즉시 수정해 테스트 스위트를 강화한다.

**Architecture:** 3단계 파이프라인 — (1) 도메인 그룹별 병렬 감사 → 구조화 findings 파일 3개, (2) 통합 합산 → plan/test_review_0628.md, (3) 품질 수정 + 커버리지 갭 추가 → commit.

**Tech Stack:** .NET 10, xUnit 2.9.2, C#, ServerLib/Auth/Ticketing 프로덕션 코드

## Global Constraints

- .NET 10 / net10.0 타겟
- xUnit 2.9.2 — `[Fact]`, `[Theory]`, `[InlineData]` 사용
- **프로덕션 코드 절대 수정 금지** — 테스트 파일만 변경
- 신규 테스트는 `dotnet test` 실행 시 처음부터 PASS해야 함 (프로덕션 구현이 이미 존재)
- Minor 항목은 리포트에만 기록, 코드 수정 제외
- 모든 신규 테스트는 기존 테스트와 독립적으로 실행 가능해야 함 (실행 순서 의존 금지)
- CLAUDE.md 주석 규칙 불필요 (테스트 파일은 적용 대상 외)

---

### Task 1: 직렬화 도메인 감사

**Files:**
- Read (테스트): `ServerLib.Tests/SpanReaderWriterTests.cs`
- Read (테스트): `ServerLib.Tests/BinaryPacketSerializerTests.cs`
- Read (테스트): `ServerLib.Tests/PacketRoundTripTests.cs`
- Read (테스트): `ServerLib.Tests/PacketPoolTests.cs`
- Read (프로덕션): `ServerLib/Core/Serialization/SpanReader.cs`
- Read (프로덕션): `ServerLib/Core/Serialization/SpanWriter.cs`
- Read (프로덕션): `ServerLib/Core/Serialization/BinaryPacketSerializer.cs`
- Read (프로덕션): `ServerLib/Core/Serialization/PacketPool.cs` (존재하면)
- Read (프로덕션): `ServerLib/Core/Serialization/Packets/` 주요 파일들
- Output: `.superpowers/sdd/audit-group1.md`

**Interfaces:**
- Produces: `.superpowers/sdd/audit-group1.md` (Task 4가 소비)

- [ ] **Step 1: 테스트 파일 4개 전부 읽기**

`SpanReaderWriterTests.cs`, `BinaryPacketSerializerTests.cs`, `PacketRoundTripTests.cs`, `PacketPoolTests.cs` 전부 읽는다.

- [ ] **Step 2: 대응 프로덕션 코드 읽기**

`SpanReader.cs`, `SpanWriter.cs`, `BinaryPacketSerializer.cs`, `PacketPool.cs`(또는 `PacketSendExtensions.cs`) 읽는다.

- [ ] **Step 3: 품질 감사 수행**

각 테스트 파일에 대해 다음 항목을 확인한다:

**품질 체크리스트:**
1. Assert 없는 테스트 — `[Fact]` 메서드 body에 `Assert.*` 호출이 없는 것
2. 의미없는 Assertion — `Assert.True(true)`, `Assert.Equal(1, 1)` 등 항상-true
3. 테스트명이 의도를 설명하지 않음 — `Test1()`, `Method_Works()` 수준의 무의미한 이름
4. 경계값 누락 — `WriteInt32(-1)`, `WriteInt32(int.MaxValue)`, `WriteString("")`, `WriteString(null)` 등 누락
5. 실행 순서 의존 — 이전 테스트가 남긴 상태(static field 등)에 의존
6. Arrange-Act-Assert 위반 — 하나의 테스트에 여러 Act+Assert 반복

- [ ] **Step 4: 커버리지 갭 분석**

프로덕션 코드의 public 메서드/프로퍼티를 목록화하고 테스트 파일에서 커버되지 않는 것을 찾는다:

**갭 체크리스트:**
1. SpanWriter/SpanReader — WriteBool/ReadBool, WriteString edge (빈 문자열, 최대 길이), ReadString 오버플로우 방어
2. BinaryPacketSerializer — 잘못된 패킷 ID로 Deserialize 시도, bodyLength=0 패킷, 버퍼 언더플로우 (너무 짧은 배열)
3. PacketPool — HeaderSize 상수 검증, Rent/Return 패턴 (존재하면)
4. PacketRoundTrip — 아직 테스트되지 않은 패킷 타입 (프로덕션에 있으나 테스트 없는 것)

- [ ] **Step 5: findings 파일 작성**

`.superpowers/sdd/audit-group1.md`를 생성하고 다음 형식으로 작성:

```markdown
# Group 1: 직렬화 도메인 감사 결과

## 품질 이슈
- [Critical] 파일명:테스트명 — 문제 설명 — 권장 수정
- [Important] 파일명:테스트명 — 문제 설명 — 권장 수정
- [Minor] 파일명:테스트명 — 문제 설명

## 커버리지 갭
- [Critical] 대상: ClassName.MethodName — 누락 시나리오 — 제안 테스트명
- [Important] 대상: ClassName.MethodName — 누락 시나리오 — 제안 테스트명
- [Minor] 대상: ClassName.MethodName — 누락 시나리오

## 요약
- 품질 이슈: Critical N건, Important N건, Minor N건
- 커버리지 갭: Critical N건, Important N건, Minor N건
```

---

### Task 2: 인증·DB·세션 도메인 감사

**Files:**
- Read (테스트): `ServerLib.Tests/LoginServiceTests.cs`
- Read (테스트): `ServerLib.Tests/DbMetricsTests.cs`
- Read (테스트): `ServerLib.Tests/SessionRegistryTests.cs`
- Read (테스트): `ServerLib.Tests/SessionStateTests.cs`
- Read (프로덕션): `Auth/LoginService.cs`
- Read (프로덕션): `Auth/PasswordHasher.cs`
- Read (프로덕션): `Auth/DbMetrics.cs`
- Read (프로덕션): `ServerLib/Core/SessionRegistry.cs`
- Read (프로덕션): `ServerLib/Interface/SessionState.cs`
- Output: `.superpowers/sdd/audit-group2.md`

**Interfaces:**
- Produces: `.superpowers/sdd/audit-group2.md` (Task 4가 소비)

- [ ] **Step 1: 테스트 파일 4개 전부 읽기**

`LoginServiceTests.cs`, `DbMetricsTests.cs`, `SessionRegistryTests.cs`, `SessionStateTests.cs` 전부 읽는다.

- [ ] **Step 2: 대응 프로덕션 코드 읽기**

`Auth/LoginService.cs`, `Auth/PasswordHasher.cs`, `Auth/DbMetrics.cs`, `ServerLib/Core/SessionRegistry.cs`, `ServerLib/Interface/SessionState.cs` 읽는다.

- [ ] **Step 3: 품질 감사 수행**

Task 1과 동일한 품질 체크리스트를 적용한다. 추가로 인증 도메인 특화 항목:

1. **LoginService 타이밍 공격 방어 테스트** — 존재하지 않는 사용자와 잘못된 비밀번호의 응답 지연이 일관한지
2. **DbMetrics 동시성** — 여러 스레드에서 `Record*` 동시 호출 시 카운트 정확성
3. **SessionRegistry 동시성** — `Register`/`Unregister` race 조건

- [ ] **Step 4: 커버리지 갭 분석**

**갭 체크리스트:**
1. LoginService — `LoginAsync` 취소 토큰(ct) 전달 후 취소 동작, 빈 username/password
2. DbMetrics — `GetSnapshot()` zero-count 케이스 (이미 있는지 확인), 동시성 (Parallel.For로 Record* 호출 후 count 검증)
3. SessionRegistry — `BroadcastAsync` 대상 0개일 때, 이미 등록된 세션 재등록 시도
4. SessionState — 모든 enum 값이 테스트에서 사용되는지 (누락 값 확인)

- [ ] **Step 5: findings 파일 작성**

`.superpowers/sdd/audit-group2.md`를 Task 1과 동일한 형식으로 작성한다.

---

### Task 3: RPC·메트릭·티켓팅 도메인 감사

**Files:**
- Read (테스트): `ServerLib.Tests/RpcDispatcherTests.cs`
- Read (테스트): `ServerLib.Tests/ServerMetricsTests.cs`
- Read (테스트): `ServerLib.Tests/TicketInventoryConcurrencyTests.cs`
- Read (테스트): `ServerLib.Tests/TicketPacketRoundTripTests.cs`
- Read (프로덕션): `ServerLib/Core/Rpc/RpcDispatcher.cs`
- Read (프로덕션): `ServerLib/Core/ServerMetrics.cs`
- Read (프로덕션): `Ticketing/TicketInventory.cs`
- Read (프로덕션): `Ticketing/TicketContext.cs`
- Read (프로덕션): `ServerLib/Core/Serialization/Packets/` 내 티켓 패킷 파일들
- Output: `.superpowers/sdd/audit-group3.md`

**Interfaces:**
- Produces: `.superpowers/sdd/audit-group3.md` (Task 4가 소비)

- [ ] **Step 1: 테스트 파일 4개 전부 읽기**

`RpcDispatcherTests.cs`, `ServerMetricsTests.cs`, `TicketInventoryConcurrencyTests.cs`, `TicketPacketRoundTripTests.cs` 전부 읽는다.

- [ ] **Step 2: 대응 프로덕션 코드 읽기**

`RpcDispatcher.cs`, `ServerMetrics.cs`, `TicketInventory.cs`, `TicketContext.cs`, 티켓 패킷 파일들 읽는다.

- [ ] **Step 3: 품질 감사 수행**

Task 1과 동일한 품질 체크리스트 + 동시성 특화:

1. **TicketInventory 동시성 테스트** — 46개 테스트가 실제로 race condition을 커버하는지, 아니면 단순 순차 테스트인지
2. **ServerMetrics Reset()** — Reset 후 카운터가 0으로 초기화되는지 테스트 존재 여부
3. **RpcDispatcher 중복 핸들러 등록** — 동일 PacketId로 두 번 등록 시 동작

- [ ] **Step 4: 커버리지 갭 분석**

**갭 체크리스트:**
1. RpcDispatcher — 핸들러 없는 PacketId 수신 시 동작 (예외 vs 무시), async 핸들러 예외 전파
2. ServerMetrics — `Reset()` 메서드 테스트 존재 여부, `OnBytesSent(-1)` 음수 값
3. TicketInventory — Rate Limit 경계 (MaxReserveAttemptsPerWindow 정확히 N회째에 RateLimited), TTL 스위퍼 `SweepExpired` 단독 테스트
4. TicketPacket — `TicketResult` Count=0xFF (최대값), Slots null vs empty 구분

- [ ] **Step 5: findings 파일 작성**

`.superpowers/sdd/audit-group3.md`를 Task 1과 동일한 형식으로 작성한다.

---

### Task 4: 통합 감사 리포트 합산

**Files:**
- Read: `.superpowers/sdd/audit-group1.md`
- Read: `.superpowers/sdd/audit-group2.md`
- Read: `.superpowers/sdd/audit-group3.md`
- Create: `plan/test_review_0628.md`

**Interfaces:**
- Consumes: audit-group1/2/3.md
- Produces: `plan/test_review_0628.md` (Tasks 5·6가 소비)

- [ ] **Step 1: 3개 감사 파일 읽기**

`.superpowers/sdd/audit-group1.md`, `audit-group2.md`, `audit-group3.md` 전부 읽는다.

- [ ] **Step 2: 중복 제거 및 우선순위 정렬**

동일 대상에 대한 중복 발견을 제거하고, severity별로 정렬한다.

- [ ] **Step 3: 통합 리포트 작성**

`plan/test_review_0628.md`를 다음 형식으로 작성한다:

```markdown
# ServerLib.Tests 종합 코드 리뷰 결과

**날짜:** 2026-06-28
**감사 대상:** ServerLib.Tests 176개 테스트 (11개 파일)

## 요약
- 총 품질 이슈: Critical N건, Important N건, Minor N건
- 총 커버리지 갭: Critical N건, Important N건, Minor N건

## 품질 이슈 (Critical)
### [QUALITY-C-01] 파일명:테스트명
- **문제:** 설명
- **권장 수정:** 구체적 수정 방법
- **수정 대상 파일:** ServerLib.Tests/파일명.cs

## 품질 이슈 (Important)
### [QUALITY-I-01] ...

## 품질 이슈 (Minor) — 코드 수정 제외, 기록만
### [QUALITY-M-01] ...

## 커버리지 갭 (Critical)
### [GAP-C-01] 대상: ClassName.MethodName
- **누락 시나리오:** 설명
- **제안 테스트명:** `Method_Scenario_ExpectedBehavior`
- **추가 대상 파일:** ServerLib.Tests/파일명.cs

## 커버리지 갭 (Important)
### [GAP-I-01] ...

## 커버리지 갭 (Minor) — 코드 수정 제외, 기록만
### [GAP-M-01] ...

## 수정 대상 요약 (Tasks 5·6용)
수정 필요: QUALITY-C-01, QUALITY-C-02, ..., QUALITY-I-01, ...
추가 필요: GAP-C-01, GAP-C-02, ..., GAP-I-01, ...
```

- [ ] **Step 4: 커밋**

```
git add plan/test_review_0628.md
git commit -m "문서: ServerLib.Tests 종합 코드 리뷰 감사 리포트"
```

---

### Task 5: 품질 이슈 수정

**Files:**
- Read: `plan/test_review_0628.md`
- Modify: `ServerLib.Tests/` 내 해당 파일들 (리포트에서 지정된 것)

**Interfaces:**
- Consumes: `plan/test_review_0628.md` — `[QUALITY-C-*]`·`[QUALITY-I-*]` 항목

- [ ] **Step 1: 리포트 읽기**

`plan/test_review_0628.md`를 읽고 `[QUALITY-C-*]`와 `[QUALITY-I-*]` 항목 목록을 파악한다.

- [ ] **Step 2: 대상 테스트 파일 읽기**

수정이 필요한 테스트 파일들을 읽는다.

- [ ] **Step 3: 품질 이슈 수정 적용**

각 항목에 대해 다음 유형의 수정을 적용한다:

**Assert 없는 테스트 → Assert 추가:**
```csharp
// Before: Assert 없음
[Fact]
public void SomeMethod_Works() 
{
    var result = _subject.SomeMethod();
    // 아무 검증 없음
}

// After:
[Fact]
public void SomeMethod_Returns_Expected()
{
    var result = _subject.SomeMethod();
    Assert.NotNull(result);         // 또는 실제 기대값으로
    Assert.Equal(expected, result);
}
```

**의미없는 Assertion → 실질적 검증으로 교체:**
```csharp
// Before:
Assert.True(true);

// After:
Assert.Equal(expectedValue, actualValue);
```

**테스트명 개선:**
```csharp
// Before:
[Fact]
public void Test1() { ... }

// After:
[Fact]
public void Serialize_EmptyPacket_WritesHeaderOnly() { ... }
```

- [ ] **Step 4: 수정 후 테스트 실행**

```
dotnet test ServerLib.Tests -v minimal 2>&1 | tail -10
```
Expected: 통과! 실패: 0

- [ ] **Step 5: 커밋**

```
git add ServerLib.Tests/
git commit -m "테스트: ServerLib.Tests 품질 이슈 수정 (QUALITY-C/I 항목)"
```

---

### Task 6: 커버리지 갭 테스트 추가

**Files:**
- Read: `plan/test_review_0628.md`
- Modify: `ServerLib.Tests/` 내 해당 파일들 (리포트에서 지정된 것)

**Interfaces:**
- Consumes: `plan/test_review_0628.md` — `[GAP-C-*]`·`[GAP-I-*]` 항목

- [ ] **Step 1: 리포트 읽기**

`plan/test_review_0628.md`를 읽고 `[GAP-C-*]`와 `[GAP-I-*]` 항목 목록을 파악한다.

- [ ] **Step 2: 대상 테스트 파일 + 프로덕션 파일 읽기**

신규 테스트를 추가할 파일들과 해당 프로덕션 코드를 읽어 API를 확인한다.

- [ ] **Step 3: 신규 테스트 추가**

각 GAP 항목에 대해 신규 `[Fact]` 또는 `[Theory]` 테스트를 추가한다.

**신규 테스트 패턴 예시:**

경계값 갭:
```csharp
[Theory]
[InlineData(0)]
[InlineData(-1)]
[InlineData(int.MaxValue)]
public void WriteInt32_ReadInt32_Roundtrip_Boundary(int value)
{
    var writer = new SpanWriter(stackalloc byte[4]);
    writer.WriteInt32(value);
    var reader = new SpanReader(writer.Written);
    Assert.Equal(value, reader.ReadInt32());
}
```

예외 경로 갭:
```csharp
[Fact]
public void BinaryPacketSerializer_Buffer_TooShort_DoesNotThrow()
{
    var serializer = new BinaryPacketSerializer();
    var tooShort = new byte[2]; // 헤더(4B)보다 짧음
    // Deserialize는 빈/기본 패킷 반환 또는 예외 — 프로덕션 동작에 맞게
    Assert.Throws<ArgumentException>(() =>
        serializer.Deserialize<EchoPacket>(tooShort));
    // 또는: Assert.NotNull(serializer.Deserialize<EchoPacket>(tooShort));
    // 프로덕션 코드 동작에 맞게 수정
}
```

동시성 갭:
```csharp
[Fact]
public void DbMetrics_ConcurrentRecord_CountIsAccurate()
{
    var metrics = new DbMetrics();
    const int N = 1000;
    Parallel.For(0, N, _ => metrics.RecordMysqlSelect(100));
    var snap = metrics.GetSnapshot();
    Assert.Equal(N, snap.MysqlCount);
}
```

- [ ] **Step 4: 전체 테스트 실행**

```
dotnet test -v minimal 2>&1 | tail -10
```
Expected: 통과! 실패: 0 (신규 포함 전체)

- [ ] **Step 5: 커밋**

```
git add ServerLib.Tests/
git commit -m "테스트: ServerLib.Tests 커버리지 갭 신규 테스트 추가 (GAP-C/I 항목)"
```

---

### Task 7: 최종 검증 + CLAUDE.md 갱신

**Files:**
- Modify: `CLAUDE.md` (plan 테이블에 test_review_0628 행 추가)

**Interfaces:**
- Consumes: 수정 완료된 ServerLib.Tests

- [ ] **Step 1: 전체 솔루션 빌드 + 테스트**

```
dotnet build -c Release 2>&1 | tail -5
dotnet test -v minimal 2>&1 | tail -10
```
Expected: 빌드 0 오류, 테스트 전체 통과

- [ ] **Step 2: 수정 전/후 테스트 수 비교 출력**

```
# 수정 전: 176개 (Tasks 1-4 시작 전)
# 수정 후: dotnet test --list-tests 2>/dev/null | wc -l 로 확인
```

- [ ] **Step 3: CLAUDE.md plan 테이블 갱신**

`CLAUDE.md`의 "현재 플랜 문서 목록" 테이블에 다음 행 추가:
```
| `plan/test_review_0628.md` | 2026-06-28 | ServerLib.Tests 종합 코드 리뷰 (품질 감사·커버리지 갭 분석·Critical/Important 즉시 수정) |
```

- [ ] **Step 4: 최종 커밋**

```
git add CLAUDE.md
git commit -m "문서: CLAUDE.md 테스트 리뷰 플랜 등록 + 최종 검증 완료"
```
