namespace ServerLib.Core.Rudp;

// 슬라이딩 수신 윈도우: 순서 재조립 + 중복 제거
// 상태는 Interlocked로 관리하여 lock 없이 동작
public sealed class RudpRecvWindow
{
    private const int WindowSize = 64;

    private uint _expectedSeq;
    private readonly bool[] _received = new bool[WindowSize];

    public uint ExpectedSeq => Volatile.Read(ref _expectedSeq);

    // 수신된 시퀀스 번호 처리, 순서대로 처리 가능한지 반환
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
