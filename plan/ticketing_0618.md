# 선착순 티켓팅 시스템 구현 계획

## 배경 및 목적

ProudNet류 고성능 서버 라이브러리(.NET 10) 학습 프로젝트에 선착순 티켓팅 시스템을 추가한다.
"3개의 티켓을 선착순으로 배분하고 로그인·결제까지 처리하되 로그인/결제는 더미로 간단히" 하는 데모를 목표로 한다.

이 기능의 핵심은 **다수 IO 스레드에서 동시에 들어오는 예약 요청에 대한 원자적 슬롯 배분**이며,
라이브러리의 lock-free 동시성 강점을 직접 보여주는 주제다.

## 설계 결정

| 항목 | 결정 | 근거 |
|------|------|------|
| 아키텍처 | 기존 ServerLib TCP 패킷 기반 | 기존 OnReceived 디스패치 확장, 신규 인프라 불필요 |
| 로그인 | 더미 (아이디만 수락) | MySQL/Redis/PBKDF2 외부 의존 제거, 외부 인프라 없이 실행 가능 |
| 선착순 규칙 | 예약 후 결제(reserve-then-pay) | 결제 실패 시 슬롯 반납→재예약 흐름 시연 가능 |
| 재고 저장 | 메모리, lock-free (Interlocked/CompareExchange) | Zero-allocation, 외부 의존 0 |
| 코드 위치 | 신규 `Ticketing` 공유 라이브러리 | 동시성 단위 테스트 분리, Auth 라이브러리 패턴 동일 |

## 컴포넌트 구조

```
ClaudeCodeStudy/
├── Ticketing/                     # 신규 공유 라이브러리
│   ├── Ticketing.csproj           # net10.0, ServerLib 참조
│   ├── TicketContext.cs           # 세션 컨텍스트 (SlotIndex 선형화 앵커)
│   ├── TicketInventory.cs         # lock-free 재고 관리 (핵심)
│   ├── IDummyPaymentGateway.cs    # 결제 게이트웨이 인터페이스
│   └── DummyPaymentGateway.cs    # Task.Delay 기반 더미 구현
├── ServerLib/Core/Serialization/Packets/
│   ├── TicketStatus.cs             # 신규: 상태 코드 enum (byte 기반)
│   ├── TicketReserveRequestPacket.cs  # 신규: Id=13, 0B 본문
│   ├── TicketPayRequestPacket.cs   # 신규: Id=14, 1B 본문 (SimulateFailure)
│   └── TicketResultPacket.cs       # 신규: Id=15, 3B 본문 (Status/Slot/Remaining)
├── AppConfig/
│   ├── ServerConfig.cs             # 수정: EnableTicketing + TicketConfig
│   └── ClientConfig.cs             # 수정: EnableTicketing + TicketingDemoConfig
├── Server/
│   ├── Server.csproj               # 수정: Ticketing ProjectReference 추가
│   ├── Program.cs                  # 수정: 더미로그인·예약·결제 분기, 이탈반납, TTL스위퍼
│   └── appsettings.json            # 수정: EnableTicketing, Ticket 섹션
├── Client/
│   ├── Program.cs                  # 수정: EnableTicketing → RunTicketingDemoAsync
│   └── appsettings.json            # 수정: EnableTicketing, Ticketing 섹션
└── ServerLib.Tests/
    ├── ServerLib.Tests.csproj      # 수정: Ticketing ProjectReference 추가
    ├── TicketPacketRoundTripTests.cs    # 신규: 패킷 라운드트립 14개
    └── TicketInventoryConcurrencyTests.cs  # 신규: 동시성 7개
```

의존 방향:
```
Server → Ticketing → ServerLib
Client → (Ticketing 불필요: 패킷 타입만 ServerLib 직접 사용)
ServerLib.Tests → Ticketing → ServerLib
```

## 핵심 API

### 티켓 흐름

```
클라 연결
  → LoginRequest(Id=10) → [더미 로그인] → LoginResponse(Success=true, Token="")
  → TicketReserveRequest(Id=13) → TicketResult(Reserved | SoldOut | AlreadyReserved)
  → TicketPayRequest(Id=14, SimulateFailure=bool)
       결제 성공 → TicketResult(Confirmed)
       결제 실패 → TicketResult(PaymentFailed) → 슬롯 반납 → 재예약 가능
  연결 끊김 → OnClientDisconnected → ReleaseByContext(자동 반납)
  TTL 초과 → SweepExpired → CAS 기반 안전 반납
```

### TicketInventory 선형화

```csharp
// 예약: CAS(Free→Reserved) 원자적 슬롯 점유
if (Interlocked.CompareExchange(ref _states[i], Reserved, Free) == Free) { ... }

// 확정/반납: Exchange가 단일 소비 선형화 지점
int slot = Interlocked.Exchange(ref ctx.SlotIndex, -1); // 한 번만 소비됨

// 스위퍼: CAS로 소유권 검증(ABA 안전)
if (Interlocked.CompareExchange(ref owner.SlotIndex, -1, i) == i) { ... }
```

