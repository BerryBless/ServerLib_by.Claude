using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace SoakTest.Workloads;

/// <summary>
/// 티켓팅 reserve → pay / abandon / ttl-expire churn 워크로드입니다.
/// fire-and-pace 방식 — 서버 응답을 파싱하지 않고 타이밍으로만 흐름을 제어합니다.
/// 누수 진실은 전부 서버측 [TICKET] KPI에 있습니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe.
/// 각 <see cref="SoakClient"/> 인스턴스는 고유한 인스턴스를 소유합니다.
/// <b>[Memory:]</b>
/// Login·Pay 버퍼는 생성자에서 1회 직렬화 후 모든 사이클에서 재사용합니다.
/// Reserve 버퍼는 최대 크기로 생성자에서 1회 할당하고 사이클마다 재직렬화(내용 덮어쓰기)합니다.
/// <b>[Blocking:]</b> Non-blocking 기반이지만 <c>loginSettleMs</c>·<c>paymentDelayMs</c>·
/// ttl-expire 경로에서 <c>Task.Delay</c> 비동기 대기가 발생합니다.
/// </remarks>
internal sealed class TicketingWorkload : IWorkload
{
    private readonly int              _clientId;
    private readonly int              _k;               // seatsPerSession
    private readonly int              _totalRows;
    private readonly int              _totalCols;
    private readonly int              _paymentDelayMs;
    private readonly double           _abandonRate;
    private readonly double           _expireRate;
    private readonly int              _ttlMs;           // ttlSeconds × 1000
    private readonly int              _loginSettleMs;
    private readonly ContentionPattern _pattern;
    private readonly SoakStats        _stats;

    // BinaryPacketSerializer: Thread-safe — 내부 상태 없음. 사이클마다 reserve 직렬화에 재사용.
    private readonly BinaryPacketSerializer _serializer;

    // ReadOnlyMemory<byte>: 1회 직렬화한 LoginRequestPacket 버퍼. 모든 사이클에서 재사용(무할당).
    private readonly ReadOnlyMemory<byte> _loginBuf;

    // ReadOnlyMemory<byte>: 1회 직렬화한 TicketPayRequestPacket(본문 0B) 버퍼. 모든 사이클 재사용.
    private readonly ReadOnlyMemory<byte> _payBuf;

    // byte[]: 4(헤더) + 1(count) + MaxSeats×2(row,col 쌍) 크기로 1회 할당.
    // 사이클마다 재직렬화(내용 덮어쓰기)해 힙 재할당 없이 사용. 단일 Task 전용 — 스레드 안전 불필요.
    private readonly byte[] _reserveBuf;

