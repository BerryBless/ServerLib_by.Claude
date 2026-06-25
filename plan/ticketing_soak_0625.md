# 티켓팅 소크 하네스 (SoakTest 확장)

**날짜:** 2026-06-25  
**검증:** `dotnet build` 0오류·0경고, 전 테스트 시나리오 PASS (6종 — Spread·Hotspot·Grind·Expire-focus·10분 장시간·Damage 회귀)

### 전체 테스트 결과 (2026-06-25)

| 테스트 | 결과 | 주요 KPI |
|--------|------|---------|
| Smoke/Spread (30s) | **PASS** | sent=921=serverRecv, sold=36, errs=0 |
| Hotspot (40s) | **PASS** | sent=1,386=serverRecv, errs=0 |
| Grind (40s) | **PASS** | sent=1,298=serverRecv, sold=34, errs=0 |
| Expire-focus (expire-rate=0.5, 40s) | **PASS** | expired=16, reserved=0(settle후) |
| Long-duration (10분, 6,619 cycles) | **PASS** | heap 2~25MB 진동(단조증가 없음), errs=0 |
| Damage 회귀 | **PASS** | sent=17,950=serverRecv, errs=0 |

### 파이프 버퍼링 루트 코즈 (진단 기록)

초기 테스트에서 `ClientErrorRate [Hard] FAIL` (errs≈300/550 사이클) 반복 발생. 근본 원인:

1. **`run_soak.ps1` `ReadToEnd()` 파이프 블로킹**: PowerShell이 자식 프로세스 stdout을 `ReadToEnd()`로 수집 → 프로세스 종료 전까지 읽지 않음 → stdout 파이프 버퍼(64KB) 포화
2. **서버 `Console.WriteLine` 블로킹**: SoakTest의 파이프 버퍼가 꽉 차면 `ServerProcess.ReadStdoutAsync`가 SoakTest stdout에 출력하지 못하고 블로킹 → 서버 stdout 파이프도 꽉 참 → 서버 `Console.WriteLine("[+] ...")` 블로킹
3. **accept 루프 블로킹**: `SocketPipelineListener.AcceptLoopAsync`가 `await OnClientConnected(session)`를 기다리는 중, `OnClientConnected`가 `Console.WriteLine("[+]")` 후 `await session.SendAsync(MobHpPacket)` — `Console.WriteLine`이 블로킹되면 accept 루프도 블로킹
4. **TCP backlog 포화 → ECONNREFUSED**: accept 루프가 블로킹된 사이 쌓이는 SYN 요청이 backlog(512) 초과 → 커널이 RST 전송 → 클라이언트 `WSAECONNREFUSED`

**수정**: `run_soak.ps1`을 `BeginOutputReadLine()` + `ConcurrentBag<string>` 이벤트 방식으로 변경 → 파이프 실시간 소비 → 블로킹 연쇄 차단 → 전 테스트 PASS.

---

## 배경 및 목적

티켓팅 서버는 `reserve → pay → TTL` churn 워크로드를 가지지만 장시간 구동하며
**메모리·세션·슬롯 누수**를 검증하는 전용 하네스가 없었다.
`SoakTest`는 `DamagePacket`만 송신하고 티켓팅 워크로드와 KPI 파싱을 지원하지 않았다.

**목표:** `SoakTest`를 확장해 티켓팅 churn을 구동하는 설정 가능한 장시간 누수/안정성 하네스를 만든다.
child-process 서버 + stdout 파싱 + Hard/Soft 판정 + graceful-FIN 인프라를 그대로 재사용한다.

---

## 설계 결정

