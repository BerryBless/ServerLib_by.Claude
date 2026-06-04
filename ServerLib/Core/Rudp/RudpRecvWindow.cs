namespace ServerLib.Core.Rudp;

/// <summary>슬라이딩 수신 윈도우: 순서 재조립 + 중복 제거</summary>
/// <remarks>
/// <b>[Thread Safety — OnReceive]:</b> Not thread-safe. <see cref="OnReceive"/>는
/// 단일 수신 스레드(ReceiveLoopAsync)에서만 호출해야 합니다.
/// <c>_received[]</c> 배열은 원자적 접근이 보장되지 않으므로 동시 호출 시 데이터 레이스가 발생합니다.
/// <c>_expectedSeq</c>는 <see cref="Volatile"/>/<see cref="Interlocked"/>로 보호되어
/// 다른 스레드에서 읽기(<see cref="ExpectedSeq"/>)는 안전합니다.
/// </remarks>
public sealed class RudpRecvWindow
{
    private const int WindowSize = 64;

    private uint _expectedSeq;
    // bool[64]: 고정 크기 배열을 생성 시 1회만 할당하고 모듈로 인덱싱으로 영구 재사용 → 수신마다 할당이 없는 링버퍼(GC 무압력).
    private readonly bool[] _received = new bool[WindowSize];

    // Volatile.Read: 다른 스레드가 _expectedSeq를 읽을 때 캐시된 옛 값이 아닌 최신값을 보도록 acquire 배리어 적용(가시성만 보장).
    public uint ExpectedSeq => Volatile.Read(ref _expectedSeq);

    /// <summary>수신된 시퀀스 번호를 처리하고 순서대로 전달 가능한지 반환합니다.</summary>
    /// <remarks><b>[Thread Safety:]</b> Not thread-safe. 단일 수신 스레드 전용.</remarks>
    public bool OnReceive(uint seq, out uint advancedTo)
    {
        advancedTo = _expectedSeq;
        var diff = (int)(seq - _expectedSeq);

        if (diff < 0 || diff >= WindowSize) return false;  // 중복 or 윈도우 초과

        _received[seq % WindowSize] = true;  // 모듈로 인덱싱: 고정 배열을 링버퍼로 재사용 — seq가 커져도 새 메모리 할당 없음

        // 연속된 수신 확인 후 윈도우 슬라이드
        while (_received[_expectedSeq % WindowSize])
        {
            _received[_expectedSeq % WindowSize] = false;
            // Interlocked.Increment: 단일 수신 스레드가 쓰지만, ExpectedSeq를 읽는 다른 스레드에 원자적 갱신을 release 배리어와 함께 게시
            Interlocked.Increment(ref _expectedSeq);
        }

        advancedTo = _expectedSeq;
        return true;
    }

    // ACK 비트맵 생성 (32비트, 최근 32개 수신 여부)
    public uint BuildAckBitmap()
    {
        uint bitmap = 0;
        for (int i = 0; i < 32; i++)
        {
            if (_received[(_expectedSeq + i) % WindowSize])
                bitmap |= 1u << i;
        }
        return bitmap;
    }
}
