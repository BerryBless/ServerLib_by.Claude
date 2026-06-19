# 종합 코드 리뷰 리포트 — 좌석지정 예약 시스템
**생성:** 2026-06-20  |  **대상:** commit 1bbc355 (티켓팅 좌석지정 예약 시스템, 2D 그리드·SeatMap·SeatTaken 재선택)  
**이전 리뷰:** plan/ticketing_review_0618.md (SEC-01·ARCH-01 등 15건 지적)

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low | Info |
|--------|------|----------|------|--------|-----|------|
| 🏗️ 아키텍처 | 62 / 100 | 0 | 2 | 3 | 2 | 1 |
| 🔒 보안 | 82 / 100 | 0 | 1 | 2 | 2 | 5 |
| ⚡ 성능 | 84 / 100 | 0 | 0 | 0 | 4 | 4 |
| 🎨 스타일 | 81 / 100 | — | 1 | 8 | 6 | — |
| **종합** | **77 / 100** | **0** | **4** | **13** | **14** | **10** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

---

## 이전 리뷰(0618) 해소 현황

| 항목 | 상태 | 근거 |
|------|------|------|
| SEC-01 결제 전 SlotIndex 검증 누락 | ✅ **해소** | Server/Program.cs:317-328 `Volatile.Read(ref tctx.SlotIndex) < 0` 가드 |
| SEC-02 Username 길이 제한 | ✅ **해소** | Server/Program.cs:258-263 MaxUsernameLength=32 |
| SEC-04 EnableLogin+EnableTicketing 상호배제 | ✅ **해소** | Server/Program.cs:46-49 InvalidOperationException throw |
| GAP-03 ReleaseByContext null 가드 | ✅ **해소** | TicketInventory.cs:238 `if (ctx is null) return;` |
| **ARCH-01 도메인 오염** | ❌ **미수정·재발** | TicketInventory.cs:2 using 여전히 존재 |

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [아키텍처] ARCH-01 (High) — 도메인 오염 미수정 재발
**위치:** `Ticketing/TicketInventory.cs:2`

이전 리뷰(ARCH-01)에서 "TicketStatus와 패킷 5종을 Ticketing 프로젝트로 이동"을 권고했으나 반영되지 않았다. `using ServerLib.Core.Serialization.Packets;` import가 그대로 유지되며, 생성자 주석(라인 101-104)이 `TicketResultPacket.Slot/Remaining`의 byte 폭을 도메인 불변식 근거로 인용하는 인프라 상세 의존이 지속된다. 도메인 계층(Ticketing/)이 인프라 계층(ServerLib.Core.Serialization.Packets/)에 역방향 의존하는 구조 위반이다.

**수정:** `TicketStatus` 열거형과 티켓팅 패킷 5종을 `Ticketing/` 프로젝트로 이동. `Ticketing.csproj`는 `IPacket/SpanWriter/SpanReader`만 의존하도록 변경. 생성자 주석은 "byte 범위 불변식"으로 자기완결적으로 재작성.

---

### [아키텍처] ARCH-02 (High) — Row/Col→seatId 변환 핸들러 누출 + Col 범위 미검증 → 좌석 별칭 버그
**위치:** `Server/Program.cs:298`

```csharp
int seatId = req.Row * ticketInventory.Cols + req.Col;  // Col 범위 검증 없음
```

2×3 그리드에서 클라이언트가 `Row=0, Col=3`을 전송하면 `seatId=3`이 계산되고 `TryReserve`의 범위 검증(`seatId < 6`)은 통과한다. 결과적으로 **좌석 (0,3) 요청 → 좌석 (1,0) 예약**이 이루어지는 좌석 별칭 버그가 발생한다. `TicketInventoryConcurrencyTests`의 `Make()` 헬퍼가 1×N 고정이라 이 버그를 탐지하지 못한다.

**수정:**
```csharp
// Server/Program.cs Reserve 핸들러에 추가
if (req.Row >= ticketInventory.Rows || req.Col >= ticketInventory.Cols)
{
    await session.SendAsync(new TicketResultPacket
        { Status = TicketStatus.SeatTaken, Slot = TicketResultPacket.NoSlot,
          Remaining = (byte)ticketInventory.FreeCount });
    return;
}
```
또는 `TicketInventory.TryReserveByRowCol(ctx, row, col)` 메서드로 검증+변환을 도메인 내부로 이동(권장).

---

### [보안] SEC-NEW-01 (High) — SimulateFailure 결제 실패 플래그가 와이어 프로토콜에 노출
**위치:** `Server/Program.cs:333`, `TicketPayRequestPacket.cs`  
**CWE:** CWE-807

`TicketPayRequestPacket(Id=14)`의 와이어 형식에 `SimulateFailure` 필드(1바이트 bool)가 포함된다. 모든 클라이언트는 이 값을 `true`로 설정하여 의도적으로 결제를 실패시킬 수 있고, 프로덕션 환경에서는 예약 슬롯을 점유한 채 TTL 만료를 반복하는 DoS 공격이 가능하다.

