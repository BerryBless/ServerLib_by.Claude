# Session Registry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 활성 세션을 중앙에서 추적하는 `ISessionRegistry` / `SessionRegistry`를 추가하고, `SocketPipelineListener`가 자동으로 등록·해제하도록 연동한다.

**Architecture:** `ISessionRegistry`는 `ServerLib/Interface/`에, 구현체 `SessionRegistry`는 `ServerLib/Core/`에 위치한다. `ConcurrentDictionary<Guid, ISession>`로 Lock-free 관리. `SocketPipelineListener`에 `Registry` 프로퍼티를 추가해 optional 연동한다.

**Tech Stack:** .NET 10, xUnit 2.9.0, `System.Collections.Concurrent`, `System.Threading.Tasks`

---

## File Map

| 경로 | 유형 | 역할 |
|------|------|------|
| `ServerLib.Tests/ServerLib.Tests.csproj` | 신규 | xUnit 테스트 프로젝트 |
| `ServerLib.Tests/Stubs/StubSession.cs` | 신규 | ISession 테스트 스텁 |
| `ServerLib.Tests/SessionRegistryTests.cs` | 신규 | SessionRegistry 단위 테스트 |
| `ServerLib/Interface/ISessionRegistry.cs` | 신규 | 레지스트리 인터페이스 |
| `ServerLib/Core/SessionRegistry.cs` | 신규 | ConcurrentDictionary 기반 구현체 |
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | `Registry` 프로퍼티 + 자동 등록/해제 |
| `Server/Program.cs` | 수정 | 레지스트리 사용 예제 |

---

## Task 1: 테스트 프로젝트 생성

**Files:**
- Create: `ServerLib.Tests/ServerLib.Tests.csproj`
- Create: `ServerLib.Tests/Stubs/StubSession.cs`

- [ ] **Step 1: csproj 파일 생성**

`ServerLib.Tests/ServerLib.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>ServerLib.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ServerLib\ServerLib.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 솔루션에 추가**

```bash
dotnet sln ClaudeCodeStudy.sln add ServerLib.Tests/ServerLib.Tests.csproj
```

- [ ] **Step 3: StubSession 스텁 작성**

`ServerLib.Tests/Stubs/StubSession.cs`:
```csharp
using System.Net;
using ServerLib.Interface;

namespace ServerLib.Tests.Stubs;

