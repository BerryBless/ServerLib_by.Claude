using LoadTest;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1]) : 9000;
var clientCount = args.Length > 2 ? int.Parse(args[2]) : 100;
var durationSec = args.Length > 3 ? int.Parse(args[3]) : 60;

Console.WriteLine($"LoadTest: {clientCount} clients → {host}:{port} for {durationSec}s");

var monitor = new LoadMonitor();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

// 5초 주기 모니터 시작
_ = monitor.RunAsync(cts.Token);

// 클라이언트 동시 실행
var clients = Enumerable.Range(0, clientCount)
    .Select(_ => new DummyClient(monitor))
    .ToArray();

await Parallel.ForEachAsync(clients, cts.Token, async (client, ct) =>
{
    await client.RunAsync(host, port, ct);
    await client.DisposeAsync();
});

Console.WriteLine("LoadTest completed.");
