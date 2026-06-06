# 설계: 버스트 안정성 하네스 (StabilityTest)

**날짜:** 2026-06-06
**출처:** 사용자 요구 — "라이브러리 안정성을 테스트할 예제 서버/클라이언트, 임의의 시간에 갑자기 몰렸을 때 안정성이 1순위"
**상태:** 설계 승인됨 → 구현 계획 대기

## 배경 및 목적

기존 `LoadTest` 프로젝트는 **정상상태(steady-state)** 부하만 검증한다(N개 클라이언트가 일정하게 송신/에코). 그러나 실제 서버가 무너지는 지점은 **임의 시점의 급작스러운 폭주**다 — 순간 연결 폭주(connection storm)와 트래픽 스파이크(traffic spike)가 예측 불가능한 시점에 동시·교차로 발생할 때.

이 하네스의 **1순위는 처리량이 아니라 안정성**이다. 폭주가 닥쳤을 때 라이브러리(`SocketPipelineListener`/`SocketPipelineSession`)가 다음 4가지 실패 모드 중 **어느 것도 일으키지 않음**을 자동으로 증명해야 한다:

1. **행/데드락** — 프로세스가 살아 있으나 더 이상 패킷을 처리하지 못함(응답 정지).
2. **크래시** — 미처리 예외·OOM·소켓 고갈로 서버 프로세스가 죽음.
3. **메모리 누수** — 폭주가 가라앉은 뒤에도 메모리·세션 수가 baseline으로 복귀하지 않음.
4. **데이터 유실/손상** — 보낸 패킷 수 ≠ 서버가 처리한 수, 또는 카운터 불일치(프레이밍/파싱 손상).

**목표:** 위 4개 모드를 직접·비취약(non-fragile)하게 검증하는 전용 콘솔 하네스 `StabilityTest`를 만든다. 실시간 콘솔 모니터로 라이브 관찰을 제공하고, 종료 시 종합 PASS/FAIL 리포트 + 종료 코드(0/1)를 반환해 반복·CI 실행과 자기검증을 가능케 한다. 시드 고정 RNG로 임의 타이밍 폭주를 **재현 가능**하게 만든다.

**비목표(YAGNI):**
- **송신 경로(P3 송신게이트·`SessionSendTimeout`·`BroadcastAsync`) 검증 — 배제.** 예제 `Server`는 `OnReceived`에서 `SendAsync`를 호출하지 않으므로(에코·브로드캐스트 없음) 이 하네스는 송신 경로를 자극하지 않는다. 송신 경로 안정성은 에코/브로드캐스트 서버 변형이 필요한 **후속 사이클**로 둔다. (사용자 선택: "수신·연결·수명주기만" 린 범위.)
- **최대 처리량(TPS) 벤치마크 — 배제.** 안정성과 직교하며 `Benchmark` 프로젝트 영역.
- **분산/멀티머신 부하 — 배제.** 단일 머신 loopback으로 충분하며 재현성·결정성이 더 중요.
- **연결→급작 이탈(RST) 시나리오의 데이터 정합성 검증 — 배제.** 카오스 클라이언트는 데이터를 보내지 않으므로(아래) 데이터 유실 단언 대상이 아니다. RST는 세션 정리/누수 자극용으로만 사용.

## 설계 결정

