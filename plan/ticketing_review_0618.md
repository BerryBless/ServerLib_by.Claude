# 티켓팅 시스템 종합 코드 리뷰

**일시:** 2026-06-18  
**리뷰어:** 7개 독립 에이전트 병렬 감사 (architecture / security / performance / style / lock-free-enforcer / lock-justification-auditor / deadlock-analyzer)  
**대상 커밋:** `cac00be` 이후 신규 구현 (ticketing 시스템)

---

## 요약 점수표

| 차원 | 점수 | 최고 심각도 | 핵심 발견 |
|------|------|------------|---------|
| **Architecture** | 68/100 | High | 패킷 4종+TicketStatus가 ServerLib에 위치 (도메인 오염) |
| **Security** | 58/100 | **High** | 예약 없이 결제 먼저 실행 가능 → 실 PG 연동 시 과금 발생 |
| **Performance** | 82/100 | Medium | FreeCount 이중 호출, I/O 스레드 위 Console 전역 락 |
| **Style/Coverage** | 82/100 | High | SweepExpired 테스트 전무, 생성자 `<summary>` 3건 누락 |
| **Lock-Free** | ✅ 정확 | Medium | `charged=true && Confirm=NotReserved` 응용 계층 미처리 |
| **Lock Justification** | 100/100 | Medium | Barrier(64) CI flakiness, long 타입 Volatile/Interlocked 불일치 |
| **Deadlock** | ✅ 안전 | Low | 데드락 없음. 종료 시 응답 누락, 주석 오기 |

**TicketInventory lock-free 설계 자체는 정확하다.** 모든 High/Critical 발견은 응용 계층(`Server/Program.cs`)과 레이어 배치(`ServerLib` 도메인 오염)에 있다.

---

## 동시성 위험 가설 검증 결과

| 가설 | 판정 | 비고 |
|------|------|------|
| 1. "결제 성공했으나 슬롯 상실" (TTL 스위퍼/이탈과 경합) | **ISSUE** | `charged=true && Confirm=NotReserved` 미처리 → [SEC-01/LF-1] |
| 2. TryReserve 발행 순서 윈도우 (`_owners` null 창) | PASS | 스위퍼 owner null 가드가 커버 |
| 3. Confirm 후 `_owners` null 먼저 쓰기 | PASS | 스위퍼가 올바르게 건너뜀 |
| 4. AlreadyReserved 가드 TOCTOU | PASS | `SocketPipelineSession.ReadPipeAsync` 직렬 디스패치 코드로 확인 |
| 5. `_reservedAtTicks` Volatile.Write 충분성 | PASS | x64 환경에서 안전 (단, 32비트 torn read 가능 → [LOCK-03]) |

---

## 전체 발견사항

### 🔴 High

---

#### SEC-01 / LF-가설1 — 결제 전 예약 검증 누락 (실 PG 연동 시 Critical)

**파일:** `Server/Program.cs:273-310`  
**독립 확인:** security-reviewer(SEC-01) + lock-free-enforcer(가설1) 동시 발견  

더미 로그인만 했을 뿐 예약(`TicketReserveRequest`)을 생략하고 곧바로 `TicketPayRequest`를 보내면, `tctx is null` 가드를 통과한 뒤 **`paymentGateway!.ChargeAsync`가 먼저 실행**된다. 이후 `Confirm(tctx)`가 `SlotIndex=-1`을 확인하고 `NotReserved`를 반환하지만 결제는 이미 완료 상태다.

또한 TTL 만료로 인해 `charged=true`이면서 `Confirm=NotReserved`가 반환되는 정상 경합 시나리오에서, Server/Program.cs는 이 조합을 처리하지 않고 `NotReserved` 패킷을 그대로 클라이언트에 전송한다("결제했는데 티켓 없음").

**재현 조건:**
- 미예약 결제: 로그인 후 예약 없이 pay 패킷 직송
- 슬롯 상실: `PaymentDelayMs > ReservationTtlSeconds*1000` 설정 시 결제 완료 전 TTL 만료