| 결정 항목 | 채택 | 검토한 대안 | 근거 |
|-----------|------|-------------|------|
| 워크로드 방식 | **Fire-and-pace** (응답 파싱 없음) | Demo 스타일 Channel inbox + 재시도 | 장시간 무인 churn에서 demo 하네스 자체가 행/누수 발생원. 누수 진실은 서버측 KPI |
| KPI 판별 토큰 | `reserved_total=` | `free=` | 이벤트 라인(Program.cs:384/477/499)도 `free=` 포함 → `reserved_total=`로만 KPI 라인 식별 |
| 구현 방식 | SoakTest 확장 | 신규 프로젝트 | child-process 서버·STATS 파싱·판정 인프라 재사용, Damage 회귀 보존 |
| 구조 | IWorkload 전략 패턴 | SoakClient 내부 분기 | SoakClient = 연결 수명 전담; IWorkload = "무엇을 보낼 것인가" 전담. 확장 가능 |
| 반납 경로 | **Graceful + TTL 만료 둘 다** | graceful만 | SweepExpired 코드 경로 커버리지 확보 |
| NoTicketData 체크 | [Hard] FAIL | 무시·경고만 | ticketSnap null이면 SlotLeak/KpiConservation/SeatConservation 전부 false → vacuous PASS 방지 |

---

## 컴포넌트 구조

```
SoakTest/
├── Program.cs                 (수정) 워크로드 선택, 티켓팅 config, 권위 read 전 'q'
├── SoakClient.cs              (수정) IWorkload 위임, cycleIndex 로컬 추가
├── SoakStats.cs               (수정) reserveSent/paySent/abandonCycles/expireCycles +4
├── SoakOptions.cs             (수정) WorkloadType, ContentionPattern, 8개 신규 CLI 옵션
├── SoakReport.cs              (수정) 티켓팅 Hard 체크(NoTicketData/SlotLeak/KpiConservation/SeatConservation)
├── ServerProcess.cs           (수정) TicketSnapshot·TicketingStartConfig, [TICKET] KPI 파싱, TryStart 오버라이드
├── SeatPicker.cs              (신규) ContentionPattern enum + Pick() 순수 함수
└── Workloads/
    ├── IWorkload.cs           (신규) 워크로드 전략 인터페이스
    ├── DamageWorkload.cs      (신규) 기존 DamagePacket 로직 추출 (동작 무변경)
    └── TicketingWorkload.cs   (신규) fire-and-pace reserve→pay/abandon/ttl-expire
```

### 의존 관계

```
Program.cs
  └── SoakClient  ←→  IWorkload
                       ├── DamageWorkload
                       └── TicketingWorkload
                             └── SeatPicker (순수 함수)
      SoakStats ← (카운터 공유)
      ServerProcess → TicketSnapshot / TicketingStartConfig
      SoakReport ← Evaluate(stats, snap, ticket, totalSeats)
```

---

## 핵심 API

### 워크로드 전략 추상화

```csharp
public interface IWorkload
{
    // fire-and-pace: 응답 파싱 없음. 누수 판정은 서버측 [TICKET] KPI로 수행.
    Task RunCycleAsync(IClientConnection conn, int cycleIndex, CancellationToken ct);
}
```

### 티켓팅 사이클 처분

```
TicketingWorkload.RunCycleAsync:
  1. LoginRequestPacket(Id=10) 송신 → loginSettleMs 대기 (기본 10ms)
  2. SeatPicker.Pick(pattern, rows, cols, K, clientId, cycleIndex) → (rows[], cols[])
  3. TicketReserveRequestPacket(Id=13) [Count][Row,Col...] 배치 송신
  4. 사이클 처분 (Random.Shared로 분기):
     - pay (기본 70%): 짧은 지연 → TicketPayRequestPacket(Id=14) 송신
                       → PaymentDelayMs+100ms 대기(FIN 타이밍 보장) → 반환
     - abandon (15%):  pay 없이 즉시 반환 → graceful FIN → ReleaseAll 즉시
     - expire (15%):   TtlMs+500ms idle → TTL 스위퍼 자동 만료 → 반환
```

### SeatPicker 경합 패턴

```csharp
// hotspot  → 항상 그리드 앞 K석 → 최대 CAS 충돌
// spread   → offset=(clientId×K + cycleIndex×K) % total → 부하 분산
// grind    → offset=(clientId×K + cycleIndex) % total  → 전 그리드 공격적 회전
SeatPicker.Pick(ContentionPattern.Spread, rows:5, cols:8, k:2, clientId:3, cycleIndex:7)
// → (rows:[?], cols:[?]) — 0≤seatId<40 보장, wrap-around
```

### KPI 보존식 체크

```
KpiConservation: totalReserved != confirmed + payfail + abandon + expired + reserved
SeatConservation: (free + reserved + sold) != totalSeats
SlotLeak: reserved != 0 (안정화 후)
NoTicketData: isTicketing && !attachMode && ticketSnap == null  ← vacuous PASS 방지
```

