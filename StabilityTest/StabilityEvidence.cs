namespace StabilityTest;

/// <summary>폭주 실행 종료 후 수집한 측정 증거. <see cref="StabilityReport.Evaluate"/>의 입력입니다.</summary>
public sealed class StabilityEvidence
{
    public bool Crashed { get; set; }          // 실행 중 child가 예기치 않게 종료됨
    public int ExitCode { get; set; }          // graceful 종료 코드
    public bool HangDetected { get; set; }     // 부하 활성 구간에 received 정지
    public long ReceivedFinal { get; set; }    // count-stable 권위 수신값
    public long SentTotal { get; set; }        // Σ 신뢰 클라 송신(inc+dec)
    public long TestFinal { get; set; }        // 서버 test 순증감
    public long SentInc { get; set; }          // Σ 신뢰 클라 increment 송신
    public long SentDec { get; set; }          // Σ 신뢰 클라 decrement 송신
    public int SessionsFinal { get; set; }     // settle 후 활성 세션 수
    public long HeapBaseline { get; set; }     // 워밍업 시 heapBytes
    public long HeapFinal { get; set; }        // settle 후 heapBytes
    public double HeapTolerance { get; set; }  // 허용 배수
}