```csharp
// Server/Program.cs:273 — 수정
else if (packetId == TicketPayRequestPacket.Id && ticketInventory is not null)
{
    var tctx = session.GetContext<TicketContext>();
    if (tctx is null) return;

    // [SEC-01 수정] 예약 없이 결제하거나 이중 결제하는 경로를 사전 차단
    // 직렬 디스패치 보장으로 check-then-act 안전
    if (Volatile.Read(ref tctx.SlotIndex) < 0)
    {
        await session.SendAsync(new TicketResultPacket
        {
            Status    = TicketStatus.NotReserved,
            Slot      = TicketResultPacket.NoSlot,
            Remaining = (byte)Math.Min(ticketInventory.FreeCount, byte.MaxValue)
        });
        return;
    }

    var pay = serializer.Deserialize<TicketPayRequestPacket>(data.Span);
    bool simulateFail = pay.SimulateFailure;
    bool charged;
    try
    {
        charged = await paymentGateway!.ChargeAsync(tctx.Username, simulateFail, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // [DL-T01 수정] 서버 종료 시: 슬롯 반납 후 응답 생략
        ticketInventory.Release(tctx);
        return;
    }

    TicketResultPacket result;
    if (charged)
    {
        var (status, slot) = ticketInventory.Confirm(tctx);
        if (status == TicketStatus.NotReserved)
        {
            // [LF-가설1 수정] 결제 성공 후 슬롯 상실 (TTL 만료 경합)
            // 실제 PG 환경에서는 RefundAsync를 여기서 트리거해야 한다
            Console.WriteLine($"[TICKET-WARN] {session.RemoteEndPoint}  user={tctx.Username}  결제 성공 후 슬롯 상실 (TTL 만료 경합)");
            result = new TicketResultPacket
            {
                Status    = TicketStatus.PaymentFailed,
                Slot      = TicketResultPacket.NoSlot,
                Remaining = (byte)Math.Min(ticketInventory.FreeCount, byte.MaxValue)
            };
        }
        else
        {
            result = new TicketResultPacket
            {
                Status    = status,
                Slot      = slot >= 0 ? (byte)slot : TicketResultPacket.NoSlot,
                Remaining = (byte)Math.Min(ticketInventory.FreeCount, byte.MaxValue)
            };
        }
        Console.WriteLine($"[TICKET] {session.RemoteEndPoint}  user={tctx.Username}  pay=OK  status={status}  slot={slot}  free={ticketInventory.FreeCount}");
    }
    else { /* 기존 Release 코드 유지 */ }
    await session.SendAsync(result);
}
```

---

#### SEC-04 — EnableLogin + EnableTicketing 동시 활성화 시 무음 비활성화

**파일:** `Server/Program.cs:246`

`EnableLogin=true + EnableTicketing=true`이면 실제 로그인 분기가 항상 우선하여 `TicketContext`가 세션에 부착되지 않는다. 이후 모든 티켓팅 패킷은 `tctx is null → return`으로 **조용히 무시**되며 오류/경고가 없다.

```csharp
// Program.cs 초기화 직후 추가
if (cfg.Features.EnableTicketing && cfg.Features.EnableLogin)
    throw new InvalidOperationException(
        "EnableTicketing과 EnableLogin은 동시에 활성화할 수 없습니다. " +
        "티켓팅 모드는 더미 로그인 전용입니다. appsettings.json을 확인하세요.");
```

---

#### GAP-01 — `SweepExpired` 테스트 전무

**파일:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs`

TTL 만료 반납 로직이 전혀 테스트되지 않았다. SEC-01의 "결제 성공 후 슬롯 상실" 시나리오 재현도 SweepExpired 테스트에서 다뤄야 한다.

```csharp
[Fact]
public void SweepExpired_releases_expired_reserved_slots()
{
    // TTL 1틱으로 즉시 만료
    var inv = new TicketInventory(2, TimeSpan.FromTicks(1));
    var ctx = new TicketContext("user");
    inv.TryReserve(ctx);
    Thread.Sleep(1); // TTL 초과

    int released = inv.SweepExpired();

    Assert.Equal(1, released);
    Assert.Equal(2, inv.FreeCount);   // 슬롯 반납됨
    Assert.Equal(-1, ctx.SlotIndex);  // 선형화 앵커 소비됨
}

