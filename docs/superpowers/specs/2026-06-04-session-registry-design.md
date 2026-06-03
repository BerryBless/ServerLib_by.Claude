# Session Registry 설계 문서

**날짜:** 2026-06-04  
**상태:** 승인됨

---

## 1. 배경 및 목적

현재 `SocketPipelineListener`는 클라이언트 소켓을 accept한 뒤 생성한 `SocketPipelineSession`을 별도로 추적하지 않는다. 서버 코드에서 특정 세션을 찾거나, 전체 세션에 브로드캐스트하려면 애플리케이션 레이어가 직접 딕셔너리를 관리해야 한다.

**해결 목표:**
- 활성 세션 목록을 서버 라이브러리 수준에서 안전하게 관리
- `Guid`로 특정 세션 조회 후 개별 Push
- 연결된 모든 세션에 동일 메시지 브로드캐스트

---

## 2. 설계 결정

| 항목 | 채택안 | 비고 |
|------|--------|------|
| 위치 | 독립 `ISessionRegistry` + `SessionRegistry` | Interface/Core 원칙 준수 |
| 연동 방식 | `SocketPipelineListener.Registry` 프로퍼티 주입 | 리스너가 자동 Register/Unregister |
| 내부 자료구조 | `ConcurrentDictionary<Guid, ISession>` | Lock-free 읽기/쓰기 |
| 브로드캐스트 | `ValueTask` 반환, 병렬 전송 (`Task.WhenAll`) | GC 압력 최소화 |
| 대안 A (이벤트 구독) | 미채택 | 콜백 체이닝 복잡도 높음 |
| 대안 B (Decorator) | 미채택 | 기존 패턴과 이질적 |

---

## 3. 컴포넌트 구조

```
ServerLib/
├── Interface/
│   └── ISessionRegistry.cs           ← NEW
└── Core/
    ├── SessionRegistry.cs             ← NEW
    └── Transport/
        └── SocketPipelineListener.cs  ← MOD

Server/
└── Program.cs                         ← MOD (예제)
```

**의존 관계:**
```
SessionRegistry → ISessionRegistry, ISession   (Core → Interface, 역방향 없음)
SocketPipelineListener → ISessionRegistry      (선택적 주입, null-safe)
```

---

## 4. 핵심 API

### ISessionRegistry

```csharp
public interface ISessionRegistry
{
    int Count { get; }
    bool TryGet(Guid sessionId, out ISession? session);
    IReadOnlyCollection<ISession> GetAll();
    ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    void Register(ISession session);
    void Unregister(Guid sessionId);
}
```

### SocketPipelineListener (추가 프로퍼티)

```csharp
public ISessionRegistry? Registry { get; set; }
// AcceptLoopAsync 내부:
//   연결 시 → Registry?.Register(session)
//   해제 시 → Registry?.Unregister(session.SessionId)
```

### Server/Program.cs 사용 예제

```csharp
var registry = new SessionRegistry();
listener.Registry = registry;

// 특정 세션에 직접 전송
if (registry.TryGet(targetId, out var session))
    await session.SendAsync(data);

// 전체 브로드캐스트
await registry.BroadcastAsync(data);
```

---

## 5. 동시성 및 메모리

- `ConcurrentDictionary` 사용으로 Register/Unregister 락 경합 없음
- `BroadcastAsync`: `GetAll()` 스냅샷 후 `Task.WhenAll`로 병렬 전송, 도중 연결 해제된 세션은 예외 무시
- `Register`/`Unregister`는 `SocketPipelineListener` 내부에서만 호출 — 사용자가 직접 호출할 이유 없음 (인터페이스에 노출하되 문서로 제한)

---

## 6. 변경 파일 목록

| 파일 | 유형 | 내용 |
|------|------|------|
| `ServerLib/Interface/ISessionRegistry.cs` | 신규 | 세션 레지스트리 인터페이스 |
| `ServerLib/Core/SessionRegistry.cs` | 신규 | ConcurrentDictionary 기반 구현체 |
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | `Registry` 프로퍼티 + 자동 등록/해제 |
| `Server/Program.cs` | 수정 | 레지스트리 생성 및 사용 예제 |

---

## 7. 빌드 검증

```bash
dotnet build ServerLib
dotnet run --project Server
```

---

## 8. 향후 확장 포인트

- **세션 그룹/룸** — `ISessionRegistry`를 기반으로 `IRoomRegistry` 확장
- **세션 상태 저장** — `ISession`에 `Tag` 프로퍼티 추가 또는 별도 딕셔너리
- **유휴 타임아웃** — `ConnectedAt` + 백그라운드 타이머로 오래된 세션 강제 해제
