using ServerLib.Core;
using ServerLib.Core.Transport;
using ServerLib.Interface;

namespace ServerLib;

/// <summary>
/// ServerLib의 네트워크 구성 요소(서버 리스너·클라이언트 연결·세션 레지스트리)를 생성하는 공개 진입점(팩토리)입니다.
/// </summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description>구체 구현 타입(<c>SocketPipelineListener</c>·<c>SocketPipelineClient</c>·<c>SessionRegistry</c>)은 <see langword="internal"/>로 은닉됩니다.
/// 외부 소비자는 이 팩토리가 반환하는 인터페이스(<see cref="IServerListener"/>·<see cref="IClientConnection"/>·<see cref="ISessionRegistry"/>)로만 라이브러리를 사용합니다.</description></item>
/// <item><description>이를 통해 구현 세부사항 변경이 소비자 코드에 파급되지 않습니다(캡슐화).</description></item>
/// </list>
/// </remarks>
public static class ServerNet
{
    /// <summary>
    /// TCP 서버 리스너를 생성합니다.
    /// </summary>
    /// <param name="registry">
    /// 활성 세션을 추적할 레지스트리입니다. <see langword="null"/>(기본값)이면 세션 추적을 사용하지 않습니다.
    /// 반드시 <see cref="CreateSessionRegistry"/>가 반환한 인스턴스를 전달해야 합니다(내부적으로 등록 인터페이스가 함께 필요함).
    /// </param>
    /// <returns>아직 시작되지 않은(<see cref="IServerListener.Start(int)"/> 호출 전) 서버 리스너입니다.</returns>
    /// <remarks>
    /// <b>[사용 순서:]</b> 반환된 리스너에 콜백(<see cref="IServerListener.OnReceived"/> 등)과 옵션을 설정한 뒤 <see cref="IServerListener.Start(int)"/>를 호출합니다.
    /// 콜백·옵션은 <see cref="IServerListener.Start(int)"/> 호출 전에 설정해야 합니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> 팩토리 호출 자체는 thread-safe하나, 반환된 리스너의 설정·시작은 단일 스레드에서 수행하십시오.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 리스너 인스턴스 1회 할당.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 즉시 반환합니다(accept 루프는 <see cref="IServerListener.Start(int)"/> 시점에 시작).
    /// </remarks>
    public static IServerListener CreateListener(ISessionRegistry? registry = null)
        // SessionRegistry는 ISessionRegistry와 ISessionRegistrar를 동시 구현한다.
        // 리스너는 세션 등록/해제를 위해 등록 인터페이스(ISessionRegistrar)가 필요하므로 그쪽으로 캐스팅해 전달한다.
        // registry는 CreateSessionRegistry() 산출물이어야 캐스팅이 성립한다(아니면 null로 간주되어 세션 추적 비활성).
        => new SocketPipelineListener(registry as ISessionRegistrar);

    /// <summary>
    /// 원격 서버에 연결하는 클라이언트 연결 객체를 생성합니다.
    /// </summary>
    /// <returns>아직 연결되지 않은(<see cref="IClientConnection.ConnectAsync"/> 호출 전) 클라이언트 연결입니다.</returns>
    /// <remarks>
    /// <b>[사용 순서:]</b> 콜백(<see cref="IClientConnection.OnReceived"/> 등)과 옵션을 설정한 뒤 <see cref="IClientConnection.ConnectAsync"/>를 호출합니다.
    /// <see cref="IClientConnection"/>은 <see cref="System.IAsyncDisposable"/>을 구현하므로 <c>await using</c> 패턴 사용을 권장합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 연결 객체 1회 할당(소켓·파이프 내부 구조는 <see cref="IClientConnection.ConnectAsync"/> 시점에 생성).
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 즉시 반환합니다.
    /// </remarks>
    public static IClientConnection CreateClient()
        => new SocketPipelineClient();

    /// <summary>
    /// 활성 세션을 추적하는 세션 레지스트리를 생성합니다.
    /// </summary>
    /// <returns>읽기 인터페이스로서의 세션 레지스트리입니다. <see cref="CreateListener"/>에 그대로 전달해 서버 세션 추적에 연결할 수 있습니다.</returns>
    /// <remarks>
    /// <b>[사용 패턴:]</b> 반환값을 <see cref="CreateListener"/>의 인자로 넘기면 리스너가 세션 등록/해제를 수행하고,
    /// 동일 인스턴스로 <see cref="ISessionRegistry.GetAll"/>·<see cref="ISessionRegistry.Count"/>·<see cref="ISessionRegistry.BroadcastAsync"/>를 사용할 수 있습니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> 반환된 레지스트리의 모든 멤버는 thread-safe합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 레지스트리 인스턴스 1회 할당.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 즉시 반환합니다.
    /// </remarks>
    public static ISessionRegistry CreateSessionRegistry()
        => new SessionRegistry();
}
