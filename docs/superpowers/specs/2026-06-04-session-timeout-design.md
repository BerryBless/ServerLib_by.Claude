# Session Idle Timeout 설계 문서

**날짜:** 2026-06-04  
**상태:** 승인됨

---

## 1. 배경 및 목적

`SocketPipelineListener`가 관리하는 세션 중 일정 시간 패킷을 보내지 않는 유휴(idle) 세션을 자동으로 감지하고 연결 해제하는 기능. `OnIdleTimeout` 콜백으로 타임아웃 기인 해제와 일반 해제를 구분할 수 있다.

**해결 목표:**
- 장기 유휴 클라이언트 자동 정리 → FD·메모리 누수 방지
- 타임아웃 기인 해제 시 `OnIdleTimeout` 콜백 발화 (로깅·지표 수집용)
- 리스너 수준 일괄 설정 (`IdleTimeout = TimeSpan.FromSeconds(30)`)

---

## 2. 설계 결정

| 항목 | 채택안 | 비고 |
|------|--------|------|
| 타임아웃 기준 | 유휴(마지막 수신 이후 경과 시간) | 절대 타임아웃(ConnectedAt 기준) 미채택 |
| 감지 방식 | 리스너 중앙 `PeriodicTimer` 스윕 | 세션별 CTS(GC 압력) 미채택 |
| 시간 추적 | `long _lastReceivedTicks` + `Interlocked` | `volatile`은 구조체 불가 |
| 스윕 간격 | `max(IdleTimeout/2, 1초)` | 최대 1.5× timeout 후 감지 |
| 콜백 순서 | `OnIdleTimeout` → `DisposeAsync` → `OnDisconnected` | 기존 해제 경로 재사용 |

---

## 3. 컴포넌트 구조

```
ServerLib/
├── Interface/
│   ├── ISession.cs              ← MOD: LastReceivedAt 추가
│   └── IServerListener.cs       ← MOD: IdleTimeout, OnIdleTimeout 추가
└── Core/
    └── Transport/
        ├── SocketPipelineSession.cs   ← MOD: _lastReceivedTicks + 스탬핑
        └── SocketPipelineListener.cs  ← MOD: IdleSweepLoopAsync

ServerLib.Tests/
├── Stubs/StubSession.cs          ← MOD: LastReceivedAt settable
└── SessionTimeoutTests.cs        ← NEW: 5개 단위 테스트

Server/
└── Program.cs                    ← MOD: IdleTimeout 설정 예제
```

---

## 4. 핵심 API

### ISession.LastReceivedAt

```csharp
/// <summary>마지막으로 데이터를 수신한 UTC 시각입니다. 연결 수립 시 ConnectedAt과 동일값으로 초기화됩니다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. Interlocked 기반으로 원자적 갱신됩니다.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
/// </list>
/// </remarks>
DateTimeOffset LastReceivedAt { get; }
```

### IServerListener 추가 멤버

```csharp
/// <summary>유휴 세션 타임아웃 기간입니다. null이면 유휴 감지를 비활성화합니다(기본값).</summary>
/// <remarks>Start() 호출 전에 설정해야 합니다. 서버 동작 중 변경은 지원하지 않습니다.</remarks>
TimeSpan? IdleTimeout { get; set; }

/// <summary>세션이 유휴 타임아웃으로 해제되기 직전 호출되는 콜백입니다.</summary>
/// <remarks>OnDisconnected보다 먼저 발화됩니다. I/O 스레드에서 호출되므로 동기 블로킹 금지.</remarks>
Func<ISession, ValueTask>? OnIdleTimeout { get; set; }
```

### SocketPipelineListener.IdleSweepLoopAsync (내부)

```csharp
if (IdleTimeout.HasValue)
    _ = IdleSweepLoopAsync(IdleTimeout.Value, _cts.Token);

private async Task IdleSweepLoopAsync(TimeSpan timeout, CancellationToken ct)
{
    var interval = TimeSpan.FromTicks(Math.Max(timeout.Ticks / 2, TimeSpan.FromSeconds(1).Ticks));
    using var timer = new PeriodicTimer(interval);
    while (await timer.WaitForNextTickAsync(ct))
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var session in _activeSessions.Values)
        {
            if (now - session.LastReceivedAt <= timeout) continue;
            try
            {
                if (OnIdleTimeout != null) await OnIdleTimeout(session);
                await session.DisposeAsync();
            }
            catch { /* 개별 세션 실패가 스윕 중단 방지 */ }
        }
    }
}
```

### Server/Program.cs 사용 예제

```csharp
listener.IdleTimeout = TimeSpan.FromSeconds(30);
listener.OnIdleTimeout = session =>
{
    Console.WriteLine($"[Timeout] {session.RemoteEndPoint}  idle={DateTimeOffset.UtcNow - session.LastReceivedAt:mm\\:ss}");
    return ValueTask.CompletedTask;
};
```

---

## 5. 변경 파일 목록

| 파일 | 유형 | 핵심 변경 |
|------|------|----------|
| `ServerLib/Interface/ISession.cs` | 수정 | `LastReceivedAt` 프로퍼티 + XML 주석 |
| `ServerLib/Interface/IServerListener.cs` | 수정 | `IdleTimeout`, `OnIdleTimeout` 프로퍼티 |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | `_lastReceivedTicks` 필드, `LastReceivedAt` 프로퍼티, FillPipeAsync 스탬핑 |
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | `IdleTimeout`, `OnIdleTimeout`, `IdleSweepLoopAsync` |
| `ServerLib.Tests/Stubs/StubSession.cs` | 수정 | `LastReceivedAt { get; set; }` |
| `ServerLib.Tests/SessionTimeoutTests.cs` | 신규 | 5개 단위 테스트 |
| `Server/Program.cs` | 수정 | `IdleTimeout` 사용 예제 |

---

## 6. 테스트 케이스

| 테스트명 | 검증 내용 |
|---------|----------|
| `LastReceivedAt_Initial_EqualsConnectedAt` | 초기값 = ConnectedAt |
| `IdleSweep_IdleSession_FiresOnIdleTimeoutThenDisconnects` | 50ms timeout + 200ms 대기로 스윕 확인 |
| `IdleSweep_ActiveSession_NotDisconnected` | LastReceivedAt 최근 → 해제 안 함 |
| `IdleTimeout_Null_NoSweep` | 타임아웃 미설정 시 해제 없음 |
| `OnIdleTimeout_Called_BeforeOnDisconnected` | 콜백 발화 순서 검증 |

---

## 7. 빌드 검증

```bash
dotnet build ClaudeCodeStudy.sln
dotnet test ServerLib.Tests --logger "console;verbosity=normal"
```

수동: Client 연결 → 30초 유휴 → 서버 `[Timeout]` 로그 출력 확인.

---

## 8. 향후 확장 포인트

- 세션별 개별 타임아웃 오버라이드
- 타임아웃 직전 "경고 패킷" 전송 후 유예기간 부여
- 타임아웃 통계를 `ServerMetrics`에 통합