**수정:** `TicketPayRequestPacket`에서 `SimulateFailure` 필드 제거. 서버는 `ServerConfig.Ticket.PaymentFailureRate`를 기반으로 자체 결정. 클라이언트 데모의 `SimulateFailure=true` 플래그는 별도 서버 설정(또는 테스트 전용 패킷)으로 이동.

---

### [스타일] STYLE-03 (High) — 2D 그리드 시나리오 동시성 테스트 전면 누락
**위치:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs:14`

`Make()` 헬퍼가 `new TicketInventory(1, cols, ...)` 고정이라 21개 테스트 전부가 1×N 그리드에서만 동작한다. ARCH-02의 좌석 별칭 버그를 테스트로 탐지할 수 없으며, 2D 경계(row=Rows-1, col=Cols-1), SnapshotStates 평면화 순서, 크로스-행 동시 CAS 경합이 미검증이다.

**수정:** 최소 추가 케이스:
```csharp
[Fact]
public void TryReserve_2d_grid_seatId_mapping_correct()
{
    var inv = new TicketInventory(2, 3, TimeSpan.FromSeconds(30));
    var ctx = new TicketContext("u");
    var (status, slot) = inv.TryReserve(ctx, 5); // row=1,col=2 → seatId=5
    Assert.Equal(TicketStatus.Reserved, status);
    Assert.Equal(5, slot);
}

[Fact]
public void TryReserve_col_exceeds_grid_returns_seatTaken()
{
    var inv = new TicketInventory(2, 3, TimeSpan.FromSeconds(30));
    // seatId = 0*3+3 = 3 (유효 범위 내지만 Col=3 >= Cols=3이어야 거절됨)
    // TryReserveByRowCol 도입 후 테스트 추가
}
```

---

## Medium 발견사항 ← 권장 수정

### [아키텍처] ARCH-03 (Medium) — SeatMapResponsePacket.Deserialize 최대 길이 상한 미검증
**위치:** `SeatMapResponsePacket.cs:80`
`ReadBytes(Rows * Cols)` 전 `Rows*Cols > 255` 검증 부재. 악의적 패킷 시 65,025B 배열 할당 가능(SpanReader가 범위 오버리드는 차단하나 대형 할당은 허용). `if ((int)Rows * Cols > byte.MaxValue) throw new InvalidPacketException(...)` 추가 권장.

### [아키텍처] ARCH-04 (Medium) — IDummyPaymentGateway ISP 위반 (미수정)
**위치:** `Ticketing/IDummyPaymentGateway.cs:31`
이전 리뷰 ARCH-03 미수정. `simulateFailure` 파라미터가 인터페이스 계약에 노출. `IPaymentGateway.ChargeAsync(username, ct)`로 정화 권장.

### [아키텍처] ARCH-05 (Medium) — ISession.Context 단일 슬롯 덮어쓰기 암묵적 순서 의존
**위치:** `Server/Program.cs:146, 265`
`GameContext` → `TicketContext` 교체가 암묵적 순서에 의존하며 `OnClientDisconnected`에서 GameContext fallback이 잔존. `SessionBag` 복합 컨테이너 또는 조건부 초기화로 명시화 권장.

### [스타일] STYLE-01 (Medium) — SnapshotStates remarks에 Span<byte> 소유권·생명주기 미명시
**위치:** `Ticketing/TicketInventory.cs:163-183`
CLAUDE.md 주석 규칙(소유권·생명주기 명시) 미충족. `stackalloc` 전달 시 `await` 이전 `.ToArray()` 필수임을 remarks에 명문화 필요.

### [스타일] STYLE-05 (Medium) — SweepExpired + SnapshotStates 조합 테스트 없음
`SweepExpired` 후 SnapshotStates가 Free를 반영하는지 통합 검증 부재.

### [스타일] STYLE-06 (Medium) — 동일 컨텍스트 Release 후 재예약 테스트 없음
`Release` 후 `ctx.SlotIndex=-1` 복귀 → 동일 seatId 재예약 성공 여부 미검증.

### [스타일] STYLE-07 (Medium) — Make() 헬퍼명이 1×N 고정임을 표현하지 않음
`Make1xN(cols)` 또는 `MakeLinear(cols)`로 개명하여 의도 명시 권장.

### [스타일] STYLE-08 (Medium) — Client/Program.cs RunClient 로컬 함수 ~200줄
Failer 재예약 로직을 `TryFailerRetryAsync()` 별도 함수로 추출 권장.

### [스타일] STYLE-09 (Medium) — Server/Program.cs OnReceived 람다 ~200줄
`HandleSeatMapRequest` / `HandleTicketReserve` / `HandleTicketPay` 로컬 함수 분리 권장.

### [스타일] STYLE-11 (Medium) — SeatMapResponsePacket.States가 byte[]? nullable
null의 유효 의미가 없음. `byte[] States { get; set; } = Array.Empty<byte>();`로 변경 권장.

### [스타일] STYLE-12 (Medium) — Client.cs에서 Free 리터럴 0 주석 불일치
line 298에 `// 0 = Free` 주석 누락(line 307에는 있음). `const byte SeatFree = 0;`으로 통일 권장.

