// =============================================================================
// EchoServer — ServerLib IServerListener 사용 예제
// =============================================================================
// 동작: 포트 9000에서 TCP 연결을 수락하고, 클라이언트가 보낸 EchoPacket(Id=1)을
//       역직렬화한 뒤 동일 Message 내용을 담은 EchoPacket으로 응답합니다.
//
// 실행법:
//   dotnet run --project EchoServer
//   종료: 아무 키나 누르세요.
//
// ServerLib 핵심 흐름:
//   ServerNet.CreateListener()        — 리스너 팩토리
//   listener.On* = ...               — 콜백 등록 (Start() 전에 반드시 완료)
//   listener.Start(port)             — TCP accept 루프 시작 (Non-blocking)
//   OnReceived(session, data)        — 패킷 1개 단위로 호출 (프레이밍 자동 처리됨)
//   session.SendAsync<EchoPacket>()  — 직렬화+풀버퍼+송신 한 번에 처리
//   listener.Stop()                  — 서버 종료
// =============================================================================

using System.Net;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

// BinaryPacketSerializer: 내부 상태가 없는 무상태(stateless) 클래스.
// → 여러 IO 스레드가 동시에 호출해도 안전(Thread-safe). static readonly로 1회만 생성해 공유합니다.
// 직렬화 시 스택 전용 SpanWriter/SpanReader(ref struct)를 사용하므로 직렬화기 자체는 Zero-allocation.
var serializer = new BinaryPacketSerializer();

// ── 1. 리스너 생성 ────────────────────────────────────────────────────────────
//
// ServerNet.CreateListener():
//   내부 구현체인 SocketPipelineListener(internal 클래스)를 생성하고
//   IServerListener 인터페이스로 반환합니다.
//   → 소비자는 구체 타입을 알 필요 없이 인터페이스만으로 서버를 제어합니다(캡슐화).
//   → 즉시 반환(Non-blocking). Start()를 호출하기 전까지 소켓·accept 루프가 시작되지 않습니다.
IServerListener listener = ServerNet.CreateListener();

// ── 2. 콜백 등록 (Start() 호출 전에 완료해야 합니다) ─────────────────────────
//
// 콜백을 Start() 이후에 설정하면 InvalidOperationException이 발생합니다.
// → ServerLib가 레이스 컨디션 없이 콜백을 읽기 위해 시작 시점 이후 변경을 금지합니다.

// OnClientConnected: 새 클라이언트 TCP 연결이 수락(accept)된 직후 호출됩니다.
// ISession: 개별 클라이언트 세션 핸들. 송신·연결 정보·상태 전환 인터페이스를 제공합니다.
// ValueTask 반환: 동기 완료 시 ValueTask.CompletedTask(캐시드, 무할당), await 필요 시 상태머신 1개 할당.
listener.OnClientConnected = (ISession session) =>
{
    Console.WriteLine($"[연결] {session.RemoteEndPoint}  세션={session.SessionId:N}");
    // ValueTask.CompletedTask: 이미 완료된 캐시드 인스턴스를 반환 → Task.CompletedTask와 달리 ValueTask이므로 무할당.
    return ValueTask.CompletedTask;
};

// OnClientDisconnected: 클라이언트 연결이 정상/비정상 종료된 뒤 호출됩니다.
listener.OnClientDisconnected = (ISession session) =>
{
    Console.WriteLine($"[해제] {session.RemoteEndPoint}  세션={session.SessionId:N}");
    return ValueTask.CompletedTask;
};

// OnClientError: 패킷 파싱 실패(SpanReader 경계 초과) 또는 OnReceived 핸들러 예외 시 호출됩니다.
// ※ SocketException(연결 재설정 등)은 FillPipeAsync에서 조용히 처리되어 이 콜백에 도달하지 않습니다.
//    그 경우 소켓이 닫히면서 OnClientDisconnected가 호출됩니다.
listener.OnClientError = (ISession session, Exception ex) =>
{
    Console.WriteLine($"[오류] {session.RemoteEndPoint}: {ex.GetType().Name} — {ex.Message}");
    return ValueTask.CompletedTask;
};

