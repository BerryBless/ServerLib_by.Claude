using System.IO;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace CounterServer;

/// <summary>
/// 수신 프레임을 검증·라우팅해 <see cref="CounterState"/>를 갱신하거나 현재 값을 응답하는 서버 측 핸들러입니다.
/// <c>CounterServer/Program.cs</c>의 <c>listener.OnReceived</c>에 그대로 연결되며, E2E 테스트도 같은 인스턴스를 사용합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Context:</b> ServerLib의 세션별 수신 루프(IO 스레드)에서 직접 호출됩니다.
/// <b>같은 세션</b>의 패킷은 순차 <c>await</c>로 처리되지만, <b>서로 다른 세션</b>은 동시에 이 메서드에 진입할 수 있습니다
/// — 그것이 이 예제가 만들려는 경합입니다. 내부에서 동기 블로킹(DB·File IO)을 하면 해당 세션의 수신 루프가 정지합니다.
/// </description></item>
/// <item><description>
/// <b>Thread Safety:</b> Thread-safe. 핸들러 자신은 불변(생성 후 <see cref="State"/> 참조만 보유)이고,
/// 모든 가변 상태 접근은 <see cref="CounterState"/>의 <c>Interlocked</c> 경로로 위임됩니다.
/// </description></item>
/// <item><description>
/// <b>Blocking / Memory — 증감 경로(Id 3·4):</b> <b>동기 갱신 후 이미 완료된 <see cref="ValueTask"/>를 반환</b>합니다
/// (<c>ValueTask.CompletedTask</c> = 캐시드 인스턴스). 대기가 없으므로 <b>무할당(Zero-allocation)</b>이며
/// 패킷마다 상태머신·로그 문자열·작업 Task를 만들지 않습니다.
/// </description></item>
/// <item><description>
/// <b>Blocking / Memory — 조회 응답 경로(Id 18):</b> <c>PacketSendExtensions.SendAsync</c>로 위임합니다.
/// 이 경로는 <see cref="System.Buffers.ArrayPool{T}"/>에서 송신 버퍼를 <b>대여</b>합니다.
/// <b>풀에 재사용 가능한 버퍼가 있고</b> 커널 송신 버퍼에 여유가 있어 <b>송신이 동기 완료</b>하면
/// 추가 힙 할당 없이 대여 버퍼를 즉시 반납하고 완료된 ValueTask를 반환할 수 있습니다.
/// 다만 <c>ArrayPool.Rent</c>는 <b>해당 버킷이 비어 있으면 새 배열을 할당</b>하므로 동기 경로라도 무할당이 <b>보장되지는 않습니다</b>.
/// 커널 버퍼 포화로 <b>송신이 미완료</b>면 비동기 완료로 전환되어 <c>AwaitAndReturnAsync</c> 상태머신과
/// 하위 <c>SocketPipelineSession.SendAsync</c>/<c>SendAllAsync</c>의 비동기 대기 경로에서 <b>추가 할당이 발생할 수 있습니다</b>.
/// 이 경로 전체의 할당 개수는 단정하지 않습니다. 어느 경로든 <c>finally</c>에서 대여 버퍼 반납은 보장됩니다
/// (<c>PacketSendExtensions.cs:78-92</c>의 <c>CompleteAsync</c>/<c>AwaitAndReturnAsync</c> 분기).
/// </description></item>
/// <item><description>
/// <b>Memory Policy — <c>data</c> 소유권:</b> <c>ReadOnlyMemory&lt;byte&gt;</c>는 세션 내부 수신 버퍼의 슬라이스 뷰이며
/// <b>이 메서드가 반환되면 재사용될 수 있습니다</b>. 따라서 버퍼를 보관하지 않고 동기 구간에서 검증·역직렬화만 수행하며,
/// 밖으로 내보내는 것은 값 타입(<see cref="long"/>)뿐입니다.
/// </description></item>
/// <item><description>
/// <b>실패 정책:</b> 검증 실패·미지 패킷 ID·조회 응답 송신 실패는 예외로 표면화하며, ServerLib 수신 루프가 이를
/// 패킷 단위로 격리해 <b>해당 세션만</b> 종료합니다(<c>SocketPipelineSession</c> 디스패치 루프의 catch → <c>OnReceiveError</c> →
/// <c>OnClientDisconnected</c>). 다른 세션과 카운터 값에는 영향이 없습니다. 자동 재송신은 하지 않습니다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class CounterHandler
{
    /// <summary>미지 패킷 ID를 만났을 때 예외 메시지에 쓰이는 접두사입니다. 테스트가 원인을 식별하는 데 사용합니다.</summary>
    public const string UnknownPacketMessagePrefix = "알 수 없는 패킷 ID";

    /// <summary>본문 길이 검증에 실패했을 때 예외 메시지에 쓰이는 접두사입니다.</summary>
    public const string InvalidBodyMessagePrefix = "패킷 본문 길이가 올바르지 않습니다";

    /// <summary>이 핸들러가 갱신·조회하는 공유 카운터입니다. 모든 세션이 <b>같은 인스턴스</b>를 봅니다.</summary>
    public CounterState State { get; }

    /// <summary>지정한 공유 카운터를 갱신하는 핸들러를 만듭니다.</summary>
    /// <param name="state">모든 세션이 공유할 카운터 상태입니다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks><b>[Blocking]</b> Non-blocking. <b>[Memory]</b> 핸들러 인스턴스 1회 할당.</remarks>
    public CounterHandler(CounterState state)
        => State = state ?? throw new ArgumentNullException(nameof(state));

    /// <summary>
    /// 완전한 패킷 1개를 검증하고, 증감 명령이면 카운터를 갱신하고 조회 요청이면 현재 값을 응답합니다.
    /// </summary>
    /// <param name="session">패킷을 보낸 세션입니다. 조회 응답의 송신 대상이기도 합니다.</param>
    /// <param name="data">헤더(4B)를 포함한 완전한 패킷 1개입니다. <b>이 메서드가 반환되면 무효</b>가 되는 뷰입니다.</param>
    /// <returns>증감이면 이미 완료된 <see cref="ValueTask"/>, 조회면 응답 송신 완료 시 완료되는 <see cref="ValueTask"/>입니다.</returns>
    /// <exception cref="InvalidDataException">
    /// 헤더 파싱 실패, 선언 본문 길이와 실제 길이 불일치, 패킷 ID별 기대 본문 길이 불일치,
    /// 또는 알 수 없는 패킷 ID일 때 발생합니다. 카운터를 <b>건드리기 전에</b> 던지므로 상태는 불변입니다.
    /// </exception>
    /// <remarks>
    /// <b>[검증을 직접 하는 이유]</b> 이 핸들러가 처리하는 세 ID(3·4·18)는 본문이 모두 0B이므로 <b>역직렬화 자체를 하지 않고</b>
    /// 길이·ID 검증만으로 프레임을 판별합니다. 본문이 있는 패킷을 추가할 때도 이 검증이 <b>선행되어야</b> 합니다 —
    /// <c>BinaryPacketSerializer.Deserialize&lt;T&gt;</c>는 헤더를 <b>건너뛰기만</b> 할 뿐 타입 ID 일치나 본문 전체 소비를
    /// 검사하지 않아, 길이·ID를 먼저 확인하지 않으면 "Id=3 자리에 다른 길이의 본문"같은 프레임이 조용히 통과합니다.
    /// <br/><b>[Thread Safety]</b> Thread-safe. <b>[Blocking]</b> Non-blocking(위 클래스 <c>remarks</c>의 경로별 완료 특성 참조).
    /// </remarks>
    public ValueTask HandleAsync(ISession session, ReadOnlyMemory<byte> data)
    {
        // data.Span: ReadOnlyMemory<byte> → ReadOnlySpan<byte> 변환(zero-copy 뷰). 힙 복사 없이 같은 메모리를 참조한다.
        ReadOnlySpan<byte> frame = data.Span;

        // TryParseHeader: 앞 4B를 BinaryPrimitives로 in-place 해석(무할당). 길이가 헤더보다 짧으면 false.
        if (!PacketPool.TryParseHeader(frame, out ushort packetId, out int bodyLength))
            throw new InvalidDataException($"{InvalidBodyMessagePrefix}: 헤더 4바이트를 파싱할 수 없습니다(수신 {frame.Length}B).");

        // 선언 길이 vs 실제 길이 교차 검증. 프레이밍 계층이 헤더의 BodyLength만큼 잘라 주지만,
        // 그 값을 그대로 신뢰하지 않고 실제 도달 길이와 일치하는지 확인해 파싱 전제를 코드에 못 박는다.
        if (frame.Length != PacketPool.HeaderSize + bodyLength)
            throw new InvalidDataException(
                $"{InvalidBodyMessagePrefix}: 선언 {bodyLength}B, 실제 {frame.Length - PacketPool.HeaderSize}B (Id={packetId}).");

        switch (packetId)
        {
            case IncrementPacket.Id:
                // 증감 명령은 본문이 정확히 0B여야 한다. 0이 아니면 프로토콜 위반 → 카운터를 건드리지 않고 즉시 실패.
                RequireBodySize(packetId, bodyLength, expected: 0);
                State.Increment();
                // ValueTask.CompletedTask: 이미 완료된 캐시드 인스턴스 → 증감 경로 전체가 무할당(상태머신 없음).
                return ValueTask.CompletedTask;

            case DecrementPacket.Id:
                RequireBodySize(packetId, bodyLength, expected: 0);
                State.Decrement();
                return ValueTask.CompletedTask;

            case CounterQueryPacket.Id:
                RequireBodySize(packetId, bodyLength, expected: CounterQueryPacket.BodySize);
                return SendCurrentValueAsync(session);

            default:
                // 미지 ID는 드롭하지 않고 오류로 처리한다(해당 세션만 종료). 조용한 드롭은 프로토콜 불일치를
                // 무증상으로 만들어 학습 예제의 진단성을 떨어뜨린다.
                throw new InvalidDataException($"{UnknownPacketMessagePrefix}: {packetId}");
        }
    }

    /// <summary>현재 카운터 스냅샷을 <see cref="CounterValuePacket"/>(Id=19)으로 회신합니다.</summary>
    /// <param name="session">응답 대상 세션입니다.</param>
    /// <returns>송신 완료 시 완료되는 <see cref="ValueTask"/>입니다.</returns>
    /// <remarks>
    /// <b>[Blocking / Memory]</b> 응답 버퍼를 <see cref="System.Buffers.ArrayPool{T}"/>에서 대여합니다. 풀에 재사용 버퍼가 있고
    /// 송신이 동기 완료하면 추가 할당 없이 즉시 반환할 수 있으나, 풀 버킷이 비었으면 <c>Rent</c>가 새 배열을 할당하고,
    /// 송신이 미완료(비동기 완료)면 상태머신·하위 송신 경로에서 추가 할당이 발생할 수 있습니다. 이 경로의 총 할당 개수는 단정하지 않습니다
    /// (<c>PacketSendExtensions.cs:78-92</c>). 대여 버퍼 반납은 동기·비동기 어느 경로든 보장됩니다.
    /// <br/><b>[두 값의 비원자성]</b> 아래 두 번의 <c>Interlocked</c> 읽기 <b>사이</b>에 다른 세션이 갱신할 수 있으므로,
    /// 이 응답의 <c>Value</c>·<c>AppliedOps</c>는 진행 중에는 서로 다른 시점의 스냅샷일 수 있습니다.
    /// 두 값을 함께 단언하는 것은 모든 증감이 끝난 정지 상태 조회에서만 유효합니다.
    /// <br/><b>[송신 실패]</b> <see cref="System.Net.Sockets.SocketException"/>이 나도 <b>"상대가 종료했다"고 단정하지 않습니다</b> —
    /// ServerLib은 송신 시한 만료도 <c>SocketError.TimedOut</c>의 <c>SocketException</c>으로 변환하기 때문입니다.
    /// 예외는 그대로 전파해 수신 루프가 로그(<c>OnClientError</c>) 후 해당 세션만 종료하게 하며, 자동 재송신은 하지 않습니다
    /// (재송신하면 중복 적용 여부를 알 수 없게 됩니다).
    /// </remarks>
    private ValueTask SendCurrentValueAsync(ISession session)
    {
        var response = new CounterValuePacket
        {
            Value = State.Value,
            AppliedOps = State.AppliedOps,
        };

        // session.SendAsync<CounterValuePacket>(packet): PacketSendExtensions 확장 메서드.
        //   ① ArrayPool<byte>.Shared.Rent(4+16): TLS 슬롯 → 공유 풀 순서로 버퍼를 대여. 해당 버킷에 재사용 버퍼가 있으면
        //      new byte[]를 피하지만, 버킷이 비어 있으면 Rent가 새 배열을 할당한다(요청보다 큰 크기를 줄 수도 있다) → 무할당 미보장.
        //   ② Serialize: 대여 버퍼에 헤더(4B)+본문(16B)을 SpanWriter(ref struct, 스택)로 기록 — 이 단계 자체는 힙 할당이 없다.
        //   ③ ISession.SendAsync: _socket.SendAsync()로 커널 송신 버퍼에 직접 기록(Non-blocking, 포화 시에만 비동기 대기).
        //   ④ 동기 완료 시 버퍼 즉시 반납, 비동기 완료 시 AwaitAndReturnAsync 상태머신과 하위 송신 경로에서 추가 할당이 생길 수 있고
        //      어느 경로든 finally에서 반납이 보장된다. 이 경로의 총 할당 개수는 단정하지 않는다.
        // struct 패킷이므로 T가 값 타입으로 특수화되어 박싱이 없다.
        return session.SendAsync(response);
    }

    /// <summary>패킷 ID별 기대 본문 길이를 확인하고, 다르면 <see cref="InvalidDataException"/>을 던집니다.</summary>
    /// <param name="packetId">검증 대상 패킷 ID입니다.</param>
    /// <param name="actual">헤더가 선언한 본문 길이입니다.</param>
    /// <param name="expected">해당 ID의 고정 본문 길이입니다.</param>
    /// <exception cref="InvalidDataException">길이가 일치하지 않을 때.</exception>
    /// <remarks><b>[Memory]</b> 일치하는 정상 경로는 무할당(예외 메시지 문자열은 실패 시에만 생성). <b>[Blocking]</b> Non-blocking.</remarks>
    private static void RequireBodySize(ushort packetId, int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidDataException($"{InvalidBodyMessagePrefix}: Id={packetId}는 {expected}B여야 하는데 {actual}B입니다.");
    }
}
