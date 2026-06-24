# 배치 멀티 좌석 티켓팅 설계 문서

**날짜:** 2026-06-24  
**기반:** `plan/ticketing_seat_designation_0619.md` 좌석지정 예약 시스템

---

## 1. 배경 및 목적

기존 티켓팅 시스템은 클라이언트당 **좌석 1개**만 예약할 수 있었다 (`TicketContext.SlotIndex` 단일 `int`). 실 티켓 플랫폼처럼 "가족 4석 동시 구매"를 지원하려면 다음이 필요하다:

- **한 요청**에 N좌석을 묶어 All-or-nothing으로 예약
- 세션당 최대 좌석 상한(설정 기반)
- 보유 좌석 **전부**를 단일 결제로 확정/해제

**해결 문제:**
- `SlotIndex(int) = -1` → 단일 anchor의 구조적 한계
- 결제 성공 시 "일부만 Sold" 상태 불가능 → All-or-nothing 결제 필수
- 스위퍼 TTL 만료 시 배치 컨텍스트에서 특정 원소만 만료해야 함

---

## 2. 설계 결정

| 항목 | 채택 | 대안 | 기각 이유 |
|------|------|------|-----------|
| 예약 모델 | **배치 예약** (한 요청에 N좌석) | 단건 반복 요청 | 원자성 보장 어렵고 클라 UX 복잡 |
| 좌석 상한 | **고정 상한** (`MaxSeatsPerSession=4`) | 유동 상한(잔여석 기반) | 단순·예측 가능, 설정 변경으로 충분 |
| 결제 모델 | **일괄 결제** (보유 전체 확정) | 좌석별 개별 결제 | 부분 결제 상태 관리 복잡 |
| 예약 부분실패 | **All-or-nothing** (전부 롤백) | 성공분 유지 | 클라가 "어떤 좌석이 예약됐는지" 추적 부담 제거 |
| 결제 실패 | **All-or-nothing** (전체 해제) | 부분 해제 | 결제 실패 후 재예약 흐름 단순화 |

---

## 3. 핵심 동시성 설계

### 3.1 TicketContext 데이터 모델

```
Before: int SlotIndex = -1          (단일 linearization anchor)
After:  int[] Slots                  (원소별 독립 CAS anchor)
        Slots.Length == MaxSeatsPerSession
        -1 = 빈 슬롯, >=0 = 보유 seatId
```

**불변:** 배치는 단일 linearization point가 **아니다**. All-or-nothing은 per-seat CAS 위에 애플리케이션 레벨로 얹는 보장.

### 3.2 Reserve: All-or-nothing 롤백

```
TryReserveBatch(ctx, seatIds[N], reservedOut[N]):
  1. 보유 가드: any(Slots[k] >= 0) → AlreadyReserved
  2. cap 검증: Count > Slots.Length → SeatTaken
  3. per-seat CAS 루프 (빈 슬롯 순차 배정)
  4. 실패 시 롤백:
     for each successfully reserved seat:
       prev = Interlocked.Exchange(ref Slots[entry], -1)  ← entry claim 먼저
       if prev == seatId: _owners[seat]=null; _states[seat]=Free ← 스위퍼 ABA-safe
  5. 전체 성공 시 Interlocked.Add(ref _totalReserved, n)
```

**ABA-safe 핵심:** rollback이든 sweeper든 `Slots[entry]` 원소를 먼저 `Exchange`/-1로 claim한 후 `_states`를 건드린다. 두 소비자가 같은 seat를 동시에 Free로 만들지 않는다.

### 3.3 Confirm / Release

```
ConfirmAll(ctx, confirmedOut[]):
  for k in 0..Slots.Length-1:
    seat = Interlocked.Exchange(ref Slots[k], -1)
    if seat >= 0: _states[seat]=Sold; _totalConfirmed++

ReleaseAll(ctx, releasedOut[]):
  for k in 0..Slots.Length-1:
    seat = Interlocked.Exchange(ref Slots[k], -1)
    if seat >= 0: _states[seat]=Free; _totalPaymentFailed++
```

### 3.4 SweepExpired — 배치 컨텍스트

기존 단일 슬롯 scan → 배치 원소 탐색으로 확장:

```csharp
// owner.Slots[]에서 seatId i를 보유한 원소 entry를 탐색
int slotEntry = -1;
for (int k = 0; k < owner.Slots.Length; k++)
    if (Volatile.Read(ref owner.Slots[k]) == i) { slotEntry = k; break; }
if (slotEntry < 0) continue;
// CAS로 원소 claim — 실패 시 skip (pay나 다른 sweep이 먼저 처리)
if (Interlocked.CompareExchange(ref owner.Slots[slotEntry], -1, i) != i) continue;
Volatile.Write(ref _owners[i], null);
Interlocked.Exchange(ref _states[i], Free);
```

---

