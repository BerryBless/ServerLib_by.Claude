# 티켓팅 모니터링 설계 및 구현

**날짜:** 2026-06-22  
**상태:** 완료

---

## 1. 배경 및 목적

`TicketInventory`는 좌석 단위 상태(`SnapshotStates`)와 현재 잔여석(`FreeCount`)만 관찰 가능했다.
누적 이벤트 지표(총 예약·확정·결제실패·이탈반납·TTL만료·좌석경합)는 `[TTL]` 콘솔 로그로만 흘러가
보존되지 않았으며, 티켓팅 상태는 기존 `:9100` 모니터 API와 `[STATS]` 신호 어디에도 포함되지 않았다.

**목표:** 이미 존재하는 모니터링 스택(서버 `:9100` JSON → Python FastAPI 대시보드)에 티켓팅 관측면을
추가해, **실시간 좌석맵 + 누적 KPI**를 **콘솔과 웹 대시보드 양쪽**에서 보이게 한다.

---

## 2. 설계 결정

| 항목 | 채택 | 근거 |
|------|------|------|
| 파이프라인 변경 범위 | C# 서버 + dashboard.html 만 수정 | Python collector/state/protocol이 JSON 스키마 무관 pass-through임을 확인 |
| 카운터 위치 | `TicketInventory` 인라인 (`long` 필드) | `MobManager` 선례. 도메인이 자기 이벤트를 소유. |
| 카운터 방식 | `Interlocked.Increment` (lock-free) | Zero-allocation, CPU LOCK XADD 명령 기반 |
| 스냅샷 API | `MetricsSnapshot()` → `TicketMetrics` readonly record struct | Zero-allocation, `MobManager.Snapshot()` 패턴 재사용 |
| 좌석 배열 타입 | `int[]` (투영) | `System.Text.Json`이 `byte[]`를 Base64로 직렬화하는 함정 회피 |
| `[STATS]` 라인 | 수정 금지 | SoakTest 파싱 계약 불가침 |
| `_totalSeatTaken` 증가 위치 | CAS 실패 지점 한 곳만 | 범위 오류는 경합이 아니므로 별도 카운트하지 않음 |

---

## 3. 컴포넌트 구조

```
TicketInventory (도메인, lock-free 카운터)
        │ MetricsSnapshot() / SnapshotStates()
        ▼
Server/Program.cs ── 모니터 루프 ──► 콘솔 [TICKET] 라인
                  └─ CPU 샘플러 ──► JSON snapshot.ticket ──► :9100 StatsResponsePacket(Id=9)
                                                                    │
                                              Python collector(무수정) → state(무수정)
                                                                    ▼
                                       monitor/app/dashboard.html ── 좌석맵 그리드 + KPI 카드
```

---

## 4. 핵심 API

### 4.1 TicketMetrics (신규)

```csharp
public readonly record struct TicketMetrics(
    int Rows, int Cols, int Total,
    int Free, int Reserved, int Sold,
    long TotalReserved, long TotalConfirmed,
    long TotalPaymentFailed, long TotalAbandoned,
    long TotalExpired, long TotalSeatTaken);
```

### 4.2 MetricsSnapshot()

```csharp
// Zero-allocation, Thread-safe, Non-blocking
public TicketMetrics MetricsSnapshot()
```

### 4.3 누적 카운터 (6종)

| 카운터 | 증가 위치 | 의미 |
|--------|-----------|------|
| `_totalReserved` | `TryReserve` CAS 성공 | 예약 누적 |
| `_totalSeatTaken` | `TryReserve` CAS 실패만 | 좌석 경합 누적 |
| `_totalConfirmed` | `Confirm` 성공 | 결제 확정 누적 |
| `_totalPaymentFailed` | `Release` 성공 | 결제 실패 반납 누적 |
| `_totalAbandoned` | `ReleaseByContext` 실제 반납 | 이탈·OCE 반납 누적 |
| `_totalExpired` | `SweepExpired` 반납 루프 내 | TTL 만료 반납 누적 |

### 4.4 콘솔 출력

```
[TICKET] free=4 reserved=1 sold=1 reserved_total=6 confirmed=5 payfail=1 abandon=0 expired=0 seattaken=3
```

### 4.5 JSON ticket 섹션

```json
"ticket": {
  "rows": 2, "cols": 3, "total": 6,
  "free": 4, "reserved": 1, "sold": 1,
  "totalReserved": 6, "totalConfirmed": 5,
  "totalPaymentFailed": 1, "totalAbandoned": 0,
  "totalExpired": 0, "totalSeatTaken": 3,
  "seats": [2, 2, 0, 1, 0, 0]
}
```

---

## 5. 변경 파일 목록

| 파일 | 변경 내용 |
|------|-----------|
| `Ticketing/TicketInventory.cs` | 6종 누적 카운터 필드 추가, 각 상태 전이 chokepoint에 `Interlocked.Increment`, `MetricsSnapshot()` 메서드 추가, `TicketMetrics` readonly record struct 추가 |
| `Server/Program.cs` | 모니터 루프에 `[TICKET]` 콘솔 라인 추가, CPU 샘플러 JSON snapshot에 `ticket` 섹션 추가 (`int[]` seats 투영) |
| `monitor/app/dashboard.html` | CSS 좌석맵 스타일 추가, `renderCard()`에 좌석맵 그리드 + KPI 행 렌더링 |
| `ServerLib.Tests/TicketInventoryConcurrencyTests.cs` | 카운터·`MetricsSnapshot`·Base64 직렬화 회귀 검증 테스트 10개 추가 (총 160개) |
| `CLAUDE.md` | 플랜 목록 행 추가 |

**무수정 파일(확인됨):**
- `monitor/app/collector.py`, `state.py`, `protocol.py`, `main.py`
- `ServerLib/.../StatsResponsePacket.cs`, `StatsRequestPacket.cs`

---

## 6. 빌드 검증

```
dotnet build   # 경고 0, 오류 0
dotnet test    # 160/160 통과 (신규 10개: ㉙~㊳ 포함)
```

**미검증 항목:** E2E(서버 기동·브라우저 대시보드·Client 데모) 런타임 확인은 수행하지 않았다.
빌드 0오류·단위테스트 160/160이 자동화 검증의 전부다.

---

## 7. 주요 설계 제약 (advisor 검토 반영)

1. **Base64 함정:** `byte[]`를 `snapshot` 익명 객체에 넣으면 Base64 문자열로 직렬화됨 → `int[]` 투영 필수
2. **`_totalSeatTaken` 위치:** CAS 실패 지점(line 150-151)에만 배치. 범위 초과 거부(line 143-144, 181-182)는 경합이 아님
3. **비원자 스냅샷:** 현재 상태 스캔과 누적 카운터는 원자적이지 않음 — 파생 불변식 표시/단언 금지
4. **`[STATS]` 불가침:** SoakTest 파싱 계약. `[TICKET]`은 다음 줄에 별도 태그로 추가

---

## 8. 향후 확장 포인트

- `[TICKET]` 라인을 SoakTest 하네스에서 파싱하면 장시간 테스트 중 결제 성공률 추적 가능
- `ticketSection`에 `timestampUnixMs` 필드를 추가하면 시계열 KPI 집계 가능
- 대시보드에 예약 → 확정 전환율 바(bar) 시각화 추가 가능
