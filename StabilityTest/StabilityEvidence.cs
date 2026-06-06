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

public enum CheckSeverity { Hard, Soft }

/// <summary>단일 체크 결과. <see cref="Severity"/>가 Hard이고 미통과면 전체 FAIL입니다.</summary>
public readonly record struct CheckResult(string Name, bool Passed, CheckSeverity Severity, string Detail);

/// <summary>측정 증거로부터 4대 실패모드 체크를 평가하여 PASS/FAIL을 산출합니다.</summary>
public static class StabilityReport
{
    public static (IReadOnlyList<CheckResult> Results, bool OverallPass) Evaluate(StabilityEvidence e)
    {
        long expectedTest = e.SentInc - e.SentDec;
        long heapLimit = (long)(e.HeapBaseline * e.HeapTolerance);

        var results = new List<CheckResult>
        {
            new("Crash", !e.Crashed && e.ExitCode == 0, CheckSeverity.Hard,
                e.Crashed ? "실행 중 child 종료" : $"exitCode={e.ExitCode}"),
            new("Hang", !e.HangDetected, CheckSeverity.Hard,
                e.HangDetected ? "부하 중 received 정지" : "부하 구간 내 처리 지속"),
            new("DataLoss", e.ReceivedFinal == e.SentTotal, CheckSeverity.Hard,
                $"received={e.ReceivedFinal} sent={e.SentTotal}"),
            new("Corruption", e.TestFinal == expectedTest, CheckSeverity.Hard,
                $"test={e.TestFinal} expected={expectedTest} (inc={e.SentInc} dec={e.SentDec})"),
            new("LeakSessions", e.SessionsFinal == 0, CheckSeverity.Hard,
                $"sessions={e.SessionsFinal} (기대 0)"),
            new("LeakHeap", e.HeapFinal <= heapLimit, CheckSeverity.Soft,
                $"heapFinal={e.HeapFinal} limit={heapLimit} (baseline={e.HeapBaseline}×{e.HeapTolerance})"),
        };

        bool overallPass = results.Where(r => r.Severity == CheckSeverity.Hard).All(r => r.Passed);
        return (results, overallPass);
    }
}
