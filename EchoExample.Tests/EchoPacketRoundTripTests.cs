using System.Text;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using Xunit;

namespace EchoExample.Tests;

/// <summary>
/// <see cref="EchoPacket"/>의 직렬화·역직렬화 계약을 소켓 없이 단위 검증합니다.
/// <see cref="BinaryPacketSerializer"/> 레이어만 테스트합니다.
/// </summary>
/// <remarks>
/// 이 테스트 클래스가 보증하는 불변식:
/// <list type="number">
/// <item><description>Serialize → Deserialize 후 <see cref="EchoPacket.Message"/> 필드가 원본과 동일하게 보존됨(Round-trip 보존).</description></item>
/// <item><description><see cref="IPacket.GetBodySize"/>가 실제 직렬화 본문 길이와 일치함 — 이 값이 틀리면 <see cref="PacketSendExtensions.SendAsync{T}(ServerLib.Interface.ISession, T, System.Threading.CancellationToken)"/>의 ArrayPool 대여 크기가 잘못되어 <see cref="ArgumentException"/>이 발생합니다.</description></item>
/// <item><description><see cref="EchoPacket.Id"/> == 1 — 라우팅 키가 변경되면 서버-클라 프로토콜 호환이 깨집니다.</description></item>
/// </list>
/// </remarks>
public class EchoPacketRoundTripTests
{
    // BinaryPacketSerializer: 무상태(stateless) 클래스 — SpanWriter/SpanReader(ref struct, 스택 전용)를 인수로만 사용하고
    // 내부 필드를 변경하지 않으므로 여러 스레드·테스트 메서드가 동일 인스턴스를 공유해도 Thread-safe.
    private static readonly BinaryPacketSerializer Serializer = new();

    /// <summary>
    /// 정확한 버퍼 크기(헤더 + 본문)를 계산해 할당하는 헬퍼입니다.
    /// </summary>
    /// <remarks>
    /// PacketPool.HeaderSize = 4(PacketId 2B + BodyLength 2B).
    /// GetBodySize() 오류가 있으면 Serialize 시 ArgumentException → 버퍼 크기 계약 테스트를 간접 강제합니다.
    /// </remarks>
    private static byte[] AllocateBuffer(IPacket packet)
        => new byte[PacketPool.HeaderSize + packet.GetBodySize()];

    // ── Round-trip 보존 ───────────────────────────────────────────────────────

    /// <summary>
    /// Serialize → Deserialize 후 <see cref="EchoPacket.Message"/>가 원본과 동일함을 검증합니다.
    /// </summary>
    /// <remarks>
    /// 다루는 경계 케이스:
    /// <list type="bullet">
    /// <item><description>빈 문자열: BodySize = 2(프리픽스 ushort만), string.Empty 복원.</description></item>
    /// <item><description>ASCII: 1바이트/문자, GetByteCount == Length.</description></item>
    /// <item><description>한글(CJK): UTF-8 3바이트/문자, GetByteCount > Length.</description></item>
    /// <item><description>이모지: 4바이트 서로게이트 쌍, GetByteCount &gt; Length (C# string은 2 char).</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("한글 메시지")]
    [InlineData("🎯 emoji 🎯")]
    public void EchoPacket_RoundTrip_PreservesMessage(string message)
    {
        // Arrange
        var packet = new EchoPacket { Message = message };
        // AllocateBuffer: HeaderSize + GetBodySize() 크기의 정확한 버퍼. 크기 부족 시 Serialize가 throw하므로
        // 이 단계에서 GetBodySize 오류도 조기 검출됩니다.
        var buf = AllocateBuffer(packet);

        // Act
        Serializer.Serialize(packet, buf);
        // Deserialize<EchoPacket>: new EchoPacket() 1회 힙 할당 + string ReadString() 1회 힙 할당(불가피).
        // buf는 헤더(4B) + 본문 전체 — Deserialize는 내부에서 [HeaderSize..] 슬라이스만 SpanReader에 전달합니다.
        var result = Serializer.Deserialize<EchoPacket>(buf);

        // Assert
        Assert.Equal(message, result.Message);
    }

    // ── GetBodySize 계약 ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="EchoPacket.GetBodySize"/>가 '2(길이 프리픽스 ushort) + UTF-8 바이트 수'임을 검증합니다.
    /// </summary>
    /// <remarks>
    /// <see cref="PacketSendExtensions.SendAsync{T}(ServerLib.Interface.ISession, T, System.Threading.CancellationToken)"/>는
    /// ArrayPool에서 HeaderSize + GetBodySize() 바이트를 대여합니다.
    /// GetBodySize()가 실제보다 작으면 Serialize 시 <see cref="ArgumentException"/>,
    /// 실제보다 크면 풀 버퍼 낭비 — 정확성이 성능·안전 모두에 영향합니다.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("한글")]
    public void EchoPacket_GetBodySize_IsTwoPlusUtf8ByteCount(string message)
    {
        // Arrange
        var packet = new EchoPacket { Message = message };

        // Act
        int actual = packet.GetBodySize();

        // Assert: SpanWriter.WriteString은 ushort(2B) 길이 프리픽스 + UTF-8 바이트열로 인코딩합니다.
        int expected = 2 + Encoding.UTF8.GetByteCount(message);
        Assert.Equal(expected, actual);
    }

    // ── PacketId 계약 ─────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="EchoPacket.Id"/> 상수와 인스턴스 <see cref="EchoPacket.PacketId"/> 모두 1임을 검증합니다.
    /// </summary>
    /// <remarks>
    /// PacketId는 수신 측이 역직렬화 타입을 결정하는 라우팅 키입니다.
    /// 서버가 Id=1 패킷을 수신할 때 <see cref="EchoPacket"/>으로 역직렬화한다는
    /// 서버-클라 계약이 이 상수에 의존합니다. 값이 변경되면 프로토콜 호환이 깨집니다.
    /// </remarks>
    [Fact]
    public void EchoPacket_PacketId_IsOne()
    {
        Assert.Equal((ushort)1, EchoPacket.Id);           // 정적 상수 — 컴파일 타임에 결정
        Assert.Equal((ushort)1, new EchoPacket().PacketId); // 인스턴스 프로퍼티 — 상수와 일치해야 함
    }
}