internal sealed class StubSession : ISession
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public EndPoint? RemoteEndPoint => null;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }
    public bool ThrowOnSend { get; init; }
    public List<byte[]> SentBuffers { get; } = new();

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSend) throw new ObjectDisposedException(nameof(StubSession));
        SentBuffers.Add(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 4: 빌드 확인**

```bash
dotnet build ServerLib.Tests
```
Expected: `Build succeeded.`

---

## Task 2: 실패하는 테스트 작성

**Files:**
- Create: `ServerLib.Tests/SessionRegistryTests.cs`

이 단계에서는 `ISessionRegistry`와 `SessionRegistry`가 아직 없으므로 **컴파일 오류**가 발생해야 정상이다.

- [ ] **Step 1: 테스트 파일 작성**

`ServerLib.Tests/SessionRegistryTests.cs`:
```csharp
using ServerLib.Core;
using ServerLib.Interface;
using ServerLib.Tests.Stubs;

namespace ServerLib.Tests;

public sealed class SessionRegistryTests
{
    [Fact]
    public void Count_WhenEmpty_ReturnsZero()
    {
        var registry = new SessionRegistry();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Register_SingleSession_CountBecomesOne()
    {
        var registry = new SessionRegistry();
        registry.Register(new StubSession());
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Unregister_RegisteredSession_CountBecomesZero()
    {
        var registry = new SessionRegistry();
        var session = new StubSession();
        registry.Register(session);

        registry.Unregister(session.SessionId);

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Unregister_UnknownId_DoesNotThrow()
    {
        var registry = new SessionRegistry();
        var act = () => registry.Unregister(Guid.NewGuid());
        var ex = Record.Exception(act);
        Assert.Null(ex);
    }

    [Fact]
    public void TryGet_RegisteredSession_ReturnsTrueAndSameInstance()
    {
        var registry = new SessionRegistry();
        var session = new StubSession();
        registry.Register(session);

        bool found = registry.TryGet(session.SessionId, out var result);

        Assert.True(found);
        Assert.Same(session, result);
    }

    [Fact]
    public void TryGet_UnknownId_ReturnsFalse()
    {
        var registry = new SessionRegistry();
        bool found = registry.TryGet(Guid.NewGuid(), out _);
        Assert.False(found);
    }

    [Fact]
    public void GetAll_TwoRegisteredSessions_ReturnsBoth()
    {
        var registry = new SessionRegistry();
        var s1 = new StubSession();
        var s2 = new StubSession();
        registry.Register(s1);
        registry.Register(s2);

        var all = registry.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(s1, all);
        Assert.Contains(s2, all);
    }

    [Fact]
    public void GetAll_IsSnapshot_LaterRegistrationNotReflected()
    {
        var registry = new SessionRegistry();
        registry.Register(new StubSession());

        var snapshot = registry.GetAll();   // 1개 시점 스냅샷
        registry.Register(new StubSession()); // 이후 추가

        Assert.Single(snapshot);  // 스냅샷은 이전 상태 유지
    }

    [Fact]
    public async Task BroadcastAsync_TwoSessions_BothReceiveData()
    {
        var registry = new SessionRegistry();
        var s1 = new StubSession();
        var s2 = new StubSession();
        registry.Register(s1);
        registry.Register(s2);
        var data = new byte[] { 0x01, 0x02, 0x03 };

        await registry.BroadcastAsync(data);

        Assert.Single(s1.SentBuffers);
        Assert.Equal(data, s1.SentBuffers[0]);
        Assert.Single(s2.SentBuffers);
        Assert.Equal(data, s2.SentBuffers[0]);
    }

    [Fact]
    public async Task BroadcastAsync_OneSessionThrows_OtherSessionStillReceives()
    {
        var registry = new SessionRegistry();
        var good = new StubSession();
        var bad  = new StubSession { ThrowOnSend = true };
        registry.Register(good);
        registry.Register(bad);

        // 예외가 전파되지 않아야 한다
        await registry.BroadcastAsync(new byte[] { 1 });

        Assert.Single(good.SentBuffers);
    }
}
```

- [ ] **Step 2: 빌드 오류 확인 (Red)**

```bash
dotnet build ServerLib.Tests
```
Expected: `error CS0246: 'SessionRegistry' 형식 또는 네임스페이스 이름을 찾을 수 없습니다.`

---

## Task 3: ISessionRegistry 인터페이스 구현

**Files:**
- Create: `ServerLib/Interface/ISessionRegistry.cs`

- [ ] **Step 1: 인터페이스 파일 작성**

`ServerLib/Interface/ISessionRegistry.cs`:
```csharp
namespace ServerLib.Interface;

/// <summary>서버에 연결된 활성 세션 전체를 추적하는 레지스트리입니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. 모든 멤버는 동시 호출 안전합니다.
/// <br/><br/>
/// <b>[설계 원칙:]</b> <see cref="Register"/>와 <see cref="Unregister"/>는
/// <c>SocketPipelineListener</c> 내부에서만 호출됩니다.
/// 외부 코드는 <see cref="TryGet"/>, <see cref="GetAll"/>, <see cref="BroadcastAsync"/>만 사용하십시오.
/// </remarks>
public interface ISessionRegistry
{
    /// <summary>현재 연결된 세션 수입니다.</summary>
    /// <remarks><b>[Thread Safety:]</b> Thread-safe.</remarks>
    int Count { get; }

    /// <summary>SessionId로 특정 세션을 조회합니다.</summary>
    /// <param name="sessionId">조회할 세션의 고유 식별자</param>
    /// <param name="session">조회된 세션. 존재하지 않으면 <see langword="null"/>.</param>
    /// <returns>세션이 존재하면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe.
    /// <b>[Memory Allocation:]</b> Zero-allocation.
    /// </remarks>
    bool TryGet(Guid sessionId, out ISession? session);

    /// <summary>현재 활성 세션 전체의 스냅샷을 반환합니다.</summary>
    /// <returns>호출 시점의 활성 세션 배열 (이후 변경이 반영되지 않는 스냅샷)</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe.
    /// 반환 후 세션이 추가·제거되어도 반환된 컬렉션에는 반영되지 않습니다.
    /// </remarks>
    IReadOnlyCollection<ISession> GetAll();

    /// <summary>현재 활성 세션 전체에 동일 메시지를 병렬로 전송합니다.</summary>
    /// <param name="data">전송할 데이터 버퍼. 메서드가 완료될 때까지 유효해야 합니다.</param>
    /// <param name="cancellationToken">전송 취소 토큰</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe.
    /// <br/><br/>
    /// <b>[Error Handling:]</b> 개별 세션 전송 실패(<see cref="ObjectDisposedException"/>,
    /// <see cref="System.Net.Sockets.SocketException"/>)는 무시됩니다.
    /// 한 세션 실패가 나머지 브로드캐스트를 중단시키지 않습니다.
    /// </remarks>
    ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>세션을 레지스트리에 등록합니다.</summary>
    /// <remarks>
    /// <b>[사용 제한:]</b> <c>SocketPipelineListener</c> 내부 전용입니다.
    /// 외부에서 직접 호출하지 마십시오.
    /// </remarks>
    void Register(ISession session);

    /// <summary>세션을 레지스트리에서 제거합니다.</summary>
    /// <remarks>
    /// <b>[사용 제한:]</b> <c>SocketPipelineListener</c> 내부 전용입니다.
    /// 존재하지 않는 <paramref name="sessionId"/>를 전달해도 예외가 발생하지 않습니다.
    /// </remarks>
    void Unregister(Guid sessionId);
}
```

---

## Task 4: SessionRegistry 구현 + Green

**Files:**
- Create: `ServerLib/Core/SessionRegistry.cs`

- [ ] **Step 1: 구현체 작성**

`ServerLib/Core/SessionRegistry.cs`:
```csharp
using System.Collections.Concurrent;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core;

/// <summary><see cref="ISessionRegistry"/>의 <see cref="ConcurrentDictionary{TKey,TValue}"/> 기반 구현체입니다.</summary>
public sealed class SessionRegistry : ISessionRegistry
{
    private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();

    /// <inheritdoc/>
    public int Count => _sessions.Count;

    /// <inheritdoc/>
    public bool TryGet(Guid sessionId, out ISession? session)
        => _sessions.TryGetValue(sessionId, out session);

    /// <inheritdoc/>
    public IReadOnlyCollection<ISession> GetAll()
        => _sessions.Values.ToArray();

    /// <inheritdoc/>
    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var snapshot = _sessions.Values.ToArray();
        await Task.WhenAll(snapshot.Select(async s =>
        {
            try { await s.SendAsync(data, cancellationToken); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }));
    }

    /// <inheritdoc/>
    public void Register(ISession session)
        => _sessions[session.SessionId] = session;

    /// <inheritdoc/>
    public void Unregister(Guid sessionId)
        => _sessions.TryRemove(sessionId, out _);
}
```

- [ ] **Step 2: 테스트 실행 (Green)**

```bash
dotnet test ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 10, Skipped: 0`

- [ ] **Step 3: 커밋**

```bash
git add ServerLib/Interface/ISessionRegistry.cs ServerLib/Core/SessionRegistry.cs ServerLib.Tests/
git commit -m "추가: ISessionRegistry 인터페이스 및 SessionRegistry 구현, 단위 테스트"
```

---

## Task 5: SocketPipelineListener에 Registry 연동

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineListener.cs`

- [ ] **Step 1: `Registry` 프로퍼티 추가 및 AcceptLoopAsync 수정**

`SocketPipelineListener.cs`의 기존 전체 내용을 아래로 교체한다:

```csharp
using System.Net;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

public sealed class SocketPipelineListener : IServerListener
{
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listenSocket != null;
    public Func<ISession, ValueTask>? OnClientConnected { get; set; }
    public Func<ISession, ValueTask>? OnClientDisconnected { get; set; }
    public Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    /// <summary>
    /// 세션 레지스트리입니다. 설정하면 연결/해제 시 자동으로 Register/Unregister됩니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Start() 호출 전에 설정해야 합니다.
    /// 서버 동작 중 교체는 지원하지 않습니다.
    /// </remarks>
    public ISessionRegistry? Registry { get; set; }

    public void Start(int port)
    {
        if (IsRunning) throw new InvalidOperationException("Already running.");

        _cts = new CancellationTokenSource();
        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
        _listenSocket.Listen(backlog: 512);

        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listenSocket?.Dispose();
        _listenSocket = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await _listenSocket!.AcceptAsync(ct);
                ConfigureSocket(clientSocket);
                var session = new SocketPipelineSession(clientSocket);
                session.OnReceived = data => OnReceived?.Invoke(session, data) ?? ValueTask.CompletedTask;
                session.OnDisconnected = async () =>
                {
                    Registry?.Unregister(session.SessionId);
                    if (OnClientDisconnected != null)
                        await OnClientDisconnected(session);
                    await session.DisposeAsync();
                };

                Registry?.Register(session);
                session.StartReceiving();

                if (OnClientConnected != null)
                    await OnClientConnected(session);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) { }
        }
    }

    private static void ConfigureSocket(Socket socket)
    {
        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }
}
```

- [ ] **Step 2: 전체 빌드 확인**

```bash
dotnet build ClaudeCodeStudy.sln
```
Expected: `Build succeeded.`

- [ ] **Step 3: 테스트 재실행 (회귀 없음 확인)**

```bash
dotnet test ServerLib.Tests
```
Expected: `Passed! - Failed: 0`

- [ ] **Step 4: 커밋**

```bash
git add ServerLib/Core/Transport/SocketPipelineListener.cs
git commit -m "수정: SocketPipelineListener에 ISessionRegistry 연동 (Registry 프로퍼티)"
```

---

## Task 6: Server/Program.cs 예제 업데이트

**Files:**
- Modify: `Server/Program.cs`

- [ ] **Step 1: Registry 생성 및 사용 예제 추가**

`Server/Program.cs` 전체를 아래로 교체한다:

```csharp
using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

