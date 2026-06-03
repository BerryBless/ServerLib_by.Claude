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
    private readonly bool[] _received = new bool[WindowSize];

    public uint ExpectedSeq => Volatile.Read(ref _expectedSeq);

    /// <summary>수신된 시퀀스 번호를 처리하고 순서대로 전달 가능한지 반환합니다.</summary>
    /// <remarks><b>[Thread Safety:]</b> Not thread-safe. 단일 수신 스레드 전용.</remarks>
    public bool OnReceive(uint seq, out uint advancedTo)
    {
        advancedTo = _expectedSeq;
        var diff = (int)(seq - _expectedSeq);

        if (diff < 0 || diff >= WindowSize) return false;  // 중복 or 윈도우 초과

        _received[seq % WindowSize] = true;

        // 연속된 수신 확인 후 윈도우 슬라이드
        while (_received[_expectedSeq % WindowSize])
        {
            _received[_expectedSeq % WindowSize] = false;
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
