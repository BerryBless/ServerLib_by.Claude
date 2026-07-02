# EchoWeb — 웹 기반 에코 데모 서버 설계

## 1. 배경 및 목적

`EchoServer`/`EchoClient`는 ServerLib(raw TCP, 4바이트 길이 프리픽스 바이너리 프레이밍, 포트 9000)를 사용하는 콘솔 예제다. 브라우저는 이 TCP 프로토콜을 직접 말할 수 없기 때문에, "브라우저에서 에코 서버를 체험"하려면 프로토콜 변환 계층이 필요하다.

`EchoWeb`은 이 변환 계층 역할만 담당한다. 에코 로직 자체(패킷 파싱·응답 생성)는 기존 `EchoServer.exe`를 그대로 재사용하고, 새 프로젝트는 브라우저 WebSocket 세션을 ServerLib `IClientConnection`(TCP) 세션으로 중계하는 브리지만 구현한다.

## 2. 설계 결정

| 항목 | 채택안 | 대안 | 채택 사유 |
|------|--------|------|-----------|
| 브라우저 전송 | WebSocket | HTTP POST/fetch 폴링 | 지속 연결로 실시간 에코 스트림 시연에 적합. EchoClient의 지속 연결 특성과 대응 |
| 프로세스 구성 | 별도 프로세스(EchoWeb ↔ EchoServer.exe) | 단일 프로세스 in-process 통합 | 라이브러리의 서버/클라이언트 분리 철학을 그대로 유지, 기존 EchoServer 예제 재사용 |
| 세션 매핑 | WebSocket 1개 = EchoClient(TCP) 1개 | 커넥션 풀 공유 | per-session 격리로 실제 클라이언트 동작을 그대로 시연, 상태 혼선 없음 |
| 송신 직렬화 | `Channel<string>` 단일 소비자 펌프 | 매 수신 시 즉시 `WebSocket.SendAsync` 호출 | `WebSocket.SendAsync`는 동시 호출 금지 — ServerLib IO 스레드(`OnReceived`)와 WS 수신 루프가 동시에 쓰기를 시도할 수 있어 직렬화 필수 |
| Teardown | 단일 `linkCts`로 양방향 신호 수렴 | 각 방향 독립 처리 | 브라우저 종료·9000 드롭 두 실패원을 하나의 순서(Cancel→채널종료→펌프대기→WS close→dispose)로 고정해 소켓 누수·hang 방지 |

## 3. 컴포넌트 구조

```
[브라우저] ←WebSocket ws://127.0.0.1:8080/ws→ [EchoWeb] ←TCP 9000 (ServerLib)→ [EchoServer.exe]
```

```
EchoWeb/
  EchoWeb.csproj      # Sdk="Microsoft.NET.Sdk.Web", net10.0, ProjectReference → ServerLib
  Program.cs           # ASP.NET Core minimal API + WebSocket↔TCP 브리지 (BridgeAsync, PumpOutboundAsync)
  wwwroot/
    index.html          # 채팅형 UI (textContent 렌더, 연결 상태 표시)
```

의존 관계: `EchoWeb → ServerLib`(소스 참조), `EchoWeb → EchoServer`(런타임 TCP 연결만, 프로젝트 참조 없음 — 완전히 독립된 프로세스).

## 4. 핵심 API

```csharp
// 서버 진입점
app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { ...; return; }
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await BridgeAsync(socket, serializer, context.RequestAborted);
});

// 브리지 루프 (WebSocket 연결당 1회)
await using IClientConnection echo = ServerNet.CreateClient();
echo.OnReceived = data => { /* 즉시 역직렬화 → string enqueue */ };
echo.OnDisconnected = () => linkCts.Cancel();
await echo.ConnectAsync("127.0.0.1", 9000, linkCts.Token);
Task pumpTask = PumpOutboundAsync(socket, outbound.Reader);   // 단일 소비자
// WS 수신 루프 → echo.SendAsync(new EchoPacket { Message = text })
```

## 5. 변경 파일 목록

| 파일 | 내용 |
|------|------|
| `EchoWeb/EchoWeb.csproj` | 신규 — 웹 SDK, net10.0, ServerLib 참조 |
| `EchoWeb/Program.cs` | 신규 — WebSocket ↔ TCP 에코 브리지 (`BridgeAsync`, `PumpOutboundAsync`) |
| `EchoWeb/wwwroot/index.html` | 신규 — 채팅형 데모 UI |
| `ClaudeCodeStudy.sln` | EchoWeb 프로젝트 추가 |
| `CLAUDE.md` | "예제 코드 위치"에 EchoWeb 항목 추가 |

## 6. 빌드 검증

```
dotnet build ClaudeCodeStudy.sln   # 0 오류 확인됨 (기존 CS0419 경고 10건은 EchoWeb과 무관한 사전 존재 경고)
```

수동 통합 검증 (2026-07-02 완료):
1. `EchoServer`(9000)·`EchoWeb`(127.0.0.1:8080)를 각각 별도 콘솔 프로세스로 기동.
2. WebSocket 클라이언트로 `/ws` 연결 → 텍스트 전송 → 동일 텍스트 에코 수신 확인 (round-trip match).
3. 정적 파일 서빙(`GET /` → `index.html`, 200) 확인.
4. **장애 경로:** EchoServer(9000) 중지 후 `/ws` 재연결 → 브라우저에 `[에러] 에코 서버(127.0.0.1:9000)에 연결할 수 없습니다: ...` 텍스트 수신, WebSocket은 `Open` 상태 유지한 채 hang 없이 즉시(약 2초, TCP RST 대기 포함) 응답 — 서버 프로세스도 예외 없이 정상 유지됨.

자동 테스트는 이번 범위에서 명시적으로 제외(WebSocket+TCP 브리지 통합 테스트는 9000 에코 서버 동시 구동이 필요해 하네스 비용 대비 이득이 낮다고 판단). 수동 검증으로 대체.

## 7. 향후 확장 포인트

- 다중 패킷 타입(Chat/Ping 등) 시연 탭 추가.
- 접속자 수·RTT 등 `IClientConnection.Rtt` 실시간 표시.
- `WebApplicationFactory` 기반 WebSocket 통합 테스트 추가(9000을 in-process 리스너로 대체하면 하네스 비용 절감 가능).
