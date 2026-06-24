namespace Ticketing;

/// <summary>
/// 더미 로그인 성공 시 세션에 부착되는 티켓팅 컨텍스트입니다.
/// <see cref="ServerLib.Interface.ISession.Context"/>에 저장되며, 이후 모든 티켓 요청 핸들러가
/// <c>session.GetContext&lt;TicketContext&gt;()</c>로 인증 및 예약 상태를 조회합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> <see cref="Slots"/>의 각 원소는 <c>Interlocked</c> 연산으로만 수정합니다.
/// <see cref="Username"/>은 생성 후 불변입니다.
/// </description></item>
/// <item><description>
/// <b>per-element 선형화 앵커:</b> <see cref="Slots"/>의 각 원소가 독립적인 선형화 지점입니다.
/// <c>-1</c>은 "빈 슬롯"을 나타냅니다. <c>0 ~ N-1</c>은 현재 보유 중인 seatId입니다.
/// <br/>
/// 발행(publish): <c>TicketInventory.TryReserveOne</c>이 마지막으로 <c>Volatile.Write(ref Slots[k], seatId)</c>를
/// 호출함으로써 <c>_owners</c>·<c>_reservedAtTicks</c>보다 나중에 가시화됩니다.
/// <br/>
/// 소비(consume): <c>TicketInventory.ConfirmAll</c>·<c>ReleaseAll</c>·<c>ReleaseAllByContext</c>는
/// <c>Interlocked.Exchange(ref Slots[k], -1)</c>로 단 한 번만 소비합니다.
/// <br/>
/// ABA 안전: <c>TicketInventory.SweepExpired</c>는 <c>Interlocked.CompareExchange(ref Slots[k], -1, seatId)</c>를
/// 사용하여 특정 seatId와 일치할 때만 해제합니다. seatId로 비교하므로 재예약 ABA가 발생해도 안전합니다.
/// </description></item>
/// <item><description>
/// <b>직렬 세션 디스패치 가정:</b> 배치 예약(<c>TryReserveBatch</c>)의 "AlreadyReserved" 가드와
/// 빈 슬롯 탐색 루프는 동일 세션의 패킷이 직렬로 처리된다는 가정 위에서 안전합니다.
/// <c>Slots</c> 쓰기(예약)는 이 세션의 단일 생산자이고, 소비는 다른 경로(Confirm·Release·Sweep)입니다.
/// </description></item>
/// <item><description>
/// <b>Memory:</b> <see cref="Slots"/> 배열의 길이는 <c>MaxSeatsPerSession</c>과 동일합니다.
/// 참조 읽기/쓰기는 64비트에서 원자적입니다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class TicketContext
{
    /// <summary>더미 로그인 시 입력된 사용자 이름입니다. 생성 후 불변입니다.</summary>
    public string Username { get; }

    // int[]: 각 원소가 독립적인 CAS 대상. 원소마다 -1(빈 슬롯) 또는 seatId(보유 좌석).
    // Volatile.Write/Interlocked.*: 스위퍼 스레드와의 교차 접근에서 가시성 및 원자성 보장.
    // 배열 참조 자체는 생성 후 불변이므로 readonly 선언 가능.
    /// <summary>
    /// 세션이 현재 보유 중인 좌석 인덱스(seatId) 배열입니다.
    /// 각 원소: <c>-1</c>=빈 슬롯, <c>≥0</c>=보유 중인 seatId.
    /// 길이는 세션당 최대 예약 가능 좌석 수(<c>MaxSeatsPerSession</c>)입니다.
    /// </summary>
    public readonly int[] Slots;

    // long 필드: Environment.TickCount64 기반 슬라이딩 윈도우 시작 시각.
    // Interlocked.CompareExchange(long)으로 CAS 교체 — 64비트에서 정렬 보장.
    public long RateLimitWindowStart;

    // int 필드: 현재 윈도우 내 Reserve 시도 횟수. Interlocked.Increment로 원자적 증가.
    public int RateLimitAttempts;

    /// <summary>속도 제한 윈도우 길이(밀리초). 60초.</summary>
    public const int RateLimitWindowMs = 60_000;

    /// <summary>단일 윈도우 내 허용 최대 Reserve 시도 횟수(배치 요청 단위).</summary>
    public const int MaxReserveAttemptsPerWindow = 10;

    /// <summary>새 티켓팅 컨텍스트를 초기화합니다.</summary>
    /// <param name="username">더미 로그인 시 입력된 사용자 이름입니다.</param>
    /// <param name="maxSeatsPerSession">
    /// 세션당 최대 예약 가능 좌석 수입니다. 기본값 1(단일 좌석, 하위호환).
    /// <c>TicketInventory.TryReserveBatch</c>가 이 값을 상한으로 사용합니다.
    /// </param>
    public TicketContext(string username, int maxSeatsPerSession = 1)
    {
        Username = username;
        // Array.Fill: -1로 초기화 — 모든 슬롯이 "빈 상태"에서 시작함을 명시적으로 보장
        Slots = new int[maxSeatsPerSession];
        Array.Fill(Slots, -1);
    }
}
