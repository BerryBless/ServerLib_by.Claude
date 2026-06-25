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
    // ── 공통 Hard 체크 ────────────────────────────────────────────────────────────
    /// <summary>[Hard] 서버가 'q' 송신 전에 예기치 않게 종료(크래시)했는지를 나타냅니다.</summary>
    public bool Crash { get; init; }
    /// <summary>[Hard] 클라이언트 전원 종료 후 서버 세션 수가 0이 아닌지를 나타냅니다.</summary>
    public bool SessionLeak { get; init; }
    /// <summary>[Hard] 클라이언트 오류율이 임계(5%)를 초과했는지를 나타냅니다.</summary>
    public bool ClientErrorRateHigh { get; init; }

    // ── Damage 모드 Hard 체크 ─────────────────────────────────────────────────────
    /// <summary>[Hard][Damage] 서버 received &lt; 클라이언트 sent인지를 나타냅니다. 티켓팅 모드에서 생략.</summary>
    public bool DataLoss { get; init; }

    // ── 티켓팅 모드 Hard 체크 ─────────────────────────────────────────────────────
    /// <summary>
    /// [Hard][티켓팅] 클라이언트 전원 종료 후 reserved가 0이 아닌지를 나타냅니다.
    /// 소유자 없는 좌석이 예약 상태로 고착됐음을 의미합니다.
    /// </summary>
    public bool SlotLeak { get; init; }

    /// <summary>
    /// [Hard][티켓팅] KPI 보존식이 성립하지 않는지를 나타냅니다.
    /// <c>totalReserved != confirmed + payfail + abandon + expired + reserved</c>
    /// 카운터 정합성 붕괴 — SlotLeak의 상위호환(중간 정합성까지 포착).
    /// </summary>
    public bool KpiConservation { get; init; }

    /// <summary>
    /// [Hard][티켓팅] 좌석 인벤토리 불변식이 성립하지 않는지를 나타냅니다.
    /// <c>free + reserved + sold != totalSeats</c>
    /// </summary>
    public bool SeatConservation { get; init; }

    /// <summary>
    /// [Hard][티켓팅] 티켓팅 모드에서 서버가 <c>[TICKET]</c> KPI 라인을 한 번도 출력하지 않은지를 나타냅니다.
    /// <c>EnableTicketing</c> config 오버라이드 실패·<c>reserved_total=</c> 판별 미스 등을 조기에 탐지합니다.
    /// ticketSnap이 null이면 SlotLeak·KpiConservation·SeatConservation은 전부 false(무관찰)이므로
    /// 이 플래그로 vacuous PASS를 방지합니다.
    /// </summary>
    public bool NoTicketData { get; init; }

    // ── Soft 체크 ────────────────────────────────────────────────────────────────
    /// <summary>[Soft] 종료 heap이 기준 heap × 4를 초과했는지를 나타냅니다. verdict 무영향.</summary>
    public bool HeapGrowth { get; init; }

    // ── 모드 플래그 ───────────────────────────────────────────────────────────────
    /// <summary>티켓팅 모드 여부입니다. true이면 티켓팅 체크를 활성화하고 DataLoss를 생략합니다.</summary>
    public bool IsTicketing { get; init; }

    // ── 통계 ─────────────────────────────────────────────────────────────────────
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
    /// <summary>[티켓팅] 최종 TicketSnapshot. 비티켓팅 모드에서 null.</summary>
    public TicketSnapshot? FinalTicketSnap { get; init; }

    /// <summary>Hard 체크 전부 통과 시 true입니다.</summary>
    public bool OverallPass => IsTicketing
        ? !Crash && !SessionLeak && !ClientErrorRateHigh && !SlotLeak && !KpiConservation && !SeatConservation && !NoTicketData
        : !Crash && !SessionLeak && !DataLoss && !ClientErrorRateHigh;

    // ── 출력 ─────────────────────────────────────────────────────────────────────
    /// <summary>판정 결과를 콘솔에 출력합니다.</summary>
    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        string mode = IsTicketing ? "[티켓팅]" : "[Damage]";
        Console.WriteLine($"  {mode}  cycles={TotalCycles:N0}  conns={TotalConnects:N0}  " +
                          $"sent={FinalSent:N0}  serverRecv={FinalServerReceived:N0}  errs={TotalErrors}");
        Console.WriteLine($"  sessions={FinalServerSessions}  " +
                          $"heap={FinalHeapBytes / 1024:N0}KB  baseline={BaselineHeapBytes / 1024:N0}KB");

        if (IsTicketing && FinalTicketSnap is { } t)
        {
            Console.WriteLine($"  [Ticket] free={t.Free} reserved={t.Reserved} sold={t.Sold}");
            Console.WriteLine($"  [KPI]    totalReserved={t.TotalReserved}  confirmed={t.TotalConfirmed}  " +
                              $"payfail={t.TotalPaymentFailed}  abandon={t.TotalAbandoned}  " +
                              $"expired={t.TotalExpired}  seatTaken={t.TotalSeatTaken}");
        }

        Console.WriteLine("───────────────────────────────────────────────────────");

        // 공통 Hard 체크
        PrintCheck("Crash",           !Crash,               "[Hard]");
        PrintCheck("SessionLeak",     !SessionLeak,         "[Hard]");
        PrintCheck("ClientErrorRate", !ClientErrorRateHigh, "[Hard]");

        if (IsTicketing)
        {
            // 티켓팅 Hard 체크
            PrintCheck("NoTicketData",     !NoTicketData,    "[Hard]");
            PrintCheck("SlotLeak",         !SlotLeak,        "[Hard]");
            PrintCheck("KpiConservation",  !KpiConservation, "[Hard]");
            PrintCheck("SeatConservation", !SeatConservation,"[Hard]");
        }
        else
        {
            // Damage Hard 체크
            PrintCheck("DataLoss",        !DataLoss,        "[Hard]");
        }

        // 공통 Soft 체크
        PrintCheck("HeapGrowth",      !HeapGrowth,          "[Soft]");

        Console.WriteLine("───────────────────────────────────────────────────────");
        Console.WriteLine($"  RESULT {(OverallPass ? "PASS" : "FAIL")}");
        Console.WriteLine("═══════════════════════════════════════════════════════");
    }

    private static void PrintCheck(string name, bool pass, string kind)
    {
        string mark  = pass ? "✓" : "✗";
        string label = pass ? "OK" : "FAIL";
        Console.WriteLine($"  {mark} {name,-20} {kind}  {label}");
    }

    // ── 팩토리 ───────────────────────────────────────────────────────────────────
    /// <summary>
    /// 수집된 통계 데이터를 바탕으로 판정 리포트를 생성합니다.
    /// </summary>
    /// <param name="stats">클라이언트 측 집계 카운터입니다.</param>
    /// <param name="serverSnap">서버 안정화 후 최종 스냅샷입니다. attach 모드에서 null입니다.</param>
    /// <param name="baselineHeapBytes">서버 준비 직후 기준 heap 바이트입니다.</param>
    /// <param name="serverCrashed">서버 크래시 여부입니다.</param>
    /// <param name="attachMode">외부 서버 부착 모드 여부입니다. true이면 서버 측 체크를 생략합니다.</param>
    /// <param name="isTicketing">티켓팅 워크로드 여부입니다. true이면 티켓팅 체크를 활성화하고 DataLoss를 생략합니다.</param>
    /// <param name="ticketSnap">최종 티켓 스냅샷입니다. 비티켓팅 모드에서 null입니다.</param>
    /// <param name="totalSeats">그리드 총 좌석 수(rows × cols)입니다. SeatConservation 체크에 사용합니다.</param>
    /// <returns>생성된 판정 리포트입니다.</returns>
    public static SoakReport Evaluate(
        SoakStats stats,
        ServerStatsSnapshot? serverSnap,
        long baselineHeapBytes,
        bool serverCrashed,
        bool attachMode,
        bool isTicketing = false,
        TicketSnapshot? ticketSnap = null,
        int totalSeats = 0)
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

        // SessionLeak: 전 클라 종료 후 세션이 남으면 소켓 누수
        bool sessionLeak = !attachMode && serverSessions != 0;

        // ClientErrorRate: 오류율이 연결 수의 5% 초과 — 연결 자체가 한 건도 없으면 무시
        // SeatTaken·RateLimited는 서버 응답(클라 예외 없음) → 여기 포함 안 됨
        bool highErrorRate = connects > 0 && ((errors * 100) / connects) > 5;

        // HeapGrowth: 연결 churn이 ArrayPool·Pipe를 정상 확장 → 상시 발생 가능한 Soft 경고
        bool heapGrowth = !attachMode && baselineHeapBytes > 0 && heapBytes > baselineHeapBytes * 4;

        // ── Damage 전용 ──────────────────────────────────────────────────────────
        // DataLoss: 서버 received < 클라 sent — 티켓팅 모드에서는 의미 없으므로 생략
        bool dataLoss = !isTicketing && !attachMode && serverReceived < sent;

        // ── 티켓팅 전용 ──────────────────────────────────────────────────────────
        bool noTicketData    = false;
        bool slotLeak        = false;
        bool kpiConservation = false;
        bool seatConservation= false;

        if (isTicketing && !attachMode)
        {
            if (ticketSnap is { } t)
            {
                // SlotLeak: 안정화 후 reserved != 0 → 소유자 없는 좌석 고착
                slotLeak = t.Reserved != 0;

                // KpiConservation: totalReserved != confirmed + payfail + abandon + expired + reserved
                // 티켓팅 KPI 보존식. 카운터 오버플로·레이스 조건·CAS 버그를 포착.
                long expected = t.TotalConfirmed + t.TotalPaymentFailed + t.TotalAbandoned + t.TotalExpired + t.Reserved;
                kpiConservation = t.TotalReserved != expected;

                // SeatConservation: free + reserved + sold != totalSeats → 인벤토리 불변식 붕괴
                seatConservation = totalSeats > 0 && (t.Free + t.Reserved + t.Sold) != totalSeats;
            }
            else
            {
                // NoTicketData: ticketSnap == null → 서버가 [TICKET] KPI를 한 번도 출력하지 않음
                // EnableTicketing config 오버라이드 실패·discriminator 버그 등을 조기 탐지.
                // 이 플래그 없이는 SlotLeak/KpiConservation/SeatConservation이 모두 false → vacuous PASS.
                noTicketData = true;
            }
        }

        return new SoakReport
        {
            Crash               = crash,
            SessionLeak         = sessionLeak,
            ClientErrorRateHigh = highErrorRate,
            DataLoss            = dataLoss,
            NoTicketData        = noTicketData,
            SlotLeak            = slotLeak,
            KpiConservation     = kpiConservation,
            SeatConservation    = seatConservation,
            HeapGrowth          = heapGrowth,
            IsTicketing         = isTicketing,
            FinalSent           = sent,
            FinalServerReceived = serverReceived,
            FinalServerSessions = serverSessions,
            FinalHeapBytes      = heapBytes,
            BaselineHeapBytes   = baselineHeapBytes,
            TotalCycles         = cycles,
            TotalErrors         = errors,
            TotalConnects       = connects,
            FinalTicketSnap     = ticketSnap,
        };
    }
}
