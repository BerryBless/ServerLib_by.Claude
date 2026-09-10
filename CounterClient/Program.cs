// =============================================================================
// CounterClient — 경합(contention) 시연용 다중 연결 카운터 클라이언트 예제
// =============================================================================
// 동작: 127.0.0.1:9300의 CounterServer에 연결 8개를 만들고, 모두 동시에 출발시켜
//       연결당 더하기 1,000회 · 빼기 750회를 뒤섞어 보냅니다(= 경합 유발).
//       모든 연결이 "내 명령은 전부 처리됐다"는 확인을 받은 뒤(배리어) 최종 조회 1회를 보내고,
//       최종값이 결정적 기대값과 일치하는지 판정해 PASS/FAIL과 종료 코드를 출력합니다.
//
// 기대값:
//   Value      = 8 × (1,000 − 750) = 2,000        ← 순(net) 증감 결과
//   AppliedOps = 8 × (1,000 + 750) = 14,000       ← 서버가 실제로 적용한 연산 수
//   AppliedOps를 함께 보는 이유: Value만 보면 "더하기 N + 빼기 N"과 "아무것도 처리 안 함"이
//   똑같이 0으로 보여, 상쇄가 결함을 가려 버립니다.
//
// 학습 포인트:
//   8개 연결이 같은 메모리를 마구 갱신해도(=경합), 서버가 Interlocked 원자 연산으로 갱신하면
//   최종값이 항상 정확히 2,000입니다. 경합을 없앤 게 아니라, 경합 하에서 정확한 것입니다.
//
// 실행법:
//   dotnet run --project CounterServer      (먼저 실행, 별도 터미널)
//   dotnet run --project CounterClient
//
// ※ 검증은 "새로 시작한 서버 + 클라이언트 1회 실행"을 전제로 합니다.
//   카운터는 서버 프로세스 수명 동안 누적되므로, 다시 검증하려면 서버를 재시작하십시오.
//
// 종료 코드: 0 = PASS, 1 = 값 불일치(FAIL), 2 = 통신 오류·타임아웃
// =============================================================================

using CounterClient;

// ── 학습용 설정 상수 ─────────────────────────────────────────────────────────
//
// 값을 바꿔 가며 경합 강도를 조절해 볼 수 있습니다. 어떤 조합이든 최종값은
// ConnectionCount × (Increments − Decrements)로 결정적이어야 합니다.
// Timeout: 루프백에서 14,000회 왕복은 보통 수 초 안에 끝나지만, CI·부하 상황의 스케줄링 지연을
//   감안해 넉넉히 30초로 둡니다. 무응답 시 매달리지 않고 정리 후 종료하기 위한 상한입니다.
var options = new CounterScenarioOptions
{
    Host = "127.0.0.1",
    Port = 9300,
    ConnectionCount = 8,
    IncrementsPerConnection = 1_000,
    DecrementsPerConnection = 750,
    Timeout = TimeSpan.FromSeconds(30),
};

Console.WriteLine("경합 카운터 클라이언트 — 다중 연결 동시 증감 검증");
Console.WriteLine($"  대상       : {options.Host}:{options.Port}");
Console.WriteLine($"  연결 수    : {options.ConnectionCount}");
Console.WriteLine($"  연결당 더하기/빼기 : {options.IncrementsPerConnection:N0} / {options.DecrementsPerConnection:N0}");
Console.WriteLine("───────────────────────────────────────");

try
{
    // CounterScenario.RunAsync: 연결 수립 → 공통 시작 신호 → 동시 증감 → 연결별 완료 확인(배리어)
    //   → 정지 상태 최종 조회 → 판정. Program과 E2E 테스트가 이 실행기를 공유합니다.
    // 값 불일치는 결과의 Passed=false로, 통신 오류·타임아웃은 예외로 표면화됩니다.
    CounterScenarioResult result = await CounterScenario.RunAsync(options);

    if (result.InitialSnapshot != default)
    {
        Console.WriteLine($"[경고] 시작 시점 카운터가 0이 아닙니다: {result.InitialSnapshot}");
        Console.WriteLine("       이 서버는 이미 다른 실행을 처리했습니다. 정확한 검증을 위해 서버를 재시작한 뒤 다시 실행하십시오.");
        Console.WriteLine("───────────────────────────────────────");
    }

    Console.WriteLine($"  소요 시간   : {result.Elapsed.TotalSeconds:F2}초");
    Console.WriteLine($"  Value       : 기대 {result.ExpectedValue:N0} / 실제 {result.ActualValue:N0}");
    Console.WriteLine($"  AppliedOps  : 기대 {result.ExpectedAppliedOps:N0} / 실제 {result.ActualAppliedOps:N0}");
    Console.WriteLine("───────────────────────────────────────");

    if (result.Passed)
    {
        Console.WriteLine("PASS — 8개 연결이 동시에 갱신했지만 최종값이 기대값과 정확히 일치합니다.");
        Console.WriteLine("       (서버가 Interlocked 원자 연산으로 갱신하므로 갱신 유실이 없습니다)");
        return 0;
    }

    Console.WriteLine("FAIL — 최종값이 기대값과 다릅니다.");
    // 진단 힌트: 두 값이 어긋나는 양상으로 원인을 좁힐 수 있습니다.
    if (result.ActualAppliedOps != result.ExpectedAppliedOps)
        Console.WriteLine("       AppliedOps 부족 → 일부 패킷이 서버에 도달·적용되지 않았습니다(전송/연결 문제).");
    else
        Console.WriteLine("       AppliedOps는 일치하는데 Value가 틀림 → 갱신 유실(비원자 갱신) 의심.");
    return 1;
}
catch (TimeoutException ex)
{
    // 기한 만료: RunAsync가 모든 연결을 정리한 뒤 던진 것입니다. PASS를 출력하지 않습니다.
    Console.WriteLine($"오류(타임아웃) — {ex.Message}");
    Console.WriteLine("       CounterServer가 127.0.0.1:9300에서 실행 중인지 확인하십시오.");
    return 2;
}
catch (Exception ex)
{
    // 연결 실패·중도 종료·프로토콜 위반 등. 일부 작업이 실패한 뒤 PASS를 출력하는 일은 없습니다.
    Console.WriteLine($"오류 — {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("       CounterServer가 127.0.0.1:9300에서 실행 중인지 확인하십시오.");
    return 2;
}