### 직렬 세션 디스패치 가정

`SocketPipelineSession.ReadPipeAsync`가 `await DispatchPacketAsync`를 루프 내에서 await하므로
동일 세션에서 패킷이 직렬로 처리된다. 이 가정 덕분에 `TryReserve`의 중복 예약 가드
(`ctx.SlotIndex >= 0` 체크)가 check-then-act여도 안전하다.

## 핵심 API 사용 패턴

```csharp
// 서버 초기화
var inv = new TicketInventory(cfg.Ticket.TotalTickets, TimeSpan.FromSeconds(cfg.Ticket.ReservationTtlSeconds));
var gw  = new DummyPaymentGateway(cfg.Ticket.PaymentDelayMs, cfg.Ticket.PaymentFailureRate);

// 예약 핸들러 (non-blocking, no await)
var (status, slot) = inv.TryReserve(tctx);
await session.SendAsync(new TicketResultPacket { Status = status, Slot = ..., Remaining = ... });

// 결제 핸들러 (async, await 전에 데이터 복사!)
var pay = serializer.Deserialize<TicketPayRequestPacket>(data.Span);
bool sim = pay.SimulateFailure; // await 전 스택 복사 필수!
bool ok  = await gateway.ChargeAsync(tctx.Username, sim, ct);
var (s2, slot2) = ok ? inv.Confirm(tctx) : inv.Release(tctx);

// 이탈 핸들러
inv.ReleaseByContext(session.GetContext<TicketContext>());

// TTL 스위퍼 (1초 주기 Task.Run)
int released = inv.SweepExpired();
```

## 변경 파일 목록

| 파일 | 유형 | 내용 |
|------|------|------|
| `Ticketing/Ticketing.csproj` | 신규 | net10.0, ServerLib 참조 |
| `Ticketing/TicketContext.cs` | 신규 | 세션 컨텍스트, SlotIndex 선형화 앵커 |
| `Ticketing/TicketInventory.cs` | 신규 | lock-free 재고 핵심 (CAS/Exchange/Volatile) |
| `Ticketing/IDummyPaymentGateway.cs` | 신규 | 결제 인터페이스 (ValueTask 반환) |
| `Ticketing/DummyPaymentGateway.cs` | 신규 | Task.Delay 기반 더미 결제, Random.Shared |
| `ServerLib/.../TicketStatus.cs` | 신규 | byte 기반 와이어 상태 코드 enum |
| `ServerLib/.../TicketReserveRequestPacket.cs` | 신규 | Id=13, 0B struct |
| `ServerLib/.../TicketPayRequestPacket.cs` | 신규 | Id=14, 1B struct (SimulateFailure) |
| `ServerLib/.../TicketResultPacket.cs` | 신규 | Id=15, 3B struct (Status/Slot/Remaining) |
| `AppConfig/ServerConfig.cs` | 수정 | EnableTicketing + TicketConfig 추가 |
| `AppConfig/ClientConfig.cs` | 수정 | EnableTicketing + TicketingDemoConfig 추가 |
| `Server/Server.csproj` | 수정 | Ticketing ProjectReference |
| `Server/Program.cs` | 수정 | 더미로그인·예약·결제 분기, 이탈반납, TTL스위퍼 |
| `Server/appsettings.json` | 수정 | EnableTicketing, Ticket 섹션 |
| `Client/Program.cs` | 수정 | EnableTicketing 분기, RunTicketingDemoAsync |
| `Client/appsettings.json` | 수정 | EnableTicketing, Ticketing 섹션 |
| `ServerLib.Tests/ServerLib.Tests.csproj` | 수정 | Ticketing ProjectReference |
| `ServerLib.Tests/TicketPacketRoundTripTests.cs` | 신규 | 패킷 라운드트립 14개 |
| `ServerLib.Tests/TicketInventoryConcurrencyTests.cs` | 신규 | 동시성 7개 |

## 빌드 검증

```bash
# 빌드 (0 오류)
dotnet build

# 테스트 (114개 통과 — 기존 92개 + 신규 22개)
dotnet test ServerLib.Tests

# 티켓팅 데모 실행
# Server/appsettings.json: Features.EnableTicketing = true
dotnet run --project Server
# Client/appsettings.json: Features.EnableTicketing = true
dotnet run --project Client
```

## 향후 확장 포인트

1. **결제 실패율 시연**: `Ticket.PaymentFailureRate=0.3`으로 설정하면 30% 확률로 결제 실패 → 반납 루프
2. **TTL 만료 시연**: `ReservationTtlSeconds=5`로 짧게 설정 후 클라가 결제하지 않으면 TTL 반납 로그 확인
3. **실제 PG 연동**: `IDummyPaymentGateway`를 구현하는 실제 HTTP 클라이언트로 교체 가능
4. **티켓 수량 확장**: `TotalTickets` 조정으로 슬롯 수 변경 (TicketInventory 재생성 불필요)
5. **대기열(Waiting Room)**: `SoldOut` 응답 시 대기열에 등록 → 반납 시 대기열 선두에 알림
