// =============================================================================
// EchoClient — ServerLib IClientConnection 사용 예제
// =============================================================================
// 동작: 127.0.0.1:9000 에코 서버에 연결 후 콘솔 입력을 EchoPacket으로 전송합니다.
//       서버 응답(에코)을 받으면 "[서버 응답] <메시지>" 형식으로 출력합니다.
//
// 실행법:
//   dotnet run --project EchoClient
//   종료: "exit" 입력 후 Enter.
//   (먼저 EchoServer를 실행해 두어야 합니다)
//
// ServerLib 핵심 흐름:
//   ServerNet.CreateClient()              — 클라이언트 팩토리
//   client.OnReceived = ...              — 수신 콜백 등록 (ConnectAsync() 전에)
//   await client.ConnectAsync(host, port) — TCP 연결 + 수신 파이프라인 시작
//   await client.SendAsync<EchoPacket>() — 직렬화+풀버퍼+송신 한 번에 처리
//   await using                          — DisposeAsync()로 소켓 정리 보장
// =============================================================================

using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

// BinaryPacketSerializer: 무상태(stateless) → Thread-safe. 단일 인스턴스를 공유합니다.
// Deserialize<T> 내부에서 SpanReader(ref struct, 스택 할당)를 사용하므로 역직렬화기 자체는 Zero-allocation.
var serializer = new BinaryPacketSerializer();

// ── 1. 클라이언트 생성 ────────────────────────────────────────────────────────
//
// ServerNet.CreateClient():
//   내부 구현체인 SocketPipelineClient(internal 클래스)를 생성하고
//   IClientConnection 인터페이스로 반환합니다.
//   → 구체 타입이 숨겨져 있어 소비자는 인터페이스만 다루면 됩니다(캡슐화).
//   → 즉시 반환(Non-blocking). ConnectAsync() 전까지 소켓이 생성되지 않습니다.
//
// await using:
//   IClientConnection은 IAsyncDisposable을 구현합니다.
//   블록을 벗어날 때 DisposeAsync()가 자동 호출되어 소켓·수신 파이프라인 자원을 정리합니다.
//   명시적 Disconnect() 없이도 안전한 종료가 보장됩니다.
await using IClientConnection client = ServerNet.CreateClient();

// ── 2. 수신 콜백 등록 (ConnectAsync() 전에 설정) ─────────────────────────────

// OnReceived: 서버에서 완전한 패킷 1개가 도착할 때마다 IO 스레드에서 호출됩니다.
//
// [data 소유권]
//   ReadOnlyMemory<byte>는 내부 수신 버퍼의 슬라이스 뷰입니다.
//   콜백이 반환된 뒤 버퍼가 재사용될 수 있으므로, 콜백 밖에서 참조하려면 ToArray()로 복사 필요.
//   이 예제는 동기 역직렬화 후 즉시 반환하므로 복사 없이 Span<byte> 뷰를 사용합니다.
//
// [호출 스레드]
//   IO 전용 스레드에서 호출됩니다. 무거운 동기 작업(파일 IO, DB 등)을 수행하면 수신 루프가 지연됩니다.
client.OnReceived = (ReadOnlyMemory<byte> data) =>
{
    // data.Span: zero-copy 뷰(힙 복사 없음). Deserialize가 동기 완료하므로 콜백 반환 전에 파싱이 끝납니다.
    //
    // BinaryPacketSerializer.Deserialize<EchoPacket>(ReadOnlySpan<byte>):
    //   - 헤더(PacketId 2B + BodyLength 2B)를 검증·파싱합니다.
    //   - 본문을 SpanReader(ref struct, 스택 할당, 무할당)로 읽습니다.
    //   - EchoPacket.Deserialize(): string ReadString() → UTF-8 디코딩 + 새 string 인스턴스 생성(1회 할당).
    EchoPacket pkt = serializer.Deserialize<EchoPacket>(data.Span);
    Console.WriteLine($"[서버 응답] {pkt.Message}");

    // ValueTask.CompletedTask: 동기 완료를 나타내는 캐시드 인스턴스(무할당).
    return ValueTask.CompletedTask;
};

// OnDisconnected: 서버가 연결을 끊었거나 네트워크 오류로 연결이 종료될 때 호출됩니다.
client.OnDisconnected = () =>
{
    Console.WriteLine("[서버 연결 해제]");
    return ValueTask.CompletedTask;
};

// ── 3. 서버 연결 ──────────────────────────────────────────────────────────────
//
// ConnectAsync(host, port):
//   ① TCP 3-way handshake 수행 (비동기 대기: 완료 전까지 스레드 블로킹 없음)
//   ② 소켓과 System.IO.Pipelines(Pipe) 내부 자원을 초기화합니다.
//   ③ 수신 루프 Task를 백그라운드에서 시작합니다.
//   완료 후 client.IsConnected == true이고, SendAsync 호출이 가능합니다.
await client.ConnectAsync("127.0.0.1", 9000);
Console.WriteLine("서버에 연결되었습니다. 메시지를 입력하세요 (종료: exit)");
Console.WriteLine("───────────────────────────────────────");

// ── 4. 인터랙티브 입력 루프 ───────────────────────────────────────────────────
while (true)
{
    string? line = Console.ReadLine();

    // null: Ctrl+Z(EOF) 입력 시 종료
    if (line is null || line.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (line.Length == 0)
        continue;

    // client.SendAsync<EchoPacket>(packet): PacketSendExtensions 확장 메서드.
    //   ① ArrayPool<byte>.Shared.Rent(): TLS 슬롯 → 공유 풀 순서로 버퍼 탐색·대여 (무할당)
    //   ② BinaryPacketSerializer.Serialize(): 대여 버퍼에 헤더(4B) + 본문 기록 (Zero-allocation)
    //   ③ IClientConnection.SendAsync(ReadOnlyMemory<byte>): _socket.SendAsync()로 TCP 커널 송신 버퍼에 직접 기록.
    //      → Non-blocking: 커널 버퍼 여유 시 즉시 반환, 버퍼 포화 시만 비동기 대기(TCP 흐름 제어).
    //      (수신 경로는 PipeReader를 사용하지만 송신 경로는 소켓 직접 호출입니다)
    //   ④ 동기 완료 시 버퍼 즉시 반납(무할당), 비동기 완료 시 상태머신 1개 할당 후 완료 시 반납
    await client.SendAsync(new EchoPacket { Message = line });
}

// ── 5. 연결 종료 ──────────────────────────────────────────────────────────────
//
// await using 블록 종료 시 client.DisposeAsync()가 자동 호출됩니다.
//   → CTS 취소(_cts.CancelAsync()) + 소켓 폐기(_socket.Dispose()) → 수신 루프 종료 신호.
//   FillPipeAsync/ReadPipeAsync Task의 완료를 명시적으로 await하지 않습니다.
//   소켓 폐기 직후 루프가 신속히 종료되지만, DisposeAsync 반환 시점에 완료 보장은 없습니다.
Console.WriteLine("클라이언트 종료.");
