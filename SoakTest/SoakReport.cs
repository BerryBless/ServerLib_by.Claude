namespace SoakTest;

/// <summary>
/// 소크 테스트 종료 시 Hard/Soft 판정 결과를 담는 불변 레코드입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변(init-only). Thread-safe.
/// <b>[판정 분류]</b>
/// <list type="bullet">
/// <item><description><b>Hard FAIL</b> → <see cref="OverallPass"/> = false → 종료코드 1</description></item>
/// <item><description><b>Soft FAIL</b> → 자문 경고만 출력, verdict 무영향</description></item>
/// </list>
/// </remarks>
public sealed class SoakReport
{
    // ── Hard 체크 ────────────────────────────────────────────
    /// <summary>[Hard] 서버가 'q' 송신 전에 예기치 않게 종료(크래시)했는지를 나타냅니다.</summary>
    public bool Crash { get; init; }
    /// <summary>[Hard] 클라이언트 전원 종료 후 서버 세션 수가 0이 아닌지를 나타냅니다.</summary>
    public bool SessionLeak { get; init; }
    /// <summary>[Hard] 서버 received &lt; 클라이언트 sent인지를 나타냅니다.</summary>
    public bool DataLoss { get; init; }
    /// <summary>[Hard] 클라이언트 오류율이 임계(5%)를 초과했는지를 나타냅니다.</summary>
    public bool ClientErrorRateHigh { get; init; }

    // ── Soft 체크 ────────────────────────────────────────────
    /// <summary>[Soft] 종료 heap이 기준 heap × 4를 초과했는지를 나타냅니다. verdict 무영향.</summary>
    public bool HeapGrowth { get; init; }

    // ── 통계 ─────────────────────────────────────────────────
    /// <summary>클라이언트 측 총 송신 성공 횟수입니다.</summary>
    public long FinalSent { get; init; }
    /// <summary>서버 측 총 수신 횟수(권위 read)입니다.</summary>
    public long FinalServerReceived { get; init; }
    /// <summary>종료 시 서버 활성 세션 수입니다.</summary>
    public long FinalServerSessions { get; init; }
    /// <summary>종료 시 서버 힙 사용량(바이트)입니다.</summary>
    public long FinalHeapBytes { get; init; }
    /// <summary>기준 힙 사용량(바이트). 서버 준비 직후 측정값입니다.</summary>
    public long BaselineHeapBytes { get; init; }
    /// <summary>총 완료 churn 사이클 수입니다.</summary>
    public long TotalCycles { get; init; }
    /// <summary>총 클라이언트 오류 수입니다.</summary>
    public long TotalErrors { get; init; }
    /// <summary>총 연결 성공 횟수입니다.</summary>
    public long TotalConnects { get; init; }

    /// <summary>Hard 체크 전부 통과 시 true입니다.</summary>
    public bool OverallPass => !Crash && !SessionLeak && !DataLoss && !ClientErrorRateHigh;

    // ── 출력 ─────────────────────────────────────────────────
    /// <summary>판정 결과를 콘솔에 출력합니다.</summary>
    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine(
            $"  cycles={TotalCycles:N0}  conns={TotalConnects:N0}  " +
            $"sent={FinalSent:N0}  serverRecv={FinalServerReceived:N0}  errs={TotalErrors}");
        Console.WriteLine(
            $"  sessions={FinalServerSessions}  " +
            $"heap={FinalHeapBytes / 1024:N0}KB  baseline={BaselineHeapBytes / 1024:N0}KB");
        Console.WriteLine("───────────────────────────────────────────────────────");
        PrintCheck("Crash",           !Crash,               "[Hard]");
        PrintCheck("SessionLeak",     !SessionLeak,         "[Hard]");
        PrintCheck("DataLoss",        !DataLoss,            "[Hard]");
        PrintCheck("ClientErrorRate", !ClientErrorRateHigh, "[Hard]");
        PrintCheck("HeapGrowth",      !HeapGrowth,          "[Soft]");
        Console.WriteLine("───────────────────────────────────────────────────────");
        Console.WriteLine($"  RESULT {(OverallPass ? "PASS" : "FAIL")}");
        Console.WriteLine("═══════════════════════════════════════════════════════");
    }

    private static void PrintCheck(string name, bool pass, string kind)
    {
        string mark = pass ? "✓" : "✗";
        string label = pass ? "OK" : "FAIL";
        Console.WriteLine($"  {mark} {name,-20} {kind}  {label}");
    }

    // ── 팩토리 ───────────────────────────────────────────────
    /// <summary>
    /// 수집된 통계 데이터를 바탕으로 판정 리포트를 생성합니다.
    /// </summary>
    /// <param name="stats">클라이언트 측 집계 카운터입니다.</param>
    /// <param name="serverSnap">서버 안정화 후 최종 스냅샷입니다. attach 모드에서 null입니다.</param>
    /// <param name="baselineHeapBytes">서버 준비 직후 기준 heap 바이트입니다.</param>
    /// <param name="serverCrashed">서버 크래시 여부입니다.</param>
    /// <param name="attachMode">외부 서버 부착 모드 여부입니다. true이면 서버 측 체크를 생략합니다.</param>
    /// <returns>생성된 판정 리포트입니다.</returns>
    public static SoakReport Evaluate(
        SoakStats stats,
        ServerStatsSnapshot? serverSnap,
        long baselineHeapBytes,
        bool serverCrashed,
        bool attachMode)
    {
        long sent     = stats.Sent;
        long errors   = stats.Errors;
        long cycles   = stats.Cycles;
        long connects = stats.Connects;

        long serverReceived = serverSnap?.Received  ?? 0;
        long serverSessions = serverSnap?.Sessions  ?? 0;
        long heapBytes      = serverSnap?.HeapBytes ?? 0;

        // Crash: 우리가 'q'를 보내기 전 서버가 종료됨 — attach 모드에서는 관찰 불가
        bool crash = serverCrashed && !attachMode;

        // DataLoss: 서버 received < 클라 sent → attach 모드에서는 server stats 없으므로 생략
        bool dataLoss = !attachMode && (serverReceived < sent);

        // SessionLeak: 전 클라 종료 후 세션이 남으면 소켓 누수
        bool sessionLeak = !attachMode && (serverSessions != 0);

        // ClientErrorRate: 오류율이 연결 수의 5% 초과 — 연결 자체가 한 건도 없으면 무시
        bool highErrorRate = connects > 0 && ((errors * 100) / connects) > 5;

        // HeapGrowth: 연결 churn이 ArrayPool·Pipe를 정상적으로 확장 → 상시 발생 가능한 Soft 경고
        // 기준×4 초과 시만 경고(터무니없이 성장한 경우만)
        bool heapGrowth = !attachMode && baselineHeapBytes > 0 && (heapBytes > baselineHeapBytes * 4);

        return new SoakReport
        {
            Crash               = crash,
            SessionLeak         = sessionLeak,
            DataLoss            = dataLoss,
            ClientErrorRateHigh = highErrorRate,
            HeapGrowth          = heapGrowth,
            FinalSent           = sent,
            FinalServerReceived = serverReceived,
            FinalServerSessions = serverSessions,
            FinalHeapBytes      = heapBytes,
            BaselineHeapBytes   = baselineHeapBytes,
            TotalCycles         = cycles,
            TotalErrors         = errors,
            TotalConnects       = connects,
        };
    }
}