[Fact]
public void SweepExpired_does_not_disturb_reReserved_context()
{
    var inv = new TicketInventory(2, TimeSpan.FromTicks(1));
    var ctx = new TicketContext("user");
    inv.TryReserve(ctx);      // 슬롯 0 점유
    Thread.Sleep(1);          // TTL 초과
    inv.Confirm(ctx);         // 슬롯 0 → Sold, SlotIndex = -1
    inv.TryReserve(ctx);      // 슬롯 1 재예약

    int released = inv.SweepExpired(); // 슬롯 0은 Sold → 건너뜀

    Assert.Equal(0, released);
    Assert.True(ctx.SlotIndex >= 0); // 슬롯 1 보유 유지
}
```

---

#### ARCH-01 — `TicketStatus`·패킷 4종이 ServerLib에 위치 (도메인 오염)

**파일:** `ServerLib/Core/Serialization/Packets/Ticket*.cs`, `TicketStatus.cs`

ServerLib는 네트워크 직렬화 빌딩블록을 제공하는 라이브러리인데, 게임 비즈니스 도메인의 티켓팅 상태 코드와 패킷이 포함되어 있다. ServerLib를 Ticketing 없이 사용하는 소비자도 이 타입들을 끌고 다닌다.

**권장 이동:** `TicketStatus`와 패킷 4종을 `Ticketing` 프로젝트로 이동하고, `Ticketing.csproj`에서 `ServerLib`의 `IPacket`·`SpanWriter`·`SpanReader`만 의존하도록 한다. Server 프로젝트는 `Ticketing` 어셈블리를 참조하므로 사용에 문제없다.

---

### 🟠 Medium

---

#### GAP-03 — `ReleaseByContext(null ctx)` 방어 코드 없음

**파일:** `Ticketing/TicketInventory.cs:174`

더미 로그인 전에 이탈한 세션이 `OnClientDisconnected`에서 `ReleaseByContext(null)`을 호출할 수 있다. 현재 `NullReferenceException` 발생.

```csharp
public void ReleaseByContext(TicketContext? ctx)
{
    if (ctx is null) return; // 로그인 전 이탈 — no-op
    int slot = Interlocked.Exchange(ref ctx.SlotIndex, -1);
    if (slot < 0) return;
    Volatile.Write(ref _owners[slot], null);
    Interlocked.Exchange(ref _states[slot], Free);
}
```

테스트:
```csharp
[Fact]
public void ReleaseByContext_null_context_is_noop()
{
    var inv = Make(3);
    var exception = Record.Exception(() => inv.ReleaseByContext(null));
    Assert.Null(exception);
}
```

---

#### SEC-02 — Username 길이 미검증 → 64KB 힙 할당 공격

**파일:** `Server/Program.cs:250`, `ServerLib/Core/Memory/SpanReader.cs` (ReadString ushort 상한 없음)

`ReadString`이 `ushort`(최대 65535) 길이를 제한 없이 수락하므로 악의적 클라이언트가 64KB Username으로 세션당 힙 할당을 강제할 수 있다. `MaxConnections=10000` 설정 시 최대 ~640MB 강제 점유 가능.

```csharp
// 더미 로그인 핸들러 (Program.cs:250 이후)
const int MaxUsernameLength = 32;
if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > MaxUsernameLength)
{
    await session.SendAsync(new LoginResponsePacket { Success = false, Token = string.Empty });
    return;
}
```

---

#### LOCK-05 — `Barrier(64)` 테스트 패턴 → CI 2코어 flakiness

**파일:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs:22-30`

`Barrier.SignalAndWait()`는 동기 블로킹이라 64개 `Task.Run`이 ThreadPool 스레드를 동시 점유한다. CI 2코어 환경에서 ThreadPool 스레드 부족으로 30초+ 지연 가능.

```csharp
[Fact]
public async Task Concurrent_reserve_exactly_totalTickets_succeed()
{
    var inv = Make(3);
    const int concurrency = 64;
    // CI 환경에서 충분한 스레드 사전 확보
    ThreadPool.SetMinThreads(concurrency, concurrency);
    // ... 기존 코드
}
```

---

#### PERF-01 — 요청당 FreeCount O(n) 이중 호출

**파일:** `Server/Program.cs:268+271, 294+296, 305+307`

예약/결제 응답 생성과 `Console.WriteLine`에서 `ticketInventory.FreeCount`를 각각 따로 호출한다. O(n) 스캔 2회 발생.