| 항목 | 채택 | 대안(미채택) | 사유 |
|------|------|------------|------|
| 서버 관찰 모델 | **B: 자식 프로세스 서버** (`Server.exe`를 child로 실행, `System.Diagnostics.Process`로 관찰) | A: in-process 호스팅 / C: 블랙박스 클라이언트만 | 실제 출하 예제를 프로세스 격리하에 검증. 크래시(`HasExited`)·메모리(`PrivateMemorySize64`)를 OS 수준에서 직접 관찰 |
| 폭주 모양 | **연결 폭주 + 트래픽 스파이크, 무작위 타이밍** | 단일 램프업 / 한 종류만 | "임의의 시간에 갑자기" 요구에 가장 충실 — 가장 가혹 |
| 타이밍 | **시드 고정 `Random`** | 무시드 난수 / 고정 스케줄 | 실패한 폭주를 **재현**해야 디버깅 가능 |
| 클라이언트 | **혼합: 신뢰=`SocketPipelineClient`, 카오스=raw `Socket`** | 전부 Pipeline / 전부 raw | 신뢰 클라이언트로 라이브러리 클라이언트(C2 dispose 경로)까지 검증, 카오스는 RST(`Linger 0`) 세밀 제어 |
| 데이터 유실 단언 | **신뢰 클라이언트만 카운트, 전원 graceful FIN 종료** | 모든 클라가 송신 | RST는 in-flight 바이트를 폐기 → 집계 비결정. 카오스는 **앱 데이터 0바이트** 송신해야 `received == sent`가 깨끗이 성립 |
| 권위 카운트 읽기 | **부하 중단 후 `received`가 N회 연속 안정될 때까지 폴링 → 그 값을 단언 → 이후 `q` 송신** | 종료(`q`) 후 종료 라인 파싱 | `Stop()`은 버퍼 미프레임 데이터를 폐기 가능 → 종료 후 읽으면 정상 송신분을 유실로 오판(false FAIL). count-stable 게이트가 결정적 |
| 서버 신호원 | **always-on 단조 카운터 + 토글 독립 세션 수** | 기존 `windowPackets`/`metrics` 재사용 | `windowPackets`는 매 구간 리셋(`Exchange(…,0)`), `metrics`는 `EnableMetrics` 토글 의존 → 권위 카운터로 부적합 |
| 누수 1차 단언 | **`sessions == 0` (하드)** | heap 임계값 (하드) | 세션 복귀=0은 FIN·RST 정리 경로를 결정적으로 증명. heap은 강제 GC 없이 안 줄어 임계 단언이 flaky |
| heap 단언 | **baseline × tolerance 이하 (소프트/경고)** | 하드 FAIL | GC 비결정성으로 하드화하면 거짓 실패. 추세 신호로만 |
| 행 판정 범위 | **부하 활성 구간에 한정** | 상시 "received 정지=행" | drain/settle 중 `received` 정지는 **정상** 상태. 부하 제공 중에만 정지를 행으로 판정 |

## 컴포넌트 구조

신규 `StabilityTest` 콘솔 프로젝트 + 라이브러리/예제 소폭 수정 2건.

```
StabilityTest/                       (신규 프로젝트)
├─ Program.cs            오케스트레이터: 실행 수명주기·종합 판정·종료 코드
├─ StabilityConfig.cs    파라미터: seed, 구간 길이, storm/spike 범위, tolerance, port
├─ ServerProcess.cs      Server.exe child 실행; stdin/stdout 리다이렉트;
│                        [STATS] 파싱 → Received/Test/Sessions/HeapBytes/Gen2 노출;
│                        HasExited, PrivateMemorySize64; stdin "q"로 graceful Stop
├─ BurstScheduler.cs     시드 RNG → BurstEvent 타임라인(무작위 시점)
├─ ReliableClient.cs     SocketPipelineClient: connect → 카운트 Inc/Dec 송신 →
│                        flush → graceful FIN close; sentInc/sentDec 추적
├─ ChaosClient.cs        raw Socket: 연결 폭주 + 급작 RST(LingerOption(true,0));
│                        앱 데이터 0바이트 (연결/수명주기 자극 전용)
├─ StabilityMonitor.cs   주기적 라이브 콘솔 라인 (LoadMonitor 스타일)
└─ StabilityReport.cs    증거 수집 → 4개 체크 평가 → 표 출력 → 종료 코드

ServerLib/Core/Transport/
└─ SocketPipelineListener.cs   (수정) public int ActiveSessionCount => _activeSessions.Count;

Server/
└─ Program.cs                  (수정) always-on long _totalReceived + [STATS] 구조화 라인
```

