# 좌석지정 예약 시스템 (Seat-Designated Reservation)

**날짜:** 2026-06-19  
**버전:** v1.3.0

---

## 1. 배경 및 목적

기존 티켓팅 시스템은 **익명 슬롯 + 선착순 자동배정** 방식이었다. `TicketInventory.TryReserve(ctx)`가 첫 번째 빈 슬롯을 스캔해 자동 배정하므로 클라이언트가 특정 좌석을 지정할 수 없었다.

이번 변경으로 공연장·영화관처럼 **클라이언트가 2D 좌석(예: A2)을 직접 지정해 예약**하는 흐름을 도입한다.

- **제거:** 선착순 자동배정 (`TryReserve(ctx)` 스캔 방식)
- **추가:** 좌석지정 예약, 좌석맵 조회 패킷

---

## 2. 설계 결정

### 2D 모델 vs 평면 ID

| 항목 | 채택 방식 | 비고 |
|------|----------|------|
| 와이어 표현 | Row(1B) + Col(1B) | A12 표시 → UX 직관적 |
| 내부 표현 | `seatId = row*Cols + col` | 기존 배열 구조 재사용 |
| 최대 좌석 수 | 255석 | TicketResultPacket.Slot이 1바이트 |

### 좌석맵 조회 방식

- **채택:** 조회 후 지정 (SeatMapRequest/Response 패킷 추가)
- **이유:** 클라이언트가 Free 좌석을 확인 후 지정 → SeatTaken 최소화. 스냅샷 방식이므로 경합 시 SeatTaken 수신 → 재조회 → 재선택 루프 필요.

### 기존 동시성 설계 재사용

- `_states[]` 배열 + `Interlocked.CompareExchange` — **변경 없음**
- `ctx.SlotIndex` 선형화 앵커 — **변경 없음**
- `Confirm`/`Release`/`ReleaseByContext`/`SweepExpired` — **변경 없음**

---

## 3. 컴포넌트 구조

```
ServerLib/Core/Serialization/Packets/
  SeatMapRequestPacket.cs   ← 신규 (Id=16, 0B body)
  SeatMapResponsePacket.cs  ← 신규 (Id=17, Rows+Cols+States)
  TicketReserveRequestPacket.cs ← 수정 (Row/Col 추가, 0B→2B)
  TicketStatus.cs           ← 수정 (SeatTaken=7 추가)

Ticketing/
  TicketInventory.cs        ← 수정 (2D 생성자, TryReserve(seatId), SnapshotStates)

AppConfig/
  ServerConfig.cs           ← 수정 (TotalTickets→Rows/Cols)

Server/Program.cs           ← 수정 (SeatMap 핸들러 추가, Reserve 핸들러 갱신)
Client/Program.cs           ← 수정 (좌석맵 조회→지정 예약 흐름)
Server/appsettings.json     ← 수정 (TotalTickets→Rows/Cols)

ServerLib.Tests/
  TicketInventoryConcurrencyTests.cs ← 갱신 (21개 테스트)
  TicketPacketRoundTripTests.cs      ← 갱신 (SeatMap·SeatTaken 추가)
```

---

## 4. 핵심 API

### 패킷 ID 할당

| 패킷 | Id | 방향 | Body |
|------|----|----|------|
| `SeatMapRequestPacket` | 16 | C→S | 0B |
| `SeatMapResponsePacket` | 17 | S→C | 2+N B (Rows·Cols·States[N]) |
| `TicketReserveRequestPacket` | 13 | C→S | 2B (Row·Col) |
| `TicketPayRequestPacket` | 14 | C→S | 1B (변경 없음) |
| `TicketResultPacket` | 15 | S→C | 3B (변경 없음) |

### TicketInventory 신규 API

```csharp
// 2D 생성자
public TicketInventory(int rows, int cols, TimeSpan reservationTtl)

// 좌석지정 예약 (선착순 스캔 제거)
public (TicketStatus status, int slot) TryReserve(TicketContext ctx, int seatId)
// → Reserved / AlreadyReserved / SeatTaken

// zero-alloc 좌석맵 스냅샷 (서버가 호출, stackalloc 버퍼 전달)
public void SnapshotStates(Span<byte> dest) // 값: 0=Free, 1=Reserved, 2=Sold
```

