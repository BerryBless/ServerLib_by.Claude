# ServerLib.Tests 종합 코드 리뷰 설계

**날짜:** 2026-06-28  
**상태:** 승인됨

---

## 1. 배경 및 목적

ServerLib.Tests는 176개 테스트를 11개 파일에 걸쳐 보유한다. 누적 개발 과정에서 다음 문제가 예상된다:

- TDD 사이클 속도 우선으로 경계값·예외 경로 테스트가 생략된 케이스
- Assert가 취약하거나 항상 통과하는 테스트
- 새 기능(DbMetrics·티켓팅 배치·Rate Limit 등) 추가 후 대응 테스트가 불완전한 갭

**목표:** 기존 테스트 품질 감사 + 커버리지 갭 분석을 동시에 수행하고, 발견된 Critical/Important 항목을 즉시 수정(테스트 코드만)한다.

---

## 2. 범위

**대상:** `ServerLib.Tests/` 11개 파일, 176개 테스트

| 파일 | 테스트수 | 도메인 |
|------|---------|--------|
| SpanReaderWriterTests.cs | 17 | 직렬화 기반 |
| BinaryPacketSerializerTests.cs | 8 | 직렬화 |
| PacketRoundTripTests.cs | 11 | 직렬화 E2E |
| PacketPoolTests.cs | 9 | 메모리 풀링 |
| LoginServiceTests.cs | 13 | 인증 (PasswordHasher + LoginService) |
| DbMetricsTests.cs | 4 | DB 계측 |
| SessionRegistryTests.cs | 10 | 세션 관리 |
| SessionStateTests.cs | 9 | 세션 상태 |
| RpcDispatcherTests.cs | 9 | RPC 라우팅 |
| ServerMetricsTests.cs | 7 | 서버 메트릭 |
| TicketInventoryConcurrencyTests.cs | 46 | 티켓팅 동시성 |
| TicketPacketRoundTripTests.cs | 17 | 티켓 패킷 직렬화 |

**제외:** DbPerfTest.Tests (별도 세션에서 구현된 최신 코드)

---

## 3. 설계 결정

### 접근법 비교

| 접근 | 속도 | 정확성 | 채택 |
|------|------|--------|------|
| **A. 병렬 감사 → 통합 수정** | 빠름 | 높음 | ✅ 채택 |
| B. 파일별 순차 Review+Fix | 느림 | 높음 | ❌ |
| C. 도메인 그룹별 묶음 | 중간 | 중간 | ❌ |

**A 채택 이유:** 11개 파일을 동시에 감사해 전체 리포트를 빠르게 확보한 뒤, 우선순위 기반으로 단일 수정 사이클을 실행하는 것이 효율적이다.

---

## 4. 3단계 파이프라인

### Phase 1: 병렬 감사 (11 에이전트)

각 에이전트는 **테스트 파일 1개 + 대응 프로덕션 코드**를 읽고 구조화 감사 리포트를 반환한다.

**품질 감사 체크리스트:**
- Assert 없는 테스트 (항상 통과)
- 의미없는 Assertion (상수 비교, 항상-true)
- 테스트명이 의도를 서술하지 않음
- 경계값 누락 (0, -1, int.MaxValue, empty string, null)
- 테스트 간 실행 순서 의존성
- Arrange-Act-Assert 구조 미준수
- 중복 테스트 (동일 시나리오를 다른 이름으로 반복)

**커버리지 갭 체크리스트:**
- public 메서드/프로퍼티 중 테스트 없는 것
- throw 케이스·실패 분기 미테스트
- 동시성 코드의 race condition 시나리오 누락
- 경계 조건 (빈 컬렉션, 단일 원소, 최대값) 미테스트

**출력 형식 (각 에이전트):**
```
품질 이슈:
  - [Critical/Important/Minor] 파일명:테스트명 — 문제 설명 — 권장 수정
커버리지 갭:
  - [Critical/Important/Minor] 대상 클래스.메서드 — 누락 시나리오 — 제안 테스트명
```

### Phase 2: 통합 합산 (1 에이전트)

- 11개 리포트를 병합·중복 제거
- severity별 정렬 (Critical → Important → Minor)
- `plan/test_review_0628.md` 로 저장

### Phase 3: 일괄 수정 (1 에이전트)

- Critical/Important 품질 문제 → 기존 테스트 수정
- Critical/Important 커버리지 갭 → 신규 테스트 메서드 추가
- Minor → 리포트 기록만, 코드 수정 제외
- **프로덕션 코드 불변** (테스트 파일만 수정)
- 수정 후 `dotnet test` 전체 통과 확인

---

## 5. 수정 범위 원칙

| 원칙 | 내용 |
|------|------|
| 프로덕션 코드 불변 | ServerLib, Auth, Ticketing 등 프로덕션 코드 수정 금지 |
| 신규 테스트 독립성 | 각 신규 테스트는 다른 테스트와 독립적으로 실행 가능해야 함 |
| TDD 준수 | 신규 테스트는 프로덕션 코드가 이미 존재하므로 처음부터 통과해야 정상 |
| Minor 미수정 | 범위 통제를 위해 Minor는 리포트에만 기록 |

---

## 6. 결과물

1. **`plan/test_review_0628.md`** — 통합 감사 리포트 (품질 이슈 + 커버리지 갭, severity별)
2. **수정 커밋** — 테스트 파일 변경만 포함, `dotnet test` 전체 통과
3. **수정 전/후 테스트 수 비교** — 리포트 말미에 기록

---

## 7. 성공 기준

- `dotnet test` 수정 후 0 failures (회귀 없음)
- Critical 품질 이슈 전량 수정
- Critical 커버리지 갭 전량 신규 테스트로 추가
- Important 항목 80% 이상 수정

---

## 8. 향후 확장

- DbPerfTest.Tests 동일 감사 적용
- CI에 커버리지 임계값(Coverlet) 통합
- 동시성 테스트에 `Parallel.For` 기반 stress 테스트 추가