### 의존 방향

```
StabilityTest ──launches(child process)──▶ Server.exe
StabilityTest ──TCP(loopback)──▶ Server (accept/recv 경로 = SUT)
StabilityTest ──references──▶ ServerLib (SocketPipelineClient, PacketPool, Inc/DecrementPacket)
```

`StabilityTest`는 `ServerLib`를 참조(클라이언트·패킷·헤더 헬퍼 사용). 서버는 별도 프로세스이므로 컴파일 의존 없음. Core→Interface 의존 규칙 불변.

## 핵심 동작

### 서버 수정: 커맨드라인 설정 + always-on 권위 카운터 + [STATS] 라인

하네스는 child의 **포트**(개발 서버 9000과 충돌 회피)와 **모니터 주기**(count-stable 폴링이 충분히 자주 일어나도록 1초)를 제어해야 한다. 현재 `Server`는 args를 무시하고 자기 바이너리 폴더의 `appsettings.json`만 읽는다(`AppContext.BaseDirectory` 기준). 따라서 `ConfigurationBuilder`에 커맨드라인 소스를 추가한다(예제 서버를 JSON 편집 없이 설정 가능하게 만드는 소폭 개선):

```csharp
// AddCommandLine: appsettings.json 위에 args 오버라이드 계층을 얹는다 → 하네스가 포트·주기·토글을 인자로 제어.
// 예: Server.exe --Server:Port=9100 --Server:MonitorIntervalSeconds=1
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddCommandLine(args)
    .Build();
```

`Microsoft.Extensions.Configuration.CommandLine` 패키지 참조 추가. 하네스는 `--Server:Port=910x --Server:MonitorIntervalSeconds=1`로 child를 띄운다.

`OnReceived`에서 `EnableMetrics` 토글과 무관하게 단조 증가하는 카운터를 둔다:

```csharp
long totalReceived = 0;              // always-on: metrics 토글과 독립한 권위 수신 카운트
// ... OnReceived 내부:
Interlocked.Increment(ref totalReceived);
```

기존 모니터 루프(`MonitorIntervalSeconds` 주기)와 종료 직전에 **구조화 ASCII 라인**을 추가 출력(기존 한국어 라인은 유지):

```csharp
// [STATS]: 하네스가 머신 파싱하는 권위 신호. ASCII·고정 키=값, 토글 독립 소스만 사용.
Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} " +
                  $"test={Volatile.Read(ref test)} " +
                  $"sessions={listener.ActiveSessionCount} " +    // 토글 독립(레지스트리/메트릭 무관)
                  $"heapBytes={GC.GetTotalMemory(false)} " +      // 서버측 관리 힙 — 외부 PrivateBytes보다 정밀한 누수 신호
                  $"gen2={GC.CollectionCount(2)}");
```

- `received` — 데이터 유실 1차 신호(누적, 토글 독립).
- `test` — 손상/순서 신호(Inc−Dec 순증감).
- `sessions` — 누수 1차 신호(아래 `ActiveSessionCount`).
- `heapBytes`/`gen2` — 누수 보조(소프트) 신호.

### 라이브러리 수정: 토글 독립 세션 수

`SocketPipelineListener`에 상시 사용 가능한 활성 세션 수를 노출(레지스트리/메트릭 토글과 무관):

```csharp
/// <summary>현재 활성 세션 수입니다. 세션 레지스트리·메트릭 토글과 무관하게 항상 사용 가능합니다.</summary>
/// <remarks>Thread-safe. ConcurrentDictionary.Count 경유(주기적 통계용으로 비용 무시 가능). Zero-allocation.</remarks>
public int ActiveSessionCount => _activeSessions.Count;
```