### 서버 흐름 (Server/Program.cs)

```
SeatMapRequest(16) 수신
  → Span<byte> states = stackalloc byte[total]
  → inventory.SnapshotStates(states)
  → SeatMapResponsePacket{Rows,Cols,States=states.ToArray()} 송신

TicketReserveRequest(13) 수신
  → seatId = Row * Cols + Col
  → inventory.TryReserve(ctx, seatId) → Reserved | SeatTaken | AlreadyReserved
  → TicketResultPacket 송신
```

### 클라이언트 흐름 (Client/Program.cs)

```
로그인 → SeatMapRequest 송신 → SeatMapResponse 수신
→ States[preferSeatId]==Free → TicketReserveRequest{Row,Col}
→ SeatTaken → 재조회 → 다른 Free 좌석 재선택 (최대 5회)
→ Reserved → TicketPayRequest{SimulateFailure}
→ PaymentFailed(failer) → SeatMapRequest 재조회 → 재예약 → 재결제
```

---

## 5. 변경 파일 목록

| 파일 | 변경 내용 |
|------|----------|
| `ServerLib/Core/Serialization/Packets/SeatMapRequestPacket.cs` | **신규** — Id=16, 0B body |
| `ServerLib/Core/Serialization/Packets/SeatMapResponsePacket.cs` | **신규** — Id=17, Rows·Cols·States 직렬화 |
| `ServerLib/Core/Serialization/Packets/TicketReserveRequestPacket.cs` | Row/Col 필드 추가, 0B→2B |
| `ServerLib/Core/Serialization/Packets/TicketStatus.cs` | `SeatTaken=7` 추가 |
| `Ticketing/TicketInventory.cs` | 2D 생성자, `TryReserve(ctx,seatId)`, `SnapshotStates` |
| `AppConfig/ServerConfig.cs` | `TicketConfig.TotalTickets` → `Rows`/`Cols` |
| `Server/appsettings.json` | `TotalTickets:3` → `Rows:2`, `Cols:3` |
| `Server/Program.cs` | SeatMap 핸들러 신규, Reserve 핸들러 Row/Col 처리 |
| `Client/Program.cs` | `RunTicketingDemoAsync` — 좌석맵 조회→지정 예약 흐름 |
| `ServerLib.Tests/TicketInventoryConcurrencyTests.cs` | 21개 테스트로 갱신 |
| `ServerLib.Tests/TicketPacketRoundTripTests.cs` | SeatMap·SeatTaken 테스트 추가 |

---

## 6. 빌드 검증

```bash
dotnet build        # 0 오류
dotnet test ServerLib.Tests  # 144/144 통과
```

### E2E 데모 검증 (EnableTicketing=true)

서버 실행 후 클라이언트 실행:
1. `[Ticket] 티켓팅 모듈 초기화 grid=2×3(총6석)` 로그 확인
2. 클라이언트별 `좌석맵 조회 완료 목표좌석=A1` → `예약 성공 좌석=A1(seatId=0)` 로그 확인
3. 경합 시 `SeatTaken → 재시도` 로그 확인
4. Failer `결제 실패(의도적) → 좌석맵 재조회 후 재예약 시도` → `재결제=Confirmed` 로그 확인
5. 최종 `Confirmed == min(7, 6)` 불변식 출력 확인

---

## 7. 향후 확장 포인트

| 항목 | 설명 |
|------|------|
| 좌석 카테고리 | States 값에 VIP(3)/Disabled(4) 등 추가 가능 |
| 부분 좌석맵 조회 | 대형 공연장(255석 초과)을 위해 Row 범위 지정 조회 추가 |
| 좌석 점유 알림 | 실시간 좌석맵 변경 푸시(구독 패턴) |
| 좌석 홀드 시간 표시 | Reserved 슬롯의 TTL 잔여 시간을 SeatMapResponse에 포함 |