## 4. 패킷 와이어 포맷 변경

### TicketReserveRequestPacket (Id=13)

```
Before: [Row(1B)][Col(1B)]           bodySize=2
After:  [Count(1B)][Row₀,Col₀]...   bodySize=1+Count*2
```

`TicketReserveRequestPacket.Single(row, col)` 하위호환 factory 제공.

### TicketResultPacket (Id=15)

```
Before: [Status(1B)][Slot(1B)][Remaining(1B)]      bodySize=3
After:  [Status(1B)][Count(1B)][Slots...][Remaining(1B)]  bodySize=3+Count
```

성공: `Count=N`, `Slots=[seatId₀,...,seatIdₙ₋₁]`  
실패: `Count=0`, `Slots=[]`

---

## 5. 불변식 (최종 출력)

```
기대 상한: ConfirmedSeats ≤ ExpectedMax
where ExpectedMax = min(ClientCount, floor(TotalSeats / SeatsPerClient)) * SeatsPerClient
```

**주의:** 단순 `min(ClientCount*SeatsPerClient, TotalSeats)`는 SeatsPerClient가 TotalSeats를 나누어 떨어지지 않을 때 틀린다. 예: TotalSeats=6, SeatsPerClient=4 → ExpectedMax=4, 기존 식은 6 반환.

---

## 6. 설정 추가

### Server\appsettings.json
```json
"Ticket": {
  "MaxSeatsPerSession": 4  // ← 신규 (기본 4석/세션)
}
```

기동 시 `MaxSeatsPerSession < 1` 즉시 예외 — 0이면 모든 Reserve가 무음 SeatTaken.

### Client\appsettings.json
```json
"Ticketing": {
  "SeatsPerClient": 2  // ← 신규 (기본 2석/클라, MaxSeatsPerSession 이하 clamp)
}
```

---

## 7. 변경 파일 목록

| 파일 | 변경 내용 |
|------|-----------|
| `Ticketing/TicketContext.cs` | `SlotIndex` → `int[] Slots` (MaxSeatsPerSession 길이) |
| `Ticketing/TicketInventory.cs` | `TryReserveBatch`, `ConfirmAll`, `ReleaseAll`, `ReleaseAllByContext`, `SweepExpired` 배치화; 하위호환 래퍼 유지 |
| `ServerLib/Core/Serialization/Packets/TicketReserveRequestPacket.cs` | 배치 와이어 포맷 `[Count][Row,Col...]` |
| `ServerLib/Core/Serialization/Packets/TicketResultPacket.cs` | 배치 결과 `[Status][Count][Slots...][Remaining]` |
| `AppConfig/ServerConfig.cs` | `TicketConfig.MaxSeatsPerSession` |
| `AppConfig/ClientConfig.cs` | `TicketingDemoConfig.SeatsPerClient` |
| `Server/appsettings.json` | `MaxSeatsPerSession: 4` |
| `Client/appsettings.json` | `SeatsPerClient: 2` |
| `Server/Program.cs` | 핸들러 배치화, MaxSeatsPerSession 검증, 불변식 수정 |
| `Client/Program.cs` | K석 배치 예약/결제 데모, 올바른 상한 출력 |
| `ServerLib.Tests/TicketInventoryConcurrencyTests.cs` | `.SlotIndex`→`.Slots[0]` replace_all; 배치 테스트 9종 추가 |
| `ServerLib.Tests/TicketPacketRoundTripTests.cs` | 배치 포맷 라운드트립 테스트 |

---

## 8. 빌드 검증

```bash
dotnet build     # 0 오류
dotnet test ServerLib.Tests  # 172/172 통과
```

---

## 9. E2E 데모 실행

```bash
# 터미널 1: 서버 (appsettings.json에서 EnableTicketing=true로 변경 후)
dotnet run --project Server

# 터미널 2: 클라이언트 (appsettings.json에서 EnableTicketing=true로 변경 후)
dotnet run --project Client
```

확인 포인트:
- `[TICKET] 기대 상한:` 줄이 `ConfirmedSeats ≤ ExpectedMax`를 만족하는지
- `[TICKET] reserve=Reserved  seats=[A1,A2]` 형태로 배치 좌석이 출력되는지
- FailingUsername=user0의 결제 실패 → 재예약 → 재결제 흐름이 K석 배치로 동작하는지
- `:9100` JSON `ticket` 섹션 KPI가 배치 단위로 누적되는지

---

## 10. 향후 확장 포인트

- **부분 배치 전략** — 요청 좌석 중 가능한 만큼만 예약(SeatsTaken 구분 필요)
- **좌석 hold 시간 연장** — 결제 중 TTL 만료 방지(PG 콜백 전 연장)
- **배치 중복 방지** — 같은 배치 내 중복 seatId 클라이언트단 검증
- **다단 결제** — 대용량 배치를 분할 청구하는 PG 파이프라인