---

## 변경 파일 목록

| 파일 | 구분 | 내용 요약 |
|------|------|-----------|
| `SoakTest/Workloads/IWorkload.cs` | 신규 | 워크로드 전략 인터페이스 |
| `SoakTest/Workloads/DamageWorkload.cs` | 신규 | DamagePacket 로직 추출 (동작 무변경) |
| `SoakTest/Workloads/TicketingWorkload.cs` | 신규 | fire-and-pace 티켓팅 워크로드 |
| `SoakTest/SeatPicker.cs` | 신규 | ContentionPattern + Pick() 순수 함수 |
| `SoakTest/SoakStats.cs` | 수정 | +4 티켓팅 카운터 (Interlocked) |
| `SoakTest/SoakClient.cs` | 수정 | IWorkload 위임, cycleIndex 로컬 |
| `SoakTest/SoakOptions.cs` | 수정 | WorkloadType 열거, 8개 신규 CLI 옵션 |
| `SoakTest/ServerProcess.cs` | 수정 | TicketSnapshot/Config, [TICKET] 파싱, TryStart 오버라이드 |
| `SoakTest/SoakReport.cs` | 수정 | NoTicketData/SlotLeak/KpiConservation/SeatConservation Hard 체크 |
| `SoakTest/Program.cs` | 수정 | 모드 인지형 워크로드 와이어링, 권위 read, 판정 |

---

## 빌드 검증

```powershell
dotnet build -c Release        # 0오류·0경고
dotnet test ServerLib.Tests    # 172/172 PASS

# Damage 회귀 (20s)
dotnet run -c Release --project SoakTest -- --clients 10
# → PASS (DataLoss·SessionLeak·ClientErrorRateHigh 모두 OK)

# 티켓팅 스모크 (40s)
dotnet run -c Release --project SoakTest -- \
  --workload ticketing --clients 15 --rows 5 --cols 8 \
  --seats-per-session 2 --ttl 5 --payment-delay 50 --contention spread
# → PASS: sold=36, KPI 56=56, seats 40=40, reserved=0
```

---

## 치명적 함정 (구현 시 준수)

1. **KPI 판별 = `reserved_total=`** (`free=`는 이벤트 라인도 포함 → 오탐)
2. **권위 ticket read는 'q' 송신 전** (DisposeAsync 이후 Pipe 폐기 → 스냅샷 유실)
3. **pay FIN = PaymentDelayMs+100ms 후** (조기 FIN → OCE→ReleaseAll → confirmed 거짓 하락)
4. **TTL < IdleTimeout** (expire 사이클 idle 보유 중 idle-kick 차단)
5. **SeatTaken/RateLimited는 클라 에러 아님** (ClientErrorRateHigh 오탐 방지)
6. **안정화 timeout > TTL** (스위퍼 TTL-expire 처리 시간 확보)
7. **rows×cols ≤ 255** (1바이트 seatId; SoakOptions에서 검증·clamp)
8. **MaxConnectionsPerIp ≥ clients×2** (전 클라 127.0.0.1 → 기본 50 초과 차단 방지)
9. **NoTicketData [Hard]** (ticketSnap==null → 전 체크 false → vacuous PASS → 반드시 FAIL 처리)

---

## 향후 확장 포인트

- **SeatPicker 단위 테스트** — 순수 함수이므로 `ServerLib.Tests`에 hotspot/spread/grind 분포·wrap 검증 추가 가능
- **SeatMapRequest 포함 경로** — 현재 좌석맵 조회 없이 직접 예약(충돌은 seattaken 카운터로 관찰). 좌석맵 조회 후 빈 좌석 우선 선택 모드 추가 가능
- **장시간(10분+) 구동** — `--report 30`으로 heap 추이 관찰·[PROGRESS] 무한 증가 없음 확인
- **경합 스윕** — `--contention hotspot`(높은 seattaken·낮은 confirmed) + `grind`(매진 압박+churn) 조합 테스트
- **만료 경로 집중** — `--expire-rate 0.5`로 expired 카운터 > 0 및 최종 reserved==0 집중 검증
