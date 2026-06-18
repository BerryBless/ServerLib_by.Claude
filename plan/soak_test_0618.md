# SoakTest 소크 테스트 하네스 설계

## 1. 배경 및 목적

ProudNet류 .NET 10 서버 라이브러리(ClaudeCodeStudy)의 **장시간 안정성**을 자동화 도구로 검증할 수단이 없었다.
과거 `StabilityTest`(버스트 안정성) 하네스가 2026-06-07 솔루션 슬림화로 삭제됐으나, 그 하네스를 위해 도입한 서버 훅은 그대로 남아있다:

| 훅 | 설명 |
|----|------|
| `[STATS] received= hp= gen= sessions= heapBytes= ...` | 머신 파싱용 권위 신호(1초 주기·토글 독립) |
| `listener.ActiveSessionCount` | 세션 수 직접 접근 |
| `--Server:Port=` / `--Server:AdminPort=` / `--Server:MonitorIntervalSeconds=` | CLI 오버라이드 |
| stdin `q` → graceful 종료 | 외부 종료 신호 |

**목표:** N개 클라이언트가 연결→송수신→해제를 무한 반복하며 서버에 지속 부하를 가하고, 중단 시점에 크래시·세션 누수·데이터 유실을 자동 판정하는 독립 콘솔 하네스를 구축한다. **서버 코드 수정 없이** 기존 훅을 재사용한다.

## 2. 설계 결정

| 항목 | 채택 | 후보 대안 | 이유 |
|------|------|-----------|------|
| 반복 방식 | 소크 테스트(무한) | 버스트 N회 | 장시간 안정성 검증 목적 |
| 위치 | 신규 독립 프로젝트 | Server/Client 내 통합 | 실행 독립성, 솔루션 구조 일치 |
| 서버 구동 | 자식 프로세스(child) + attach 모드 | 내부 IServerListener 재사용 | 서버 코드 미수정, 실제 exe 테스트 |
| 워크로드 | 연결 churn + 송수신 | 지속 연결, 대용량 전송 | 연결 수립/해제 경로를 반복 검증 |
| 판정 | 경량 관찰 + Hard 판정 | 전면 감사 | 빠른 결과, CI 통합 용이 |

## 3. 컴포넌트 구조

```
SoakTest/
├── SoakTest.csproj       — net10.0, ServerLib ProjectReference
├── Program.cs            — 오케스트레이션·Ctrl+C·q 처리·판정·종료코드
├── SoakOptions.cs        — CLI 파싱 (불변 init-only)
├── SoakStats.cs          — lock-free 집계 카운터 (Interlocked + Volatile)
├── SoakClient.cs         — 단일 클라 churn 루프 (await using graceful FIN)
├── ServerProcess.cs      — child Server.exe 구동·[STATS] 파싱·안정화 대기·graceful 종료
└── SoakReport.cs         — Hard/Soft 판정 → RESULT PASS/FAIL
```

의존: `SoakTest → ServerLib` (단방향). 서버는 자식 프로세스로 분리 — 컴파일 의존 없음.

## 4. 핵심 API

### CLI
```bash
# child 모드(기본): Server.exe 자동 구동
dotnet run -c Release --project SoakTest -- --clients 50 --port 9100

# attach 모드: 외부 서버에 부착(서버 측 Hard 체크 생략)
dotnet run -c Release --project SoakTest -- --attach --clients 50 --port 9000

# 전체 옵션
dotnet run -- --clients N --port N --admin-port N --sends N --churn-delay N --settle N --report N --attach
```

### 클라이언트 churn 루프 (핵심 패턴)
```csharp
// SoakClient.RunAsync 요약
while (!ct.IsCancellationRequested)
{
    await using IClientConnection conn = ServerNet.CreateClient(); // 매 사이클 새 연결 → churn
    conn.OnReceived = _ => { stats.IncReceived(); return ValueTask.CompletedTask; };
    try {
        await conn.ConnectAsync(host, port, ct);
        stats.IncConnect();
        for (int k = 0; k < sendsPerConn; k++) {
            await conn.SendAsync(_dmgBuf, ct); // 1회 직렬화 버퍼 재사용(무할당)
            stats.IncSent();
        }
        if (receiveSettleMs > 0) await Task.Delay(receiveSettleMs, ct);
    } catch (OperationCanceledException) { break; }
    catch (Exception) { stats.IncError(); }
    stats.IncCycle();
    if (churnDelayMs > 0) await Task.Delay(churnDelayMs, ct);
}
```