const int Port = 9000;

var registry = new SessionRegistry();
var metrics = new ServerMetrics();
var listener = new SocketPipelineListener();
listener.Registry = registry;

var test = 0;
long windowPackets = 0;
using var cts = new CancellationTokenSource();

listener.OnClientConnected = session =>
{
    metrics.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  (sessions: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (sessions: {metrics.ConnectedCount})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};

listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return ValueTask.CompletedTask;

    metrics.OnPacketReceived();
    Interlocked.Increment(ref windowPackets);

    if (packetId == IncrementPacket.Id)
        Interlocked.Increment(ref test);
    else if (packetId == DecrementPacket.Id)
        Interlocked.Decrement(ref test);

    return ValueTask.CompletedTask;
};

listener.Start(Port);
Console.WriteLine($"[Server] port {Port} — 증가(Id={IncrementPacket.Id}) / 감소(Id={DecrementPacket.Id}).");
Console.WriteLine($"  Enter: 현재 세션 목록 출력 | 'q'+Enter: 서버 종료");

_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(10000, cts.Token); }
        catch (OperationCanceledException) { break; }

        long count = Interlocked.Exchange(ref windowPackets, 0);
        Console.WriteLine($"[Monitor] sessions={metrics.ConnectedCount}  packets/10s={count:N0}  test={Volatile.Read(ref test)}  registry={registry.Count}");
    }
});

