namespace DbPerfTest;

/// <summary>DbPerfTest 성능 측정 결과 및 Hard/Soft 판정 리포트입니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변(init-only). Thread-safe.
/// </remarks>
public sealed class DbPerfReport
{
    // ── Hard checks ──────────────────────────────────────────────────────────
    /// <summary>[Hard] 서버가 'q' 전에 종료(크래시)했는지를 나타냅니다.</summary>
    public bool Crash                 { get; init; }

    /// <summary>[Hard] 클라이언트 전원 종료 후 서버 세션이 남았는지를 나타냅니다.</summary>
    public bool SessionLeak           { get; init; }

    /// <summary>[Hard] 클라이언트 오류율 &gt; 5%인지를 나타냅니다.</summary>
    public bool ClientErrorRateHigh   { get; init; }

    /// <summary>[Hard] 총 throughput이 목표 미달인지를 나타냅니다. TargetThroughput 미설정 시 항상 false.</summary>
    public bool ThroughputBelowTarget { get; init; }

    /// <summary>[Hard] write 또는 read p99가 목표 초과인지를 나타냅니다. TargetP99Ms 미설정 시 항상 false.</summary>
    public bool LatencyAboveTarget    { get; init; }

    /// <summary>[Hard] [DBSTATS] 라인 미수신(vacuous PASS 방지)입니다.</summary>
    public bool NoDbData              { get; init; }

    // ── Soft checks ───────────────────────────────────────────────────────────
    /// <summary>[Soft] 최종 heap &gt; baseline × 4인지를 나타냅니다. verdict 무영향.</summary>
    public bool HeapGrowth            { get; init; }

    // ── 통계 ─────────────────────────────────────────────────────────────────
    /// <summary>write 지연 백분위 결과(마이크로초)입니다.</summary>
    public PercentileResult WritePercentiles { get; init; }

    /// <summary>read 지연 백분위 결과(마이크로초)입니다.</summary>
    public PercentileResult ReadPercentiles  { get; init; }

    /// <summary>write throughput(req/s)입니다.</summary>
    public double WriteThroughput            { get; init; }

    /// <summary>read throughput(req/s)입니다.</summary>
    public double ReadThroughput             { get; init; }

    /// <summary>DB 연산 평균 지연 스냅샷입니다. null이면 [DBSTATS] 미수신.</summary>
    public DbStatsSnapshot? DbStats          { get; init; }

    /// <summary>측정 시작 시점 서버 heap 크기(바이트)입니다.</summary>
    public long BaselineHeapBytes            { get; init; }

    /// <summary>측정 종료 시점 서버 heap 크기(바이트)입니다.</summary>
    public long FinalHeapBytes               { get; init; }

    /// <summary>Hard 체크 전부 통과 시 true입니다.</summary>
    public bool OverallPass =>
        !Crash && !SessionLeak && !ClientErrorRateHigh
        && !ThroughputBelowTarget && !LatencyAboveTarget && !NoDbData;

