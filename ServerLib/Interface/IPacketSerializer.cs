namespace ServerLib.Interface;

public interface IPacketSerializer
{
    // 직렬화: 목적지 버퍼에 직접 기록, 기록한 바이트 수 반환 (Zero-copy)
    int Serialize<T>(T packet, Span<byte> destination);

    // 역직렬화: 소스 버퍼에서 직접 읽음 (복사 없음)
    T Deserialize<T>(ReadOnlySpan<byte> source);

    // 패킷 헤더만 파싱하여 전체 길이 반환 (partial read 감지용)
    bool TryReadPacketLength(ReadOnlySpan<byte> header, out int totalLength);
}
