using System.Diagnostics;
using StabilityTest;

var config = StabilityConfig.Parse(args);
Console.WriteLine($"=== StabilityTest === seed={config.Seed} port={config.Port} " +
                  $"burst={config.BurstSeconds}s settle={config.SettleSeconds}s maxClients={config.MaxReliableClients}");

using var server = new ServerProcess(config);
server.Start();
await server.WaitForReadyAsync(TimeSpan.FromSeconds(15));
Console.WriteLine("[harness] 서버 준비 완료.");

var evidence = new StabilityEvidence { HeapTolerance = config.HeapTolerance };
var reliable = new List<ReliableClient>();
long SentTotal() => SumSent(reliable);

using var monitorCts = new CancellationTokenSource();
var monitor = new StabilityMonitor(server);
var monitorTask = monitor.RunAsync(SentTotal, () => reliable.Count, monitorCts.Token);

// 1) 워밍업 & baseline ----------------------------------------------------------
for (int i = 0; i < Math.Min(10, config.MaxReliableClients); i++)
{
    var c = new ReliableClient();
    await c.ConnectAsync(config.Host, config.Port, CancellationToken.None);
    reliable.Add(c);
}
await Task.Delay(2000); // [STATS] 몇 개 수신해 baseline 확보
evidence.HeapBaseline = server.TryGetLatest(out var bs) ? bs.HeapBytes : server.PrivateMemoryBytes;
Console.WriteLine($"[harness] baseline heapBytes={evidence.HeapBaseline:N0}");

// 2) 폭주 구간 -----------------------------------------------------------------
var timeline = new BurstScheduler(config).BuildTimeline();
Console.WriteLine($"[harness] 폭주 이벤트 {timeline.Count}개 스케줄됨.");
var burstSw = Stopwatch.StartNew();
long lastReceived = 0;
int frozenSamples = 0;
int hangSampleAccumMs = 0;

foreach (var ev in timeline)
{
    // 다음 이벤트 시각까지 대기 — 그 사이 행/크래시 감시
    while (burstSw.ElapsedMilliseconds < ev.TimeOffsetMs)
    {
        await Task.Delay(200);
        hangSampleAccumMs += 200;
        if (server.HasExited) { evidence.Crashed = true; goto AfterBurst; }
        // 부하 활성 구간(폭주 중) — received가 1초 주기로 전진하는지 감시
        if (hangSampleAccumMs >= 1000)
        {
            hangSampleAccumMs = 0;
            server.TryGetLatest(out var snap);
            if (snap.Received == lastReceived) frozenSamples++;
            else { frozenSamples = 0; lastReceived = snap.Received; }
            if (frozenSamples >= config.HangFrozenSamples && SentTotal() > 0)
            {
                evidence.HangDetected = true;
                Console.WriteLine("[harness] HANG 감지 — 부하 중 received 정지.");
                goto AfterBurst;
            }
        }
    }

    if (server.HasExited) { evidence.Crashed = true; goto AfterBurst; }

    if (ev.Type == BurstEventType.ConnectionStorm)
    {
        // 연결 폭주: 카오스 클라이언트 — 0바이트, fire-and-forget
        _ = ChaosClient.StormAsync(config.Host, config.Port, ev.Magnitude, CancellationToken.None);
    }
    else
    {
        // 트래픽 스파이크: 활성 신뢰 클라이언트(부족하면 새로 연결)에게 burst 송신
        await EnsureReliableAsync(reliable, config, Math.Min(config.MaxReliableClients, 50));
        var spikeTargets = reliable.ToArray();
        _ = Task.WhenAll(spikeTargets.Select(c => SafeSendAsync(c, ev.Magnitude / Math.Max(1, spikeTargets.Length))));
    }
}

AfterBurst:
Console.WriteLine($"[harness] 폭주 구간 종료 (crashed={evidence.Crashed} hang={evidence.HangDetected}).");

// 3) drain & settle: 부하 중단 후 received가 count-stable 될 때까지 ------------------
if (!evidence.Crashed)
{
    long prev = -1; int stable = 0;
    var settleDeadline = Stopwatch.StartNew();
    while (settleDeadline.Elapsed < TimeSpan.FromSeconds(config.SettleSeconds))
    {
        await Task.Delay(1000);
        if (server.HasExited) { evidence.Crashed = true; break; }
        server.TryGetLatest(out var snap);
        if (snap.Received == prev) { if (++stable >= config.CountStableSamples) break; }
        else { stable = 0; prev = snap.Received; }
    }
}

// 신뢰 클라이언트 graceful 종료(FIN) → 모든 송신분이 서버에 도달했음을 보장
foreach (var c in reliable)
{
    try { await c.DisposeAsync(); } catch { }
}
await Task.Delay(2000); // FIN 처리·세션 정리 반영 대기

// 4) 권위 읽기 -----------------------------------------------------------------
if (!evidence.Crashed)
{
    // 세션 정리 완료를 위해 잠시 더 폴링(연결 폭주 RST 정리 포함)
    StatsSnapshot finalSnap = default;
    var pollSw = Stopwatch.StartNew();
    while (pollSw.Elapsed < TimeSpan.FromSeconds(10))
    {
        await Task.Delay(1000);
        if (server.HasExited) { evidence.Crashed = true; break; }
        server.TryGetLatest(out finalSnap);
        if (finalSnap.Sessions == 0) break; // 정리 완료
    }
    evidence.ReceivedFinal = finalSnap.Received;
    evidence.TestFinal = finalSnap.Test;
    evidence.SessionsFinal = finalSnap.Sessions;
    evidence.HeapFinal = finalSnap.HeapBytes;
}

evidence.SentInc = reliable.Sum(c => c.SentInc);
evidence.SentDec = reliable.Sum(c => c.SentDec);
evidence.SentTotal = evidence.SentInc + evidence.SentDec;

// 5) 종료 & 판정 ----------------------------------------------------------------
monitorCts.Cancel();
try { await monitorTask; } catch { }
await server.StopGracefullyAsync(TimeSpan.FromSeconds(10));
evidence.ExitCode = server.HasExited ? SafeExitCode(server) : -1;

var (results, pass) = StabilityReport.Evaluate(evidence);
Console.WriteLine();
Console.WriteLine("================ STABILITY REPORT ================");
Console.WriteLine($" seed={config.Seed}  (실패 시 동일 seed로 재현)");
foreach (var r in results)
    Console.WriteLine($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name,-13} ({r.Severity}) — {r.Detail}");
Console.WriteLine("==================================================");
Console.WriteLine(pass ? "RESULT: PASS ✅" : "RESULT: FAIL ❌");
return pass ? 0 : 1;

// ---- 로컬 헬퍼 ----
static long SumSent(List<ReliableClient> clients) => clients.Sum(c => c.SentTotal);

static int SafeExitCode(ServerProcess s) { try { return s.ExitCode; } catch { return -1; } }

static async Task SafeSendAsync(ReliableClient c, int count)
{
    try { await c.SendBurstAsync(count, CancellationToken.None); }
    catch { /* 개별 클라 송신 실패가 전체를 중단시키지 않음 */ }
}

static async Task EnsureReliableAsync(List<ReliableClient> clients, StabilityConfig cfg, int target)
{
    while (clients.Count < target && clients.Count < cfg.MaxReliableClients)
    {
        var c = new ReliableClient();
        try { await c.ConnectAsync(cfg.Host, cfg.Port, CancellationToken.None); clients.Add(c); }
        catch { await c.DisposeAsync(); break; }
    }
}