// ── 3. 수신 콜백 (에코 핵심 로직) ────────────────────────────────────────────
//
// OnReceived: 클라이언트로부터 완전한 패킷 1개가 도착할 때마다 호출됩니다.
//
// [프레이밍 보장]
//   SocketPipelineSession이 System.IO.Pipelines의 PipeReader를 통해 TCP 스트림을
//   패킷 단위로 조립합니다. 부분 패킷이 도착하면 다음 ReadAsync 시 이어붙여 처리합니다.
//   결과적으로 이 콜백은 항상 '완전한 패킷 1개분' 데이터를 받습니다.
//
// [data 소유권]
//   ReadOnlyMemory<byte>는 내부 수신 버퍼의 슬라이스 뷰입니다.
//   이 콜백이 반환된 뒤 버퍼가 재사용될 수 있으므로, 콜백 밖에서 참조하려면 ToArray()로 복사 필요.
//   이 예제는 동기 역직렬화 후 즉시 반환하므로 버퍼 복사 없이 Span<byte> 뷰만 사용합니다.
listener.OnReceived = (ISession session, ReadOnlyMemory<byte> data) =>
{
    // data.Span: ReadOnlyMemory<byte> → ReadOnlySpan<byte> 변환(zero-copy 뷰).
    //   힙 복사 없이 동일 메모리를 Span 인터페이스로 참조합니다.
    //
    // BinaryPacketSerializer.Deserialize<EchoPacket>(ReadOnlySpan<byte>):
    //   - 앞 4바이트 헤더(PacketId 2B + BodyLength 2B)를 파싱합니다.
    //   - 이후 본문을 SpanReader(ref struct, 스택 할당)로 읽어 EchoPacket.Deserialize()를 호출합니다.
    //   - EchoPacket은 class이므로: new EchoPacket() 1회 + string ReadString() 1회 힙 할당 발생.
    //     (string은 불변이라 zero-copy 불가, UTF-8 → string 변환 시 새 인스턴스 생성이 불가피합니다)
    EchoPacket received = serializer.Deserialize<EchoPacket>(data.Span);
    Console.WriteLine($"[수신] \"{received.Message}\"  from {session.RemoteEndPoint}");

    // session.SendAsync<EchoPacket>(packet): PacketSendExtensions 확장 메서드.
    //   ① ArrayPool<byte>.Shared.Rent(): 직렬화 버퍼를 TLS 슬롯 → 공유 풀 순서로 탐색해 대여
    //      → new byte[] 없이 재사용 버퍼 획득(무할당)
    //   ② BinaryPacketSerializer.Serialize(): 대여 버퍼에 헤더(4B) + 본문 기록 (Zero-allocation)
    //   ③ ISession.SendAsync(ReadOnlyMemory<byte>): _socket.SendAsync()로 TCP 커널 송신 버퍼에 직접 기록.
    //      → Non-blocking: 커널 버퍼 여유 시 즉시 반환, 버퍼 포화 시만 비동기 대기(TCP 흐름 제어).
    //      (수신 경로는 PipeReader를 사용하지만 송신 경로는 소켓 직접 호출입니다)
    //   ④ 동기 완료 시 버퍼 즉시 반납(무할당), 비동기 완료 시 상태머신 1개 할당 후 완료 시 반납
    return session.SendAsync(received);
};

// ── 4. 서버 시작 ──────────────────────────────────────────────────────────────
//
// listener.Start(port, bindAddress):
//   - 지정 포트에 소켓을 바인딩하고 listen(backlog)을 호출합니다.
//   - TCP accept 루프를 백그라운드 Task로 시작합니다 (Non-blocking: 이 줄 이후 즉시 다음 코드 실행).
//   - 이 줄 이전에 모든 콜백·옵션 설정이 완료되어야 합니다.
// IPAddress.Loopback: 루프백(127.0.0.1) 전용 바인딩 → 외부 네트워크에 노출되지 않습니다.
//   Start(9000) 단일 인수 오버로드는 IPAddress.Any(전체 인터페이스)에 바인딩하므로
//   원격 서버에서 실행 시 인증 없는 서비스가 외부에 노출될 수 있습니다.
//   실제 배포 시에는 바인딩 주소를 명시적으로 지정하세요.
// MaxConnections·IdleTimeout 미설정 시: 연결 수 무제한·유휴 스윕 비활성화(DoS 취약).
//   실제 서비스라면 listener.MaxConnections = 100; listener.IdleTimeout = TimeSpan.FromSeconds(30);
listener.Start(9000, IPAddress.Loopback);
Console.WriteLine("에코 서버 시작 — 포트 9000  [종료: 아무 키나 누르세요]");
Console.WriteLine("───────────────────────────────────────");

// 메인 스레드를 블로킹해 서버를 유지합니다. 키 입력 시 종료 흐름으로 진입합니다.
Console.ReadKey(intercept: true);
Console.WriteLine();

// ── 5. 서버 종료 ──────────────────────────────────────────────────────────────
//
// listener.Stop():
//   - 새 연결 수락을 중단합니다(_cts.Cancel() + 리슨 소켓 Dispose).
//   - 활성 세션을 DisposeAsync().GetResult()로 순차 정리합니다(동기 블로킹).
//   - AcceptLoopAsync Task는 취소 후 스스로 종료되나, Stop() 반환 시점에 완료가 보장되지는 않습니다.
listener.Stop();
Console.WriteLine("서버 종료.");