이는 누수의 **결정적** 단언 소스다 — 폭주(FIN+RST)가 가라앉은 뒤 0으로 복귀하면 정리 경로가 모든 연결을 회수했음을 증명한다.

### BurstScheduler — 시드 RNG 무작위 타임라인

시드 고정 `Random`(시작 시 시드 콘솔 출력 → 실패 재현 가능)으로 두 이벤트를 무작위 간격으로 생성:

- **연결 폭주(ConnectionStorm)** — K개 카오스 클라이언트(K 무작위, 예: 200–1000)를 거의 동시에 spawn → connect → 짧게 유휴 → 급작 RST close. accept 루프 + 세션 정리 자극.
- **트래픽 스파이크(TrafficSpike)** — 활성 신뢰 클라이언트 각자가 무작위 런(예: 500–5000)의 본문 없는 Inc/Dec 패킷을 연속 송신 후 유휴 복귀. 합쳐진 소형 패킷의 파이프라인 프레이밍 자극.

```csharp
// 본문 없는 Increment 패킷 = 4바이트 헤더만 [Id(2)=3 | bodyLen(2)=0]. 폭주 시 스트림에서 합쳐짐(coalesce)
// → 서버 파이프라인이 각 4바이트 경계를 정확히 프레이밍하는지가 데이터 정합 검증의 핵심.
```

### 실행 수명주기 (Program.cs)

1. **실행 & 준비** — child 시작, `[Server] port …` 라인 대기.
2. **워밍업 & baseline** — 신뢰 클라이언트 소수 연결; baseline `heapBytes` 캡처, `sessions` 추적 확인.
3. **폭주 구간**(기본 ~90초) — 스케줄러가 무작위 storm+spike 발사; 모니터 라이브 출력; 라이브니스 프로브 가동. **상시 감시**: child `HasExited`→**CRASH**, 부하 활성 구간에 라이브니스 실패→**HANG**.
4. **drain & settle**(기본 ~20초) — 부하 제공 중단; `[STATS] received`가 **N회 연속 안정**될 때까지 폴링(서버가 백로그 소진).
5. **권위 읽기** — 안정된 `received`/`test`/`sessions`/`heapBytes` 기록. **그 다음** child stdin에 `q` 송신, graceful 종료·종료 코드 수신.
6. **종합 판정** — 4개 체크 평가 후 표 출력·종료 코드.

## 검증 — 4개 체크 (핵심)

| 체크 | 신호 | 통과 조건 | 강도 |
|------|------|----------|------|
| **크래시** | 실행 중 `Process.HasExited`; 최종 종료 코드 | 중도 종료 없음; graceful 종료 코드 0 | **하드** |
| **행** | 부하 활성 구간에 `[STATS] received` 전진; 카나리 connect가 데드라인 내 성공 | 모든 부하 활성 구간에서 전진 | **하드** |
| **데이터 유실** | 안정 `received` vs Σ 신뢰 클라 `sent` | `received == sent` (정확, 카오스는 0 송신) | **하드** |
| **손상** | `test` vs (Σ sentInc − Σ sentDec) | 정확히 일치 | **하드** |
| **누수(세션)** | settle 후 `[STATS] sessions` | `== 0` (모든 FIN+RST 정리됨) | **하드** |
| **누수(힙)** | settle 후 `heapBytes` vs baseline | `≤ baseline × tolerance` (관대, 예: 2×) | **소프트/경고** |

**하드** 실패 1개라도 → 전체 FAIL, 종료 코드 1. **소프트**는 리포트 경고로만 표기(FAIL 아님). 리포트는 각 체크의 실측 수치를 증거로 표 출력.

## 재현성 & 안전

- 시드 RNG; 시드를 시작 라인·리포트 헤더에 출력.
- loopback 전용(`127.0.0.1`, 전용 테스트 포트) → 네트워크 변동 배제.
- 하네스가 child 전체 수명주기 소유 — 하네스 abort 시 child kill(orphan `Server.exe` 방지).
- child는 레지스트리·메트릭 기본값(둘 다 `true`)으로 실행하나, `sessions`/`received`는 **토글 독립 소스**에서 오므로 설정 변경으로 판정이 조용히 약화될 수 없다.

