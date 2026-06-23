namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 티켓 예약·결제 결과 상태 코드입니다.
/// <see cref="TicketResultPacket.Status"/> 필드와 1:1 매핑되는 와이어 프로토콜 열거형입니다.
/// </summary>
/// <remarks>
/// <c>byte</c> 기반 열거형으로 직렬화 시 1바이트를 사용합니다.
/// 클라이언트와 서버 양측이 동일 값을 공유해야 프로토콜 호환성이 유지됩니다.
/// </remarks>
public enum TicketStatus : byte
{
    /// <summary>예약 성공 — 슬롯 점유, 결제 대기 중입니다.</summary>
    Reserved = 0,

    /// <summary>매진 — 예약 가능한 슬롯이 없습니다.</summary>
    SoldOut = 1,

    /// <summary>중복 예약 — 이미 슬롯을 보유하고 있습니다.</summary>
    AlreadyReserved = 2,

    /// <summary>예약 없음 — 결제 요청이 왔으나 예약이 없거나 이미 소비됐습니다.</summary>
    NotReserved = 3,

    /// <summary>결제 성공·티켓 확정입니다.</summary>
    Confirmed = 4,

    /// <summary>결제 실패 — 슬롯이 반납됩니다.</summary>
    PaymentFailed = 5,

    /// <summary>슬롯 반납 완료입니다 (이탈·TTL·결제 실패 공용).</summary>
    Released = 6,

    /// <summary>
    /// 지정 좌석 점유됨 — 요청한 좌석이 이미 Reserved 또는 Sold 상태이거나 좌석 번호가 유효 범위 밖입니다.
    /// 클라이언트는 최신 좌석맵(<see cref="SeatMapRequestPacket"/>·<see cref="SeatMapResponsePacket"/>)을
    /// 재조회하여 다른 빈 좌석을 선택하고 예약을 재시도해야 합니다.
    /// </summary>
    SeatTaken = 7,

    /// <summary>
    /// 예약 요청 속도 제한 초과 — 단시간에 너무 많은 예약 요청이 발생했습니다.
    /// 클라이언트는 예약을 즉시 중단하고 일정 시간 후 재시도해야 합니다.
    /// </summary>
    RateLimited = 8,
}
