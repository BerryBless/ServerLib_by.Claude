using ServerLib.Interface;

namespace ServerLib.Core;

/// <summary>
/// <see cref="ISession.Context"/>(object?)를 캐스팅 없이 타입 안전하게 읽는 확장 메서드입니다.
/// </summary>
/// <remarks>
/// <b>[레이어]</b> 확장 메서드라 <see cref="ISession"/> 구현체를 바꾸지 않으며, 의존성 방향(Core→Interface)을 지킨다.
/// <b>[Thread Safety]</b> 기반 <see cref="ISession.Context"/>의 Volatile read를 그대로 통과(Thread-safe).
/// <b>[Memory Allocation]</b> 캐스팅만 수행(참조 타입 Zero-allocation). 값 타입 컨텍스트는 기존 object? 저장 모델상 박싱이 유지된다.
/// <b>[Blocking]</b> Non-blocking.
/// </remarks>
public static class SessionContextExtensions
{
    /// <summary>세션 컨텍스트를 <typeparamref name="T"/>로 읽습니다. 미설정이거나 타입이 다르면 <c>default</c>를 반환합니다(예외 없음).</summary>
    /// <typeparam name="T">기대하는 컨텍스트 타입.</typeparam>
    /// <param name="session">대상 세션.</param>
    /// <returns><see cref="ISession.Context"/>가 <typeparamref name="T"/>이면 그 값, 아니면 <c>default</c>.</returns>
    public static T? GetContext<T>(this ISession session)
        => session.Context is T t ? t : default;

    /// <summary>세션 컨텍스트가 <typeparamref name="T"/>로 존재하는지 확인합니다. "미설정"과 "기본값(default)"을 구분해야 할 때 사용합니다.</summary>
    /// <typeparam name="T">기대하는 컨텍스트 타입.</typeparam>
    /// <param name="session">대상 세션.</param>
    /// <param name="value">존재하면 컨텍스트 값, 아니면 <c>default</c>.</param>
    /// <returns>컨텍스트가 <typeparamref name="T"/>이면 <see langword="true"/>, 아니면 <see langword="false"/>.</returns>
    public static bool TryGetContext<T>(this ISession session, out T value)
    {
        if (session.Context is T t) { value = t; return true; }
        value = default!;
        return false;
    }
}
