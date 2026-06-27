using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;
using System.Diagnostics;
using System.Threading.Channels;

namespace DbPerfTest;

/// <summary>
/// closed-loop 방식으로 login(write) / token-resolve(read) 혼합 요청을 반복하는 클라이언트입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> RunAsync는 인스턴스당 단일 Task에서 호출합니다.
/// 여러 인스턴스를 병렬로 생성·실행하는 것은 안전합니다.<br/>
/// <b>[Memory:]</b> 연결당 Channel(unbounded inbox) 1개 + 수신 패킷별 byte[] 복사본.
/// BinaryPacketSerializer는 내부 상태가 없으므로 인스턴스당 1개 공유 안전합니다.<br/>
/// <b>[Blocking:]</b> Non-blocking. ReadNextAsync는 10초 per-response 타임아웃으로
/// 서버 무응답을 조기 탐지합니다.<br/>
/// <b>[Coordinated Omission 주의:]</b> closed-loop 계측은 지연 스파이크 구간에서
/// 요청을 제출하지 않으므로 실제 대기 시간을 과소집계합니다. 리포트에 명기됩니다.
/// </remarks>
public sealed class DbPerfClient
{
    private readonly string _host;
    private readonly int    _port;
    private readonly DbPerfOptions   _opt;
    private readonly LatencyRecorder _recorder;
    private readonly ClientStats     _stats;

    // BinaryPacketSerializer: 내부 가변 상태 없음(stateless) — 인스턴스당 1개 공유해도 Thread-safe
    private readonly BinaryPacketSerializer _serializer = new();

    /// <summary>DbPerfClient 인스턴스를 초기화합니다.</summary>
    /// <param name="host">접속할 서버 호스트</param>
    /// <param name="port">접속할 서버 포트</param>
    /// <param name="opt">부하 테스트 옵션(read/write 비율, 사용자명 등)</param>
    /// <param name="recorder">지연 기록기</param>
    /// <param name="stats">연결·오류 집계 카운터</param>
    public DbPerfClient(
        string host, int port,
        DbPerfOptions opt,
        LatencyRecorder recorder,
        ClientStats stats)
    {
        _host     = host;
        _port     = port;
        _opt      = opt;
        _recorder = recorder;
        _stats    = stats;
    }