// 입력 루프: Enter → 세션 목록, 'q' → 종료
while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true) break;

    // Enter: 현재 세션 목록 출력
    var sessions = registry.GetAll();
    Console.WriteLine($"[Sessions] count={sessions.Count}");
    foreach (var s in sessions)
        Console.WriteLine($"  {s.SessionId:N}  {s.RemoteEndPoint}  connected={s.ConnectedAt:HH:mm:ss}");
}

cts.Cancel();
listener.Stop();
Console.WriteLine($"종료  total={metrics.TotalPacketsReceived}  final test={test}");
```

- [ ] **Step 2: 빌드 확인**

```bash
dotnet build Server
```
Expected: `Build succeeded.`

- [ ] **Step 3: 커밋**

```bash
git add Server/Program.cs
git commit -m "수정: Server 예제에 SessionRegistry 사용 추가 (세션 목록 조회)"
```

---

## Task 7: 최종 검증

- [ ] **Step 1: 솔루션 전체 빌드**

```bash
dotnet build ClaudeCodeStudy.sln
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 2: 전체 테스트**

```bash
dotnet test ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 10`

- [ ] **Step 3: 수동 동작 확인**

터미널 1:
```bash
dotnet run --project Server
```
터미널 2:
```bash
dotnet run --project Client -- 4 100
```
Server 터미널에서 Enter 입력 → 세션 목록(4개 연결) 출력 확인.
클라이언트 종료 후 Enter 입력 → 세션 목록 비어있음(0개) 확인.
