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
/// <b>Thread Safety:</b> <see cref="SlotIndex"/>는 <c>Interlocked</c> 연산으로만 수정합니다.
/// <see cref="Username"/>은 생성 후 불변입니다.
/// </description></item>
/// <item><description>
/// <b>SlotIndex 선형화 앵커:</b> <c>-1</c>은 "예약 없음"을 나타냅니다.
/// <c>0 ~ N-1</c>은 현재 보유 슬롯 인덱스입니다.
/// <see cref="TicketInventory.Confirm"/> / <see cref="TicketInventory.Release"/> / <see cref="TicketInventory.ReleaseByContext"/>는
/// 모두 <c>Interlocked.Exchange(ref ctx.SlotIndex, -1)</c>을 선형화 지점으로 삼아
/// 단 한 번만 소비됩니다. 두 번째 호출은 항상 <c>-1</c>을 돌려받아 no-op입니다.
/// </description></item>
/// <item><description>
/// <b>Memory:</b> <c>sealed class</c>이므로 참조 읽기/쓰기는 64비트에서 원자적입니다.
/// <see cref="TicketInventory"/>의 <c>_owners[]</c> 배열에 참조로 저장되며, 각 세션마다 고유 인스턴스입니다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class TicketContext
{
    /// <summary>더미 로그인 시 입력된 사용자 이름입니다. 생성 후 불변입니다.</summary>
    public string Username { get; }

    // int 필드: Interlocked.CompareExchange / Exchange의 직접 CAS 대상 — 32bit 정렬, 원자적 접근 보장.
    // volatile 불필요: Interlocked 연산이 full fence 역할을 하므로 가시성이 보장된다.
    /// <summary>현재 예약된 슬롯 인덱스입니다. <c>-1</c>이면 예약 없음입니다.</summary>
    public int SlotIndex = -1;

    /// <summary>새 티켓팅 컨텍스트를 초기화합니다.</summary>
    /// <param name="username">더미 로그인 시 입력된 사용자 이름입니다.</param>
    public TicketContext(string username) => Username = username;
}