    /// <summary>취소 신호가 올 때까지 closed-loop 요청을 반복합니다.</summary>
    /// <param name="isRecording">
    /// true를 반환하면 측정값을 <see cref="LatencyRecorder"/>에 기록합니다.
    /// warmup 기간 중에는 false를 반환하는 람다를 전달합니다.
    /// </param>
    /// <param name="ct">루프 중단 신호입니다.</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 인스턴스당 단일 Task에서 호출합니다. 동시 호출 금지.<br/>
    /// <b>[Memory:]</b> 수신 패킷마다 byte[] 복사본 1개 할당(Span은 콜백 기간만 유효하므로 필수).
    /// 그 외 루프 내 추가 힙 할당은 없습니다.<br/>
    /// <b>[Blocking:]</b> Non-blocking. await conn.ConnectAsync, await ReadNextAsync(10초 타임아웃)만 대기합니다.<br/>
    /// <b>[Coordinated Omission:]</b> closed-loop 특성상 서버 지연 스파이크 구간에는 요청이 제출되지 않아
    /// 지연 백분위가 실제보다 낮게 측정될 수 있습니다.
    /// </remarks>
    public async Task RunAsync(Func<bool> isRecording, CancellationToken ct)
    {
        // IClientConnection: IAsyncDisposable — await using으로 graceful FIN(TCP 연결 정상 종료) 보장
        await using var conn = ServerNet.CreateClient();

        // Channel<byte[]>: lock-free MPSC 큐 — OnReceived(IO 스레드) → 단일 루프 스레드로 전달
        // SingleReader/SingleWriter: 런타임이 락-프리 최적화 경로를 선택할 수 있는 힌트 제공
        var inbox = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        conn.OnReceived = data =>
        {
            // PacketId(헤더 첫 2바이트 LE ushort)를 확인해 LoginResponsePacket(Id=11)만 큐잉.
            // MobHpPacket(Id=6) 등 보스몹 브로드캐스트가 먼저 도착하면 ReadNextAsync가 잘못된 프레임을
            // LoginResponsePacket으로 역직렬화해 closed-loop 상관관계가 영구 파괴된다.
            if (data.Length >= 2)
            {
                ushort pid = (ushort)(data.Span[0] | (data.Span[1] << 8));
                if (pid == LoginResponsePacket.Id)
                    inbox.Writer.TryWrite(data.Span.ToArray());
            }
            return ValueTask.CompletedTask;
        };

        try
        {
            await conn.ConnectAsync(_host, _port, ct);
            _stats.IncConnect();

            // Prelude: 초기 로그인 → token 확보 (warmup/measure 루프 진입 전 1회)
            string token = await PreludeLoginAsync(conn, inbox, ct);

            int opCounter = 0;
            while (!ct.IsCancellationRequested)
            {
                bool isRead      = _opt.IsReadOp(opCounter++);
                long startTicks  = Stopwatch.GetTimestamp();

                if (isRead)
                {
                    // read path: AuthToken(Id=12) 전송 → 서버가 Redis에서 토큰 검증 후 LoginResponse(Id=11) 반환
                    await conn.SendAsync(new AuthTokenPacket { Token = token }, ct);
                    await ReadNextAsync(inbox, ct);
                }
                else
                {
                    // write path: LoginRequest(Id=10) → MySQL SELECT + PBKDF2 검증 + Redis SET → LoginResponse(Id=11) 반환
                    await conn.SendAsync(
                        new LoginRequestPacket { Username = _opt.Username, Password = _opt.Password }, ct);
                    var raw  = await ReadNextAsync(inbox, ct);
                    var resp = _serializer.Deserialize<LoginResponsePacket>(raw.AsSpan());
                    // 토큰 갱신: 새 로그인마다 서버가 새 토큰을 발급 — 이후 read path에서 최신 토큰 사용
                    if (resp.Success && resp.Token.Length > 0)
                        token = resp.Token;
                }

                if (isRecording())
                {
                    // Stopwatch.GetTimestamp 차이를 마이크로초로 변환 (Frequency = ticks/sec)
                    long us = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000L / Stopwatch.Frequency;
                    if (isRead) _recorder.RecordRead(us);
                    else        _recorder.RecordWrite(us);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ct가 살아있으면 10s 타임아웃 fired — 서버 hang 탐지, 에러로 집계
            // ct가 취소됐으면 정상 종료 신호
            if (!ct.IsCancellationRequested)
                _stats.IncError();
        }
        catch (Exception ex)
        {
            _stats.IncError();
            Console.Error.WriteLine($"[DbPerfClient] 오류: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>연결 직후 초기 로그인을 수행하고 서버 발급 토큰을 반환합니다.</summary>
    private async Task<string> PreludeLoginAsync(
        IClientConnection conn, Channel<byte[]> inbox, CancellationToken ct)
    {
        await conn.SendAsync(
            new LoginRequestPacket { Username = _opt.Username, Password = _opt.Password }, ct);
        var raw  = await ReadNextAsync(inbox, ct);
        var resp = _serializer.Deserialize<LoginResponsePacket>(raw.AsSpan());

        if (!resp.Success)
            throw new InvalidOperationException(
                $"[DbPerfClient] 초기 로그인 실패 (user={_opt.Username}). " +
                "서버에 EnableLogin=true·SeedTestUser=true인지 확인하세요. " +
                "또는 docker compose up -d 후 재시도하세요.");

        return resp.Token;
    }

    // CancellationTokenSource(10s): per-response 타임아웃 — 서버 응답 없음을 루프 전체 hang 없이 조기 탐지
    private static async ValueTask<byte[]> ReadNextAsync(
        Channel<byte[]> inbox, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return await inbox.Reader.ReadAsync(linked.Token);
    }
}