    /// <summary>측정 결과로부터 판정 리포트를 생성합니다.</summary>
    /// <param name="recorder">write/read 지연 레코더</param>
    /// <param name="elapsedSec">측정 경과 시간(초)</param>
    /// <param name="clientStats">클라이언트 오류·연결 집계</param>
    /// <param name="serverSnap">서버 [STATS] 스냅샷. attachMode=true 시 null 허용.</param>
    /// <param name="baselineHeap">측정 시작 시점 서버 heap(바이트). 0이면 HeapGrowth 체크 생략.</param>
    /// <param name="serverCrashed">서버가 비정상 종료했으면 true.</param>
    /// <param name="attachMode">외부 서버 부착 모드이면 true. Crash/SessionLeak 체크를 생략한다.</param>
    /// <param name="dbSnap">[DBSTATS] 파싱 결과. null이면 NoDbData=true.</param>
    /// <param name="opt">CLI 파싱 옵션. TargetThroughput/TargetP99Ms 임계를 읽는다.</param>
    /// <returns>모든 Hard/Soft 체크 결과를 담은 불변 <see cref="DbPerfReport"/> 인스턴스.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 불변 입력만 읽고 새 인스턴스를 반환한다.<br/>
    /// <b>[Blocking:]</b> recorder.GetWritePercentiles()/GetReadPercentiles() 내 Array.Sort O(n log n) 동기 블로킹.
    /// </remarks>
    public static DbPerfReport Evaluate(
        LatencyRecorder recorder, double elapsedSec, ClientStats clientStats,
        ServerStatsSnapshot? serverSnap, long baselineHeap,
        bool serverCrashed, bool attachMode,
        DbStatsSnapshot? dbSnap, DbPerfOptions opt)
    {
        var writeP = recorder.GetWritePercentiles();
        var readP  = recorder.GetReadPercentiles();

        double writeTput = elapsedSec > 0 ? recorder.WriteCount / elapsedSec : 0;
        double readTput  = elapsedSec > 0 ? recorder.ReadCount  / elapsedSec : 0;
        double totalTput = writeTput + readTput;

        long connects = clientStats.Connects;
        long errors   = clientStats.Errors;
        long sessions = serverSnap?.Sessions  ?? 0;
        long heap     = serverSnap?.HeapBytes ?? 0;

        bool crash       = serverCrashed && !attachMode;
        bool sessionLeak = !attachMode && sessions != 0;
        bool highErrRate = connects > 0 && (errors * 100 / connects) > 5;
        bool tputBelow   = opt.TargetThroughput.HasValue && totalTput < opt.TargetThroughput.Value;
        // p99는 마이크로초 단위 → /1000으로 밀리초 변환
        bool latAbove    = opt.TargetP99Ms.HasValue &&
                           (writeP.P99 / 1000 > opt.TargetP99Ms.Value ||
                            readP.P99  / 1000 > opt.TargetP99Ms.Value);
        bool noDbData    = dbSnap is null;
        bool heapGrowth  = !attachMode && baselineHeap > 0 && heap > baselineHeap * 4;

        return new DbPerfReport
        {
            Crash                 = crash,
            SessionLeak           = sessionLeak,
            ClientErrorRateHigh   = highErrRate,
            ThroughputBelowTarget = tputBelow,
            LatencyAboveTarget    = latAbove,
            NoDbData              = noDbData,
            HeapGrowth            = heapGrowth,
            WritePercentiles      = writeP,
            ReadPercentiles       = readP,
            WriteThroughput       = writeTput,
            ReadThroughput        = readTput,
            DbStats               = dbSnap,
            BaselineHeapBytes     = baselineHeap,
            FinalHeapBytes        = heap,
        };
    }

    /// <summary>판정 결과를 콘솔에 출력합니다.</summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서 호출할 것.<br/>
    /// <b>[Blocking:]</b> Console.WriteLine 동기 I/O.
    /// </remarks>
    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  [DbPerf] write  rps={WriteThroughput:F1}  " +
                          $"p50={WritePercentiles.P50 / 1000}ms  p95={WritePercentiles.P95 / 1000}ms  " +
                          $"p99={WritePercentiles.P99 / 1000}ms  max={WritePercentiles.Max / 1000}ms  " +
                          $"n={WritePercentiles.Count:N0}");
        Console.WriteLine($"  [DbPerf]  read  rps={ReadThroughput:F1}  " +
                          $"p50={ReadPercentiles.P50 / 1000}ms  p95={ReadPercentiles.P95 / 1000}ms  " +
                          $"p99={ReadPercentiles.P99 / 1000}ms  max={ReadPercentiles.Max / 1000}ms  " +
                          $"n={ReadPercentiles.Count:N0}");
        if (DbStats is { } ds)
            Console.WriteLine($"  [DbPerf] dbstats  mysql_select={ds.MysqlSelectAvgUs}µs(n={ds.MysqlCount})  " +
                              $"redis_get={ds.RedisGetAvgUs}µs(n={ds.RedisGetCount})  " +
                              $"redis_set={ds.RedisSetAvgUs}µs(n={ds.RedisSetCount})");
        Console.WriteLine($"  [DbPerf] heap  baseline={BaselineHeapBytes / 1024:N0}KB  " +
                          $"final={FinalHeapBytes / 1024:N0}KB");
        Console.WriteLine("  ⚠ known caveat: closed-loop은 지연 스파이크를 과소집계합니다");
        Console.WriteLine("───────────────────────────────────────────────────────");

        PrintCheck("Crash",            !Crash,                 "[Hard]");
        PrintCheck("SessionLeak",      !SessionLeak,           "[Hard]");
        PrintCheck("ClientErrorRate",  !ClientErrorRateHigh,   "[Hard]");
        PrintCheck("ThroughputTarget", !ThroughputBelowTarget, "[Hard]");
        PrintCheck("LatencyTarget",    !LatencyAboveTarget,    "[Hard]");
        PrintCheck("NoDbData",         !NoDbData,              "[Hard]");
        PrintCheck("HeapGrowth",       !HeapGrowth,            "[Soft]");

        Console.WriteLine("───────────────────────────────────────────────────────");
        Console.WriteLine($"  RESULT {(OverallPass ? "PASS ✓" : "FAIL ✗")}");
        Console.WriteLine("═══════════════════════════════════════════════════════");
    }

    private static void PrintCheck(string name, bool pass, string kind)
    {
        string mark  = pass ? "✓" : "✗";
        string label = pass ? "OK" : "FAIL";
        Console.WriteLine($"  {mark} {name,-22} {kind}  {label}");
    }
}
