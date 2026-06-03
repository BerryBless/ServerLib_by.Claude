namespace ServerLib.Interface;

/// <summary>세션 레지스트리의 등록/해제 전용 인터페이스입니다. <c>SocketPipelineListener</c> 내부에서만 사용됩니다.</summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description>이 인터페이스는 <c>SocketPipelineListener</c>가 세션 생명주기를 관리하기 위한 전용 진입점입니다.</description></item>
/// <item><description>외부 소비자는 읽기 전용 <see cref="ISessionRegistry"/>만 사용하십시오.</description></item>
/// </list>
/// </remarks>
public interface ISessionRegistrar
{
    /// <summary>세션을 레지스트리에 등록합니다.</summary>
    /// <param name="session">등록할 세션</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    void Register(ISession session);

    /// <summary>세션을 레지스트리에서 제거합니다.</summary>
    /// <param name="sessionId">제거할 세션의 고유 식별자. 존재하지 않아도 예외가 발생하지 않습니다.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    void Unregister(Guid sessionId);
}
