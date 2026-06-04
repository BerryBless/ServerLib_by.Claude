namespace ServerLib.Interface;

/// <summary>
/// 세션의 생명주기 상태를 나타내는 확장 가능한 값 타입입니다.
/// 5개의 predefined 상태를 제공하며, <c>new SessionState(int)</c>로 사용자 정의 상태를 만들 수 있습니다.
/// </summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description><b>Memory Allocation:</b> <c>readonly struct</c>이므로 Zero-allocation(스택)입니다.</description></item>
/// <item><description><b>Thread Safety:</b> 불변값이므로 모든 스레드에서 안전하게 비교 가능합니다.</description></item>
/// </list>
/// </remarks>
public readonly struct SessionState : IEquatable<SessionState>
{
    /// <summary>상태를 나타내는 정수값입니다.</summary>
    public int Value { get; }

    /// <summary>지정된 정수값으로 상태를 생성합니다. 사용자 정의 상태는 5 이상의 값을 권장합니다.</summary>
    /// <param name="value">상태 정수값. 0~4는 predefined 상태로 예약되어 있으므로 사용자 정의 상태는 5 이상을 사용하십시오. 예약값을 전달하면 해당 predefined 상태와 동등(<c>==</c>)하게 취급됩니다.</param>
    public SessionState(int value) => Value = value;

    /// <summary>predefined 상태로 예약된 정수값의 상한(0~4)입니다. 사용자 정의 상태는 이 값을 초과해야 합니다.</summary>
    public const int ReservedMaxValue = 4;

    /// <summary>사용자 정의 상태를 생성합니다. predefined 예약 범위(0~<see cref="ReservedMaxValue"/>)와 충돌하지 않도록 검증합니다.</summary>
    /// <param name="value">5 이상의 사용자 정의 상태값</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/>가 예약 범위(0~4) 이하일 때 발생합니다.</exception>
    public static SessionState Custom(int value)
    {
        if (value <= ReservedMaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"사용자 정의 상태는 {ReservedMaxValue}보다 커야 합니다 (0~{ReservedMaxValue}는 predefined 예약).");
        return new SessionState(value);
    }

    /// <summary>연결 수립 중 (초기 상태). (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Connecting = new(0);
    /// <summary>연결 완료. (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Connected = new(1);
    /// <summary>인증 완료. (앱 레벨 — 소비자 설정 가능)</summary>
    public static readonly SessionState Authenticated = new(2);
    /// <summary>연결 해제 진행 중. (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Disconnecting = new(3);
    /// <summary>연결 해제 완료. (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Disconnected = new(4);

    /// <inheritdoc/>
    public bool Equals(SessionState other) => Value == other.Value;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionState s && Equals(s);
    /// <inheritdoc/>
    public override int GetHashCode() => Value;

    /// <summary>두 상태가 같은지 비교합니다.</summary>
    public static bool operator ==(SessionState a, SessionState b) => a.Value == b.Value;
    /// <summary>두 상태가 다른지 비교합니다.</summary>
    public static bool operator !=(SessionState a, SessionState b) => a.Value != b.Value;

    /// <summary>상태의 이름을 반환합니다. 사용자 정의 상태는 <c>Custom(N)</c> 형식입니다.</summary>
    /// <remarks>predefined 상태값과의 단일 진실 공급원(single source of truth)을 유지하기 위해 static 필드와 직접 비교합니다.</remarks>
    public override string ToString()
    {
        if (this == Connecting) return "Connecting";
        if (this == Connected) return "Connected";
        if (this == Authenticated) return "Authenticated";
        if (this == Disconnecting) return "Disconnecting";
        if (this == Disconnected) return "Disconnected";
        return $"Custom({Value})";
    }
}
