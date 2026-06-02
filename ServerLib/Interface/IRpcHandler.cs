namespace ServerLib.Interface;

/// <summary>
/// 수신된 패킷 페이로드를 역직렬화하고 적절한 RPC 핸들러로 디스패치하는 인터페이스입니다.
/// <c>Rpc.Generator</c>의 Source Generator가 <c>[RpcService]</c> 어트리뷰트가 붙은 인터페이스를 분석하여
/// 이 인터페이스의 구현체를 자동 생성합니다.
/// </summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description>패킷 ID는 페이로드의 앞 2바이트에서 읽으며, 나머지가 핸들러에 전달됩니다.</description></item>
/// <item><description>핸들러 룩업은 배열 인덱싱(O(1))으로 수행되어 Dictionary 오버헤드가 없습니다.</description></item>
/// <item><description>등록되지 않은 패킷 ID는 조용히 무시됩니다(예외 미발생).</description></item>
/// </list>
/// </remarks>
public interface IRpcHandler
{
    /// <summary>
    /// 수신된 로우 페이로드를 패킷 ID로 분류하여 해당 RPC 핸들러로 디스패치합니다.
    /// </summary>
    /// <param name="session">패킷을 송신한 클라이언트 세션입니다.</param>
    /// <param name="payload">수신된 원시 바이트 데이터입니다. 앞 2바이트가 패킷 ID입니다.</param>
    /// <param name="cancellationToken">디스패치 및 핸들러 실행 취소 토큰입니다.</param>
    /// <returns>디스패치 및 핸들러 실행이 완료되면 완료되는 <see cref="ValueTask"/>입니다.</returns>
    /// <remarks>
    /// <b>[Thread Context:]</b> 네트워크 I/O 스레드 풀에서 직접 호출됩니다.
    /// 핸들러 내부에서 장시간 동기 블로킹 작업을 수행하면 수신 루프가 정지되므로 금지합니다.
    /// CPU 집약적 작업은 <see cref="System.Threading.Tasks.Task.Run(System.Action)"/>으로 스레드 풀에 위임하세요.
    /// <br/><br/>
    /// <b>[Memory Policy:]</b> <paramref name="payload"/>의 소유권은 이 메서드 실행 동안만 유효합니다.
    /// <see cref="ValueTask"/> 완료 후 <paramref name="payload"/>가 무효화될 수 있습니다.
    /// 핸들러에서 비동기 작업 이후에도 페이로드 데이터가 필요하다면 <c>payload.ToArray()</c>로 복사해야 합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 디스패치 자체는 Zero-allocation입니다.
    /// 개별 핸들러의 역직렬화 과정에서 할당이 발생할 수 있습니다.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 핸들러가 비동기로 구현된 경우 즉시 반환됩니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Thread-safe. 내부 핸들러 배열은 초기화 후 읽기 전용입니다.
    /// </remarks>
    ValueTask DispatchAsync(ISession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