    /// <summary>TicketingWorkload를 초기화합니다.</summary>
    /// <param name="clientId">클라이언트 인덱스(0-based). 더미 username·좌석 오프셋에 사용합니다.</param>
    /// <param name="seatsPerSession">사이클당 예약 좌석 수(K). 서버 MaxSeatsPerSession 이하여야 합니다.</param>
    /// <param name="totalRows">그리드 행 수입니다.</param>
    /// <param name="totalCols">그리드 열 수입니다.</param>
    /// <param name="paymentDelayMs">서버 결제 처리 지연(밀리초). pay 패킷 전송 후 FIN 전 충분히 대기합니다.</param>
    /// <param name="abandonRate">graceful-abandon 사이클 비율(0.0–1.0)입니다.</param>
    /// <param name="expireRate">ttl-expire 사이클 비율(0.0–1.0)입니다. abandonRate + expireRate ≤ 1.0.</param>
    /// <param name="ttlSeconds">서버 ReservationTtlSeconds. expire 사이클의 idle 보유 시간 계산에 사용합니다.</param>
    /// <param name="loginSettleMs">login 전송 후 reserve 전 대기(밀리초). 단일 연결 in-order 안전 마진입니다.</param>
    /// <param name="pattern">좌석 선택 경합 패턴입니다.</param>
    /// <param name="stats">공유 lock-free 집계 카운터입니다.</param>
    public TicketingWorkload(
        int clientId, int seatsPerSession,
        int totalRows, int totalCols,
        int paymentDelayMs, double abandonRate, double expireRate,
        int ttlSeconds, int loginSettleMs,
        ContentionPattern pattern,
        SoakStats stats)
    {
        _clientId       = clientId;
        _k              = seatsPerSession;
        _totalRows      = totalRows;
        _totalCols      = totalCols;
        _paymentDelayMs = paymentDelayMs;
        _abandonRate    = abandonRate;
        _expireRate     = expireRate;
        _ttlMs          = ttlSeconds * 1000;
        _loginSettleMs  = loginSettleMs;
        _pattern        = pattern;
        _stats          = stats;

        // BinaryPacketSerializer: Thread-safe — 내부 상태 없음. 직렬화 전용.
        _serializer = new BinaryPacketSerializer();

        // Login 버퍼: username = "soak{clientId}", password = "" (티켓팅 모드 더미 로그인)
        // sealed class LoginRequestPacket — new T() 1회 힙 할당(생성자에서만).
        var loginPkt = new LoginRequestPacket { Username = $"soak{clientId}", Password = "" };
        int loginSz  = PacketPool.HeaderSize + loginPkt.GetBodySize();
        var loginBuf = new byte[loginSz];
        _serializer.Serialize(loginPkt, loginBuf);
        _loginBuf = loginBuf.AsMemory();

        // Pay 버퍼: 본문 0B 고정(세션 컨텍스트로 결제 처리). 4B만 전송.
        // struct TicketPayRequestPacket — 제네릭 Serialize<T>이므로 박싱 없음.
        var payPkt = new TicketPayRequestPacket();
        int paySz  = PacketPool.HeaderSize + payPkt.GetBodySize(); // 4 + 0 = 4B
        var payBuf = new byte[paySz];
        _serializer.Serialize(payPkt, payBuf);
        _payBuf = payBuf.AsMemory();

        // Reserve 버퍼: 4(헤더) + 1(count) + K×2(row,col 쌍). K = seatsPerSession 상한.
        // 사이클마다 Serialize로 내용을 덮어쓰므로 재할당 없음.
        _reserveBuf = new byte[PacketPool.HeaderSize + 1 + seatsPerSession * 2];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 순서: login → loginSettle → pick seats → reserve → disposition(pay/abandon/expire).
    /// 모든 송신은 fire-and-forget — 서버 응답을 파싱하지 않습니다.
    /// SeatTaken·RateLimited는 서버측 seattaken/reserved KPI로 관찰합니다.
    /// </remarks>
    public async Task RunCycleAsync(IClientConnection conn, int cycleIndex, CancellationToken ct)
    {
        // ── 1. 더미 로그인 ────────────────────────────────────────────────────────
        // 서버가 TicketContext.Username을 설정하는 경로. TCP 연결 in-order 처리에 의존.
        // fire-and-pace: 응답 대기 없음.
        await conn.SendAsync(_loginBuf, ct);
        _stats.IncSent();

        // 안전 마진: 단일 연결 in-order이지만 서버 처리 확인 없이 진행하므로 소폭 대기.
        if (_loginSettleMs > 0)
            await Task.Delay(_loginSettleMs, ct);

        // ── 2. 좌석 선택 ──────────────────────────────────────────────────────────
        // SeatPicker: 순수 함수 — 외부 상태 없음. K = min(seatsPerSession, totalSeats).
        var (rows, cols) = SeatPicker.Pick(_pattern, _totalRows, _totalCols, _k, _clientId, cycleIndex);
        int actualCount  = rows.Length; // SeatPicker 내 clamp 반영

        // ── 3. 예약 패킷 빌드 & 송신 ──────────────────────────────────────────────
        // struct 직렬화: 제네릭 Serialize<T> — 박싱 없음.
        // _reserveBuf 재사용: 사이클마다 내용 덮어쓰기, 힙 재할당 없음.
        var reservePkt = new TicketReserveRequestPacket
        {
            Count = (byte)actualCount,
            Rows  = rows,
            Cols  = cols,
        };
        int reserveSz = PacketPool.HeaderSize + reservePkt.GetBodySize();
        _serializer.Serialize(reservePkt, _reserveBuf.AsSpan(0, reserveSz));

        // AsMemory(0, reserveSz): 정확한 바이트 수만 전송 (버퍼는 최대 크기로 할당됨)
        await conn.SendAsync(_reserveBuf.AsMemory(0, reserveSz), ct);
        _stats.IncSent();
        _stats.IncReserveSent();

        // ── 4. 사이클 처분 (비율 기반 랜덤 선택) ─────────────────────────────────
        // Random.Shared: .NET 6+ lock-free 스레드 안전 전역 인스턴스.
        double rand = Random.Shared.NextDouble();

        if (rand < _abandonRate)
        {
            // ── graceful-abandon: pay 없이 즉시 반환 ──────────────────────────────
            // SoakClient await using 종료 → DisposeAsync → graceful TCP FIN
            // 서버: IClientConnection.Dispose → ReleaseAllByContext → 즉시 슬롯 반납 (TTL 무관).
            _stats.IncAbandonCycles();
            // 반환: SoakClient가 graceful FIN
        }
        else if (rand < _abandonRate + _expireRate)
        {
            // ── ttl-expire: TTL + margin 동안 idle 보유 ────────────────────────────
            // 서버 스위퍼(~1s 주기)가 TTL 초과 예약을 만료 → totalExpired++, reserved→0.
            // 중요: ttlSeconds < IdleTimeoutSeconds 이어야 idle-kick 전에 만료 가능.
            //       (SoakOptions에서 ttl 기본값 5s, 서버 IdleTimeout 기본 30s → 안전)
            await Task.Delay(_ttlMs + 500, ct); // TTL + 500ms 여유
            _stats.IncExpireCycles();
            // 반환: SoakClient가 graceful FIN (이미 만료된 슬롯 → ReleaseAllByContext ABA-safe no-op)
        }
        else
        {
            // ── pay 사이클: 결제 후 FIN ────────────────────────────────────────────
            await Task.Delay(10, ct); // 예약 처리 소폭 대기 (fire-and-pace 안전 마진)

            // TicketPayRequestPacket(Id=14): 본문 0B — 세션 컨텍스트(Slots[])로 서버가 결제 처리.
            // _payBuf: 4B 고정, 모든 사이클 재사용.
            await conn.SendAsync(_payBuf, ct);
            _stats.IncSent();
            _stats.IncPaySent();

            // CRITICAL: PaymentDelayMs + margin 대기 후 FIN.
            // 조기 FIN → 서버 DisposeAsync → OCE → ReleaseAll(ConfirmAll 전) → confirmed 거짓 하락 → KpiConservation 오판정.
            await Task.Delay(_paymentDelayMs + 100, ct);
        }
    }
}