### 종료 순서 (메모리 함정 반영)
```
클라 cancel → Task.WhenAll(clients) → WaitForStability (q 보내기 전!) → SoakReport.Evaluate → server.DisposeAsync("q") → Exit(0|1)
```

### 판정 기준

| 체크 | 종류 | 기준 |
|------|------|------|
| Crash | **Hard** | 서버가 'q' 전에 예기치 않게 종료 |
| SessionLeak | **Hard** | 안정화 후 server.sessions ≠ 0 |
| DataLoss | **Hard** | 안정화 후 server.received < stats.Sent |
| ClientErrorRate | **Hard** | errors/connects > 5% |
| HeapGrowth | Soft | heap > baseline × 4 (자문만) |

## 5. 변경 파일 목록

| 파일 | 유형 | 내용 |
|------|------|------|
| `SoakTest/SoakTest.csproj` | 신규 | net10.0, ServerLib ProjectReference |
| `SoakTest/Program.cs` | 신규 | 오케스트레이션, Ctrl+C/q, 판정, 종료코드 |
| `SoakTest/SoakOptions.cs` | 신규 | CLI 파싱, 불변 레코드 |
| `SoakTest/SoakStats.cs` | 신규 | lock-free 집계 카운터 |
| `SoakTest/SoakClient.cs` | 신규 | 단일 클라 churn 루프 |
| `SoakTest/ServerProcess.cs` | 신규 | child Server.exe 구동, [STATS] 파싱, graceful 종료 |
| `SoakTest/SoakReport.cs` | 신규 | Hard/Soft 판정 |
| `ClaudeCodeStudy.sln` | 수정 | SoakTest 프로젝트 등록 |
| `plan/soak_test_0618.md` | 신규 | 이 설계 문서 |
| `CLAUDE.md` | 수정 | 플랜 문서 목록에 행 추가 |

## 6. 빌드 검증

```bash
# 1) Debug 빌드 (오류 확인)
dotnet build SoakTest/SoakTest.csproj

# 2) 서버 Release 빌드 (child 모드 전제)
dotnet build -c Release --project Server

# 3) 짧은 소크 스모크 (20클라, 'q'로 종료)
dotnet run -c Release --project SoakTest -- --clients 20 --port 9100

# 기대 출력:
# [SoakTest] 서버 구동: ...Server.exe
# [SoakTest] 서버 준비 완료
# [SoakTest] 20개 클라이언트 시작
# [PROGRESS] cycles=... conns=... sent=... recv=... ...
# (q 입력 후)
# [SoakTest] 모든 클라이언트 종료 완료
# [SoakTest] 서버 안정화 대기 중 ...
# RESULT PASS
# 종료코드: 0
```

## 7. 비자명 함정

| 함정 | 대응 |
|------|------|
| 종료 후 권위 read | WaitForStability는 반드시 server.DisposeAsync("q") **이전**에 호출 |
| 행 판정 오탐 | count-stable (sessions=0 AND received 3연속 불변) 후에만 비교 |
| DataLoss 결정성 | RST 미사용, graceful FIN(DisposeAsync) 전용 |
| HeapGrowth 상시 발생 | ArrayPool·Pipe 정상 확장 → Soft 경고. Hard 판정 미영향 |
| Release exe 필요 | child 모드 전 `dotnet build -c Release --project Server` 필수 |

## 8. 향후 확장 포인트

1. **`--duration` 옵션**: 시간제한 소크 후 자동 종료 → GitHub Actions CI 게이트
2. **램프업 스케줄**: 시간대별 클라 수 변동(시드 고정 재현성)
3. **티켓팅 워크로드 모드**: reserve→pay churn으로 lock-free 재고 장시간 검증
4. **echo 서버 변형**: 수신 패킷을 echo하는 minimal 서버로 순수 TCP 처리량 측정