---

## Low / 정보성 ← 검토 권장

- [아키텍처] `Ticketing/TicketInventory.cs:94` — ARCH-06: 1D 생성자 제거(breaking change), NuGet 배포 전 v2.0.0 SemVer 검토
- [아키텍처] `TicketInventoryConcurrencyTests.cs:14` — ARCH-07: Make() 1×N 고정이 ARCH-02 버그 탐지 불가의 직접 원인
- [보안] `ServerConfig.cs:104-126` — SEC-NEW-04: MySQL 비밀번호·SeedPassword 하드코딩, 환경변수 주입 권장 (CWE-798)
- [보안] `SeatMapResponsePacket.cs:80` — SEC-NEW-05: MITM 시 최대 65KB 클라이언트 할당, TLS로 근본 차단 (CWE-400)
- [성능] `TicketInventory.cs:73-83` — PERF-01: FreeCount O(N) 스캔, N≤255 허용 범위, 빈도 증가 시 Interlocked 카운터 전환 검토
- [성능] `Server/Program.cs:285` — PERF-02: states.ToArray() 최대 255B Gen0 할당, 저빈도 허용
- [성능] `TicketInventory.cs:155` — PERF-05: Release 후 `_reservedAtTicks` 미초기화로 나노초급 창 존재. `Volatile.Write(ref _reservedAtTicks[slot], long.MaxValue)` 방어적 추가 검토
- [성능] `Client/Program.cs:342-381` — PERF-06: SeatTaken 재시도 백오프 부재, 수백 클라이언트 확장 시 지수 백오프 고려
- [스타일] `Server/Program.cs:279` — STYLE-02: stackalloc 주석의 async 안전 근거 보강 권장
- [스타일] `TicketInventoryConcurrencyTests.cs` — STYLE-04: SnapshotStates dest truncation 테스트 추가 권장
- [스타일] `TicketInventory.cs:157` — STYLE-10: Volatile.Write release-fence 내부 메커니즘 주석 보강
- [스타일] `TicketInventoryConcurrencyTests.cs:380-398` — STYLE-13: InlineData 케이스 의도 주석 누락
- [스타일] `Client/Program.cs:472` — STYLE-14: 미사용 dead variable `totalSeats` 제거
- [스타일] `Server/Program.cs:531` — STYLE-15: 빈 catch 블록 → `catch (InvalidOperationException) catch (Win32Exception)`으로 타입 특정

---

## 총평 및 판정

lock-free CAS 동시성 설계의 정확성은 이번 리뷰에서도 재확인됐으며, 이전 리뷰에서 지적된 SEC-01(결제 전 검증 누락) 등 4건이 깔끔하게 해소됐다는 점은 고무적이다. 그러나 ARCH-01(도메인 오염)이 미수정 상태로 재발 확인되었고, 신규 결함인 **좌석 별칭 버그(ARCH-02)** — Row=0, Col=Cols 전송 시 엉뚱한 좌석이 예약되는 문제 — 가 1×N 테스트 픽스처 때문에 은폐된 채 존재한다는 점이 가장 심각하다. 보안 측면에서는 `SimulateFailure` 필드가 와이어 프로토콜에 그대로 노출되어 있어 프로덕션 전환 전 반드시 제거해야 한다. 스타일 측면에서 XML 주석과 인라인 주석의 밀도는 양호하나, 2D 그리드 테스트 전면 부재라는 구조적 커버리지 공백이 이번 변경의 핵심 위험 요소다.

**판정: ⚠️ REQUEST CHANGES**

> High 4건(ARCH-01·ARCH-02·SEC-NEW-01·STYLE-03) 해소 후 재리뷰. 성능 도메인은 합격 수준(84점), 보안은 이전 리뷰 대비 24점 향상(58→82점).

---

## 권장 수정 우선순위

| 순위 | ID | 예상 공수 |
|------|-----|---------|
| 1 | **ARCH-02** 좌석 별칭 버그 수정 (`req.Col >= Cols` 범위 검증 추가 또는 TryReserveByRowCol 도입) | 30분 |
| 2 | **SEC-NEW-01** SimulateFailure와이어 제거 (서버 cfg 기반으로 이전) | 1시간 |
| 3 | **STYLE-03** 2D 그리드 테스트 추가 (Make2D 헬퍼 + 경계 케이스 3~5개) | 1시간 |
| 4 | **ARCH-01** 도메인 오염 해소 (TicketStatus + 패킷 5종 Ticketing 이동) | 2~3시간 |
| 5 | **ARCH-03** SeatMapResponsePacket.Deserialize Rows*Cols 상한 검증 | 15분 |

---

*도메인별 원본 데이터: `_workspace/02_{architecture,security,performance,style}_findings.json`*