## 변경 파일 목록

| 파일 | 종류 | 내용 |
|------|------|------|
| `StabilityTest/StabilityTest.csproj` | 신규 | 콘솔 프로젝트, `ServerLib` 참조 |
| `StabilityTest/Program.cs` | 신규 | 오케스트레이터·수명주기·종합 판정·종료 코드 |
| `StabilityTest/StabilityConfig.cs` | 신규 | 파라미터(seed, 구간 길이, storm/spike 범위, tolerance, port) |
| `StabilityTest/ServerProcess.cs` | 신규 | child 실행·관찰([STATS] 파싱, HasExited, PrivateMemory, graceful Stop) |
| `StabilityTest/BurstScheduler.cs` | 신규 | 시드 RNG 무작위 폭주 타임라인 |
| `StabilityTest/ReliableClient.cs` | 신규 | SocketPipelineClient 신뢰 클라이언트(카운트 송신·graceful 종료) |
| `StabilityTest/ChaosClient.cs` | 신규 | raw Socket 카오스 클라이언트(연결 폭주·RST, 0바이트) |
| `StabilityTest/StabilityMonitor.cs` | 신규 | 라이브 콘솔 모니터 |
| `StabilityTest/StabilityReport.cs` | 신규 | 4개 체크 평가·PASS/FAIL 표·종료 코드 |
| `Server/Program.cs` | 수정 | `.AddCommandLine(args)` + always-on `_totalReceived` + 구조화 `[STATS]` 라인(기존 출력 유지) |
| `Server/Server.csproj` | 수정 | `Microsoft.Extensions.Configuration.CommandLine` 패키지 참조 |
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | `public int ActiveSessionCount` 추가 |
| `ClaudeCodeStudy.sln` | 수정 | `StabilityTest` 프로젝트 등록 |

비변경: 기존 `LoadTest`(steady-state 역할 유지), 송신 경로 코드(`SessionSendTimeout`/`BroadcastAsync` — 린 범위 밖), Interface 시그니처.

## 빌드 검증

```
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release
# 단독 실행(기본 시드·기본 구간):
dotnet run -c Release --project StabilityTest
# 시드/구간 지정 예:
dotnet run -c Release --project StabilityTest -- --seed 12345 --burst 90 --settle 20 --port 9100
# 종료 코드로 PASS/FAIL 확인 (PowerShell):
echo $LASTEXITCODE        # 0 = PASS, 1 = FAIL
```

기존 회귀: `dotnet test ClaudeCodeStudy.sln -c Release` — 기존 테스트 전부 통과(라이브러리 변경은 `ActiveSessionCount` 추가뿐, 기존 동작 불변).

## 향후 확장 포인트

- **송신 경로 안정성(후속 사이클):** 에코/브로드캐스트 서버 변형 + 느린 리더 클라이언트로 P3 송신게이트 행·`SessionSendTimeout` 발화·`BroadcastAsync` 백프레셔를 폭주 하에 검증. 본 하네스의 `ServerProcess`/`BurstScheduler`/리포트 골격 재사용.
- **연결→급작 이탈 정합성:** 별도 카운트 채널을 두면 RST 클라이언트의 "ACK된 분까지만 received" 정밀 단언도 가능(현재는 카오스 0송신으로 회피).
- **CI 게이트화:** 종료 코드 기반이므로 파이프라인에 야간 안정성 잡으로 등록 가능. 시드를 매 실행 회전시키되 실패 시 시드를 아티팩트로 보존해 재현.
- **자원 한계 자극:** `ulimit`/포트 고갈/`backlog` 초과를 의도적으로 유발하는 극한 모드 추가.
```