// =============================================================================
// CounterServer — 경합(contention) 시연용 공유 카운터 서버 예제
// =============================================================================
// 동작: 127.0.0.1:9300에서 TCP 연결을 수락하고, 모든 세션이 공유하는 카운터 하나를
//       IncrementPacket(Id=3)로 +1, DecrementPacket(Id=4)로 −1 갱신합니다.
//       CounterQueryPacket(Id=18)을 받으면 현재 값을 CounterValuePacket(Id=19)으로 회신합니다.
//
// 학습 포인트:
//   여러 연결이 동시에 같은 메모리를 갱신해도(=경합), 갱신을 Interlocked 원자 연산으로 하면
//   최종값이 "더한 횟수 − 뺀 횟수"라는 결정적 기대값과 정확히 일치합니다.
//   경합 자체는 없앨 수 없고, 경합 하에서도 정확하도록 만드는 것이 핵심입니다.
//
// 실행법:
//   dotnet run --project CounterServer      (먼저 실행)
//   dotnet run --project CounterClient      (다른 터미널에서 실행 → PASS/FAIL 출력)
//   종료: 아무 키나 누르세요.
//
// ※ 검증은 "새로 시작한 서버 + 클라이언트 1회 실행"을 전제로 합니다.
//   카운터는 프로세스 수명 동안 누적되므로, 다시 검증하려면 서버를 재시작하십시오.
//
// ServerLib 핵심 흐름:
//   ServerNet.CreateListener()   — 리스너 팩토리(구현체 SocketPipelineListener는 internal)
//   listener.On* = ...          — 콜백 등록 (Start() 전에 반드시 완료)
//   listener.Start(port, addr)  — TCP accept 루프 시작 (Non-blocking)
//   OnReceived(session, data)   — 완전한 패킷 1개 단위로 호출 (프레이밍 자동 처리)
//   listener.Stop()             — 서버 종료
// =============================================================================

using System.Net;
using CounterServer;
using ServerLib;
using ServerLib.Interface;

// ── 0. 공유 상태 준비 ─────────────────────────────────────────────────────────
//
// CounterState: 이 예제의 경합 지점. 모든 세션이 이 인스턴스 하나를 동시에 갱신합니다.
//   내부 필드는 Interlocked 전용 long 2개(Value·AppliedOps)이며 갱신·조회 모두 무할당·논블로킹입니다.
//   static이 아닌 인스턴스로 두는 이유: 서버 1개 = 카운터 1개로 격리해 테스트가 서로 오염되지 않게 하기 위함입니다.
var state = new CounterState();

// CounterHandler: 프레임 검증(ID·본문 길이) → 라우팅 → 상태 갱신/조회 응답을 담당합니다.
//   불변 객체(생성 후 state 참조만 보유)이므로 여러 IO 스레드가 동시에 HandleAsync를 호출해도 안전합니다.
var handler = new CounterHandler(state);

// ── 1. 리스너 생성 ────────────────────────────────────────────────────────────
//
// ServerNet.CreateListener():
//   내부 구현체 SocketPipelineListener(internal)를 생성해 IServerListener로 반환합니다.
//   → 소비자는 구체 타입을 몰라도 인터페이스만으로 서버를 제어합니다(캡슐화).
//   → 즉시 반환(Non-blocking). Start() 전까지 소켓·accept 루프가 시작되지 않습니다.
IServerListener listener = ServerNet.CreateListener();

// ── 2. 콜백 등록 (Start() 호출 전에 완료해야 합니다) ─────────────────────────
//
// Start() 이후 콜백을 설정하면 InvalidOperationException이 발생합니다.
// → ServerLib가 레이스 컨디션 없이 콜백을 읽기 위해 시작 이후 변경을 금지합니다.

// OnClientConnected: 새 TCP 연결이 수락(accept)된 직후 호출됩니다.
listener.OnClientConnected = (ISession session) =>
{
    Console.WriteLine($"[연결] {session.RemoteEndPoint}  세션={session.SessionId:N}");
    // ValueTask.CompletedTask: 이미 완료된 캐시드 인스턴스 → Task.CompletedTask와 달리 참조 할당조차 없는 무할당 경로.
    return ValueTask.CompletedTask;
};

// OnClientDisconnected: 정상/비정상 종료 후 호출됩니다.
// 프로토콜 위반으로 세션이 끊긴 경우에도(OnClientError 다음에) 여기로 옵니다.
listener.OnClientDisconnected = (ISession session) =>
{
    Console.WriteLine($"[해제] {session.RemoteEndPoint}  세션={session.SessionId:N}");
    return ValueTask.CompletedTask;
};

// OnClientError: 패킷 파싱 실패, OnReceived 핸들러 예외(= CounterHandler의 InvalidDataException),
//   그리고 조회 응답 송신 실패 시 호출됩니다.
// ※ SocketException이 보인다고 해서 "상대가 종료했다"고 단정하면 안 됩니다.
//   ServerLib은 송신 시한 만료도 SocketError.TimedOut의 SocketException으로 변환하기 때문입니다.
//   원인과 무관하게 정책은 동일합니다: 로그를 남기고 해당 세션만 종료하며, 자동 재송신은 하지 않습니다
//   (재송신하면 증감이 중복 적용됐는지 알 수 없게 됩니다).
listener.OnClientError = (ISession session, Exception ex) =>
{
    Console.WriteLine($"[오류] {session.RemoteEndPoint}: {ex.GetType().Name} — {ex.Message}");
    Console.WriteLine("       → 이 세션만 종료합니다. 카운터 값과 다른 세션에는 영향이 없습니다.");
    return ValueTask.CompletedTask;
};

// ── 3. 수신 콜백 (경합 지점) ─────────────────────────────────────────────────
//
// OnReceived: 완전한 패킷 1개가 도착할 때마다 호출됩니다.
//
// [프레이밍 보장]
//   SocketPipelineSession이 System.IO.Pipelines의 PipeReader로 TCP 스트림을 패킷 단위로 조립합니다.
//   부분 패킷은 다음 ReadAsync에서 이어붙여 처리하므로, 이 콜백은 항상 '완전한 패킷 1개분'을 받습니다.
//
// [동시성]
//   같은 세션의 패킷은 수신 루프가 순차 await로 처리하지만,
//   서로 다른 세션은 각자의 IO 스레드에서 동시에 이 콜백에 진입할 수 있습니다 — 그것이 이 예제의 경합입니다.
//
// [data 소유권]
//   ReadOnlyMemory<byte>는 내부 수신 버퍼의 슬라이스 뷰입니다. 콜백 반환 후 재사용될 수 있으므로
//   보관하지 않고, 동기 구간에서 검증·역직렬화만 수행합니다(핸들러가 밖으로 내보내는 것은 long 값뿐).
listener.OnReceived = handler.HandleAsync;

// ── 4. 서버 시작 ──────────────────────────────────────────────────────────────
//
// listener.Start(port, bindAddress):
//   - 지정 포트에 소켓을 바인딩하고 listen(backlog)을 호출합니다.
//   - TCP accept 루프를 백그라운드 Task로 시작합니다 (Non-blocking: 이 줄 이후 즉시 다음 코드 실행).
// IPAddress.Loopback: 루프백(127.0.0.1) 전용 바인딩 → 외부 네트워크에 노출되지 않습니다.
//   Start(9300) 단일 인수 오버로드는 IPAddress.Any(전체 인터페이스)에 바인딩하므로,
//   인증 없는 이 학습용 카운터가 외부에서 임의 갱신될 수 있습니다. 반드시 주소를 명시하십시오.
// MaxConnections·IdleTimeout 미설정: 학습용 예제이므로 기본값(무제한·유휴 스윕 비활성)을 씁니다.
//   실제 서비스라면 listener.MaxConnections = 100; listener.IdleTimeout = TimeSpan.FromSeconds(30);
listener.Start(9300, IPAddress.Loopback);
Console.WriteLine("경합 카운터 서버 시작 — 127.0.0.1:9300  [종료: 아무 키나 누르세요]");
Console.WriteLine("  더하기=IncrementPacket(Id=3)  빼기=DecrementPacket(Id=4)  조회=CounterQueryPacket(Id=18)");
Console.WriteLine("───────────────────────────────────────");

// 메인 스레드를 블로킹해 서버를 유지합니다. 키 입력 시 종료 흐름으로 진입합니다.
Console.ReadKey(intercept: true);
Console.WriteLine();

// ── 5. 서버 종료 ──────────────────────────────────────────────────────────────
//
// listener.Stop():
//   - 새 연결 수락을 중단합니다(_cts.Cancel() + 리슨 소켓 Dispose).
//   - 활성 세션을 DisposeAsync().GetResult()로 순차 정리합니다(동기 블로킹).
listener.Stop();

// 참고용 출력입니다 — 검증 판정이 아닙니다.
// 이 시점에 모든 클라이언트가 송신을 끝냈다는 보장이 없어(정지 상태가 아님) 기대값과 비교할 수 없습니다.
// PASS/FAIL 판정은 클라이언트가 "전 연결 확인 응답 → 최종 조회" 배리어를 거친 뒤에만 내립니다.
Console.WriteLine($"서버 종료. (참고용) 카운터 값 = {state.Value}");