```csharp
// 단순 수정: 지역변수로 캐시
int free = ticketInventory.FreeCount; // 1회만
var pkt = new TicketResultPacket { ..., Remaining = (byte)Math.Min(free, byte.MaxValue) };
await session.SendAsync(pkt);
Console.WriteLine($"... free={free}");
```

슬롯 수가 1000+ 이상으로 확장될 경우 `TicketInventory` 내부에 `int _freeCount` Interlocked 카운터 추가를 권장한다.

---

#### ARCH-03 — `IDummyPaymentGateway` 인터페이스에 구현 세부(`simulateFailure`) 노출

**파일:** `Ticketing/IDummyPaymentGateway.cs`

`simulateFailure` 파라미터는 더미 전용 개념이다. 실제 PG 구현은 이 파라미터를 무시해야 하므로 인터페이스가 구현 세부를 계약으로 강제한다. 이름도 `IDummyPaymentGateway`로 구현체를 특정한다.

```csharp
// 권장: 인터페이스 정화
public interface IPaymentGateway
{
    ValueTask<bool> ChargeAsync(string username, CancellationToken ct = default);
}

// DummyPaymentGateway에서 simulateFailure는 생성자 주입 또는 인스턴스 프로퍼티로
public sealed class DummyPaymentGateway : IPaymentGateway
{
    public bool SimulateFailure { get; set; }
    public ValueTask<bool> ChargeAsync(string username, CancellationToken ct = default) { ... }
}
```

---

#### SEC-03 — TTL 기반 슬롯 고갈 DoS

**파일:** `Server/appsettings.json` (`ReservationTtlSeconds: 30`)

공격자가 3개 연결로 로그인→예약만 하고 결제 없이 유지하면 30초간 전체 슬롯 점유. `MaxConnectionsPerIp=50`으로 일부 완화되어 있으나 다수 IP 공격에는 무방비. TTL=30초는 자동화 재예약 루프에 충분한 시간을 제공한다.

**권장:** `appsettings.json`에서 `ReservationTtlSeconds`를 실제 결제 딜레이(`PaymentDelayMs`)의 10배 이하로 설정. 서버 시작 시 경고 로그 추가.

---

#### ARCH-04 — `ISession.Context` 단일 `object?` 슬롯 덮어쓰기 패턴

**파일:** `Server/Program.cs:252, 140`

`OnClientConnected`에서 `GameContext` 설정 후 더미 로그인에서 `TicketContext`로 덮어쓰는 패턴이 컨텍스트 타입 교체 순서에 암묵적으로 의존한다. `TicketContext`가 `GameContext`를 합성하거나, `ISession.Context`를 복합 컨테이너로 교체하는 것이 장기 해법이다.

---

#### LF-02 — SweepExpired CAS 성공 후 방어적 어서션 부재

**파일:** `Ticketing/TicketInventory.cs:215-220`

현재 코드 정확성에는 문제없으나, 미래 코드 추가 시 숨은 버그를 조기에 탐지하기 위한 디버그 어서션이 권장된다.

```csharp
// 단계 5: CAS 성공 후
Debug.Assert(Volatile.Read(ref _states[i]) == Reserved,
    $"SweepExpired: 슬롯 {i}가 CAS 성공 후 Reserved 상태가 아님. 예상치 못한 상태 전이.");
Volatile.Write(ref _owners[i], null);
Interlocked.Exchange(ref _states[i], Free);
```

---

#### STYLE-01~03 — 생성자 `<summary>` 태그 누락

**파일:** `TicketContext.cs:38`, `DummyPaymentGateway.cs:28`, `TicketInventory.cs:78`

CLAUDE.md 규칙 위반. 세 생성자 모두 `<param>` 태그만 있고 `<summary>`가 없다.

```csharp
/// <summary>새 티켓팅 컨텍스트를 초기화합니다.</summary>
/// <param name="username">더미 로그인 시 입력된 사용자 이름입니다.</param>
public TicketContext(string username) => Username = username;
```

---

#### GAP-02 — TTL 스위퍼 ABA 경로 테스트 없음

