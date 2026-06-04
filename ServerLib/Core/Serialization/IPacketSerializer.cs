namespace ServerLib.Core.Serialization;

/// <summary>
/// 패킷 객체와 바이트 버퍼 사이의 직렬화/역직렬화를 정의하는 인터페이스입니다.
/// 모든 연산은 <see cref="Span{T}"/>/<see cref="ReadOnlySpan{T}"/> 기반으로 동작하여
/// 힙 할당 없이 Zero-copy 직렬화를 보장합니다.
/// </summary>
/// <remarks>
/// <b>[패킷 구조]</b> 헤더 4바이트 [PacketId(2B) | BodyLength(2B)] + 본문 N바이트 (LittleEndian)
/// <list type="bullet">
/// <item><description>직렬화 결과를 새 <see cref="byte"/>[] 배열로 반환하지 않습니다. 호출자가 대여한 버퍼에 직접 기록합니다.</description></item>
/// <item><description>역직렬화 시 소스 버퍼를 복사하지 않습니다. 버퍼를 직접 파싱합니다.</description></item>
/// <item><description><see cref="TryReadPacketLength"/>는 부분 수신(partial read) 감지에 사용되어 불완전한 패킷 처리를 방지합니다.</description></item>
/// </list>
/// </remarks>
public interface IPacketSerializer
{
    /// <summary>
    /// 패킷 객체를 직렬화하여 <paramref name="destination"/> 버퍼에 직접 기록합니다.
    /// </summary>
    /// <typeparam name="T">직렬화할 패킷의 타입입니다. <see cref="IPacket"/>을 구현해야 합니다.</typeparam>
    /// <param name="packet">직렬화할 패킷 인스턴스입니다.</param>
    /// <param name="destination">직렬화 결과가 기록될 대상 버퍼입니다. 충분한 크기를 사전에 확보해야 합니다.</param>
    /// <returns>실제로 기록된 바이트 수(헤더 + 본문)입니다.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/>의 크기가 직렬화 결과보다 작을 때 발생합니다.</exception>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 내부 임시 버퍼를 생성하지 않으며,
    /// <paramref name="destination"/>에 직접 쓰기만 수행합니다.
    /// 버퍼는 <see cref="System.Buffers.ArrayPool{T}.Shared"/>로 대여하여 전달하는 것을 권장합니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Thread-safe. 공유 상태를 변경하지 않습니다.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 동기 즉시 반환됩니다.
    /// </remarks>
    int Serialize<T>(T packet, Span<byte> destination) where T : IPacket;

    /// <summary>
    /// <paramref name="source"/> 버퍼를 역직렬화하여 패킷 객체를 반환합니다.
    /// <paramref name="source"/>는 헤더를 포함한 전체 패킷이어야 합니다.
    /// </summary>
    /// <typeparam name="T">역직렬화 대상 패킷의 타입입니다. <see cref="IPacket"/>을 구현하고 기본 생성자가 있어야 합니다.</typeparam>
    /// <param name="source">헤더 + 본문으로 구성된 전체 패킷 버퍼입니다.</param>
    /// <returns>역직렬화된 패킷 인스턴스입니다.</returns>
    /// <exception cref="InvalidOperationException">버퍼가 손상되었거나 <typeparamref name="T"/>와 구조가 맞지 않을 때 발생합니다.</exception>
    /// <remarks>
    /// <b>[Memory Policy:]</b> <paramref name="source"/>의 소유권은 호출자에게 있습니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> T가 class인 경우 <c>new T()</c>로 1회 힙 할당이 발생합니다.
    /// T를 struct로 정의하면 Zero-allocation이 가능합니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Thread-safe.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    T Deserialize<T>(ReadOnlySpan<byte> source) where T : IPacket, new();

    /// <summary>
    /// 패킷 헤더를 파싱하여 패킷 전체 길이를 읽습니다.
    /// 부분 수신(partial read) 상황에서 완전한 패킷이 도착했는지 판단하는 데 사용합니다.
    /// </summary>
    /// <param name="header">파싱할 패킷 헤더 바이트입니다. 최소 헤더 크기 이상이어야 합니다.</param>
    /// <param name="totalLength">파싱 성공 시 헤더를 포함한 패킷 전체 바이트 수입니다. 실패 시 0입니다.</param>
    /// <returns>헤더 파싱 성공 여부입니다. <see langword="false"/>이면 <paramref name="header"/>가 불완전한 것입니다.</returns>
    /// <remarks>
    /// <b>[Usage Pattern:]</b> PipeReader 수신 루프에서 다음 패턴으로 사용합니다.
    /// <code>
    /// if (!serializer.TryReadPacketLength(buffer.FirstSpan, out int total)) {
    ///     reader.AdvanceTo(buffer.Start); // 더 많은 데이터 대기
    ///     continue;
    /// }
    /// </code>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 스택 기반 Span 파싱만 수행합니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Thread-safe.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    bool TryReadPacketLength(ReadOnlySpan<byte> header, out int totalLength);
}