**파일:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs`

`SweepExpired`의 CAS 기반 ABA 보호(`CompareExchange != i → no-op`)가 검증되지 않았다.

```csharp
[Fact]
public void SweepExpired_does_not_release_slot_owned_by_different_context()
{
    var inv = new TicketInventory(2, TimeSpan.FromTicks(1));
    var ctx = new TicketContext("user");
    inv.TryReserve(ctx);     // 슬롯 0
    Thread.Sleep(1);         // TTL 초과

    // 스위퍼 전에 ctx가 슬롯 0을 확정하고 슬롯 1 재예약
    inv.Confirm(ctx);        // 슬롯 0 → Sold, SlotIndex=-1
    inv.TryReserve(ctx);     // 슬롯 1 재예약

    // 슬롯 0은 이미 Sold → SweepExpired가 Reserved 조건 실패로 건너뜀
    int released = inv.SweepExpired();

    Assert.Equal(0, released);
    Assert.True(ctx.SlotIndex >= 0); // 슬롯 1 유지
}
```

---

#### GAP-04 — DummyPaymentGateway 극단값 미검증

**파일:** 테스트 없음

```csharp
[Theory]
[InlineData(0.0, true)]   // FailureRate=0 → 항상 성공
[InlineData(1.0, false)]  // FailureRate=1 → 항상 실패
public async Task DummyPaymentGateway_extreme_failureRates(double rate, bool expected)
{
    var gw = new DummyPaymentGateway(delayMs: 0, failureRate: rate);
    bool result = await gw.ChargeAsync("user", simulateFailure: false);
    Assert.Equal(expected, result);
}
```

---

### 🟡 Low

---

#### LOCK-03 — `_reservedAtTicks` Volatile vs Interlocked 불일치

**파일:** `Ticketing/TicketInventory.cs:205`

`Volatile.Read(ref _reservedAtTicks[i])`를 사용하는데, Transport 계층(`SocketPipelineSession.cs:40,43`)은 cross-thread `long`에 `Interlocked.Read`를 사용한다. x86 32비트에서 `long` torn read 가능. x64에서는 안전하지만 일관성 부재.

```csharp
// 변경 전
long reservedAt = Volatile.Read(ref _reservedAtTicks[i]);
// 변경 후 (Transport 계층과 일관성 확보)
long reservedAt = Interlocked.Read(ref _reservedAtTicks[i]);
```

---

#### DL-T01 — 서버 종료 시 결제 핸들러 OCE → 응답 누락

**파일:** `Server/Program.cs:284`

서버 종료 중 `ChargeAsync`가 `OperationCanceledException`을 던질 때 응답 없이 연결이 끊긴다. [SEC-01 수정 코드]에 이미 포함된 `try/catch (OperationCanceledException)` 블록으로 해결.

---

#### DL-T03 — Client inbox 주석 오기: OCE ≠ ChannelClosedException

**파일:** `Client/Program.cs:270`

```csharp
// 변경 전: "대기 중인 ReadAsync가 OperationCanceledException 발생"
// 변경 후:
// TryComplete → 대기 중인 ReadAsync가 ChannelClosedException(InvalidOperationException 파생)을 throw한다.
// OCE가 아님 — catch(Exception)에서 처리됨.
inbox.Writer.TryComplete();
```

---

#### ARCH-07 — `Remaining` byte 타입 → TotalTickets ≤ 255 제약 미문서화

**파일:** `Ticketing/TicketInventory.cs:80`

```csharp
public TicketInventory(int totalTickets, TimeSpan reservationTtl)
{
    if (totalTickets <= 0 || totalTickets > byte.MaxValue)
        throw new ArgumentOutOfRangeException(nameof(totalTickets),
            $"totalTickets must be 1–{byte.MaxValue} (TicketResultPacket.Remaining 필드가 1바이트).");
    // ...
}
```

---

#### STYLE-04 — `_totalTickets`·`_ttlTicks` 사이 주석 배치 혼란

**파일:** `Ticketing/TicketInventory.cs:56-58`

`_totalTickets` 선언 아래에 `Stopwatch.Frequency` 주석이 붙어 있어 `_ttlTicks`의 주석인지 `_totalTickets`의 주석인지 불명확하다.

```csharp
private readonly int  _totalTickets; // 슬롯 배열 경계값 — 루프 상한으로만 사용
// long: Stopwatch.Frequency 기반 GetTimestamp 차이로 TTL 판단.
// Stopwatch 틱은 커널 전환 없는 단조 시계 — 시스템 시각 변경에 영향받지 않음.
private readonly long _ttlTicks;
```

---

#### STYLE-05 — `Release` vs `ReleaseByContext` 반환 타입 불일치 미설명

**파일:** `Ticketing/TicketInventory.cs:158, 174`

`Release`는 `(TicketStatus, int)` 반환, `ReleaseByContext`는 `void` 반환. 의도적 설계이나 `ReleaseByContext` 주석에 이유가 없다.

```xml
/// <remarks>
/// <b>반환값 없는 이유:</b> 이탈·TTL 경로에서는 결과를 클라이언트에 전달할 세션이 없으므로
/// 반환값이 불필요합니다. 결제 실패 응답에는 <see cref="Release"/>를 사용하세요.
/// </remarks>
```

---

#### GAP-05 — `Release` 미예약 경로 테스트 없음

```csharp
[Fact]
public void Release_without_reservation_returns_notReserved()
{
    var inv = Make(3);
    var ctx = new TicketContext("user");
    var (status, slot) = inv.Release(ctx);
    Assert.Equal(TicketStatus.NotReserved, status);
    Assert.Equal(-1, slot);
}
```

---

#### GAP-06 — 생성자 `totalTickets <= 0` 예외 테스트 없음

```csharp
[Theory]
[InlineData(0)]
[InlineData(-1)]
public void Constructor_invalid_totalTickets_throws(int invalid)
{
    Assert.Throws<ArgumentOutOfRangeException>(
        () => new TicketInventory(invalid, TimeSpan.FromSeconds(30)));
}
```

---

## 권장 조치 우선순위

### 즉시 수정 (빌드·동작 정확성)

| 우선순위 | ID | 파일 | 작업 |
|---------|-----|------|-----|
| 1 | SEC-01/LF-가설1 | Server/Program.cs | 결제 전 SlotIndex 검증 + charged+NotReserved 처리 + OCE 핸들링 |
| 2 | SEC-04 | Server/Program.cs | 시작 시 EnableLogin+Ticketing 상호 배타 검증 |
| 3 | GAP-03 | TicketInventory.cs | `ReleaseByContext(null)` null 가드 추가 |
| 4 | SEC-02 | Server/Program.cs | Username 길이 검증 (MaxUsernameLength=32) |
| 5 | PERF-01 | Server/Program.cs | FreeCount 지역변수 캐시 (단순 1줄 수정) |

### 단기 개선 (코드 품질·신뢰성)

| 우선순위 | ID | 파일 | 작업 |
|---------|-----|------|-----|
| 6 | GAP-01 | TicketInventoryConcurrencyTests.cs | SweepExpired 기본 테스트 2개 추가 |
| 7 | GAP-02 | TicketInventoryConcurrencyTests.cs | ABA 경로 테스트 추가 |
| 8 | LOCK-05 | TicketInventoryConcurrencyTests.cs | Barrier(64) 전 `SetMinThreads` 추가 |
| 9 | LOCK-03 | TicketInventory.cs | `Volatile.Read` → `Interlocked.Read` (long) |
| 10 | STYLE-01~03 | Ticketing/*.cs | 생성자 `<summary>` 3건 추가 |
| 11 | DL-T03 | Client/Program.cs | OCE/ChannelClosedException 주석 수정 |

### 중장기 개선 (아키텍처 정제)

| 우선순위 | ID | 작업 |
|---------|-----|-----|
| 12 | ARCH-01 | TicketStatus + 패킷 4종을 Ticketing 프로젝트로 이동 |
| 13 | ARCH-03 | `IDummyPaymentGateway` → `IPaymentGateway` (simulateFailure 제거) |
| 14 | ARCH-07 | TicketInventory 생성자에 `totalTickets <= 255` 상한 검증 추가 |
| 15 | ARCH-04 | ISession.Context 복합 컨테이너 설계 (중장기) |

---

## 검증 방법

모든 즉시 수정 적용 후:

```bash
# 빌드 0오류 확인
dotnet build

# 전체 테스트 통과 (기존 114 + 신규 ~8개 = ~122개)
dotnet test ServerLib.Tests

# 통합 데모 (SEC-04 검증 포함)
# appsettings.json: EnableTicketing=true, EnableLogin=false
dotnet run --project Server
dotnet run --project Client
# 기대: Confirmed=3, 결제 실패 클라의 재예약→재결제 정상 완료
```
