**기존 증감 패킷을 재사용하고, `Interlocked`로 공유 카운터를 갱신하는 서버·클라이언트 예제를 추가한다.** 최종 검증은 **모든 연결의 처리 완료 확인 → 최종 조회** 순서로 수행한다.

기준 HEAD는 `3aa7a80205ae84f96f6fe780a3cbde255178faf0`과 일치한다. 다만 현재 미추적 `.claude/settings.local.json`이 있어 워킹트리가 완전히 깨끗하지는 않다. 파일 수정이나 테스트 실행은 하지 않았다.

**1. 설계 결정과 코드 근거**

| 항목 | 결정 | 이유 |
|---|---|---|
| 증감 명령 | 기존 `IncrementPacket`·`DecrementPacket` 재사용 | 각각 Id=3·4, 본문 없는 ±1 명령이 요구사항에 맞음 |
| 공유 상태 | 서버 인스턴스당 `long` 필드 1개, 초기값 0 | 세션마다 별도 카운터를 만들지 않음 |
| 갱신 | `Interlocked.Increment`·`Decrement` | 여러 세션이 같은 메모리를 직접 갱신하면서 정확성 보장 |
| 조회 | `CounterQueryPacket` Id=18, `CounterValuePacket` Id=19 추가 | 기존 프레임 형식 안에서 처리 완료 확인과 값 조회 |
| 실행 구성 | `CounterServer`, `CounterClient` | 기존 에코 예제처럼 별도 콘솔 프로세스 |
| 바인딩 | `127.0.0.1:9300` | 제시된 기존 예제 포트와 분리 |
| 테스트 | 새 `CounterExample.Tests` | 에코 테스트의 이름·책임을 유지하고 카운터 검증을 독립 실행 |

기존 패킷 정의는 [IncrementPacket.cs:5](E:/project/ClaudeCodeStudy/ServerLib/Core/Serialization/Packets/IncrementPacket.cs:5), [DecrementPacket.cs:5](E:/project/ClaudeCodeStudy/ServerLib/Core/Serialization/Packets/DecrementPacket.cs:5)에서 확인했다. 수량 필드는 추가하지 않는다.

서버는 팩토리와 인터페이스를 사용하는 [EchoServer/Program.cs:38](E:/project/ClaudeCodeStudy/EchoServer/Program.cs:38)의 구성을 따른다. 참고로 컨텍스트의 송신 확장 메서드 이름은 실제로 **`PacketSendExtensions.SendAsync<T>`**다. [PacketSendExtensions.cs:33](E:/project/ClaudeCodeStudy/ServerLib/Core/Serialization/PacketSendExtensions.cs:33)

**최종값 검증 절차**

1. 서버를 새로 시작한다. 검증 중에는 클라이언트 실행 하나만 해당 서버를 사용한다.
2. 클라이언트가 작업 연결 8개를 만들고, 모두 연결된 뒤 공통 비동기 시작 신호를 해제한다.
3. 연결별로 더하기 1,000회·빼기 750회를 송신한다. 연결마다 순서를 달리해 증감 유입이 겹치도록 한다. 각 연결 안에서는 송신을 순차 `await`한다.
4. 각 연결은 자신의 마지막 증감 명령 뒤에 조회 요청을 보내고 응답을 기다린다. 이 응답은 **그 연결의 앞선 명령이 모두 처리되었다는 확인**이다.
5. 모든 연결이 확인 응답을 받은 뒤, 연결 하나에서 다시 조회한다.
6. 최종값 `2,000 = 8 × (1,000 − 750)`을 검증하고 총 N·M·기대값·실제값·PASS/FAIL을 출력한다.

이 절차의 근거는 세션 수신 루프가 패킷별 콜백을 순차 `await`한다는 점이다. [SocketPipelineSession.cs:190](E:/project/ClaudeCodeStudy/ServerLib/Core/Transport/SocketPipelineSession.cs:190)

**송신 완료만 기다린 뒤 바로 조회하거나, 각 연결의 중간 조회값 중 마지막 도착값을 최종값으로 사용해서는 안 된다.** 다른 연결의 처리가 아직 남아 있을 수 있다. 조회 응답은 해당 시점의 값일 뿐이다.

조회 요청은 본문 0바이트, 응답은 `long Value` 8바이트로 한다. 연결당 미완료 조회를 하나로 제한하므로 요청 ID는 필요 없다. 기존 4바이트 헤더를 그대로 사용한다. [PacketPool.cs:45](E:/project/ClaudeCodeStudy/ServerLib/Core/Memory/PacketPool.cs:45)

**2. 변경 파일 목록**

| 파일 | 변경 요지 |
|---|---|
| `ServerLib/Core/Serialization/Packets/CounterQueryPacket.cs` | Id=18, 본문 없는 조회 struct 및 상세 XML 주석 |
| `ServerLib/Core/Serialization/Packets/CounterValuePacket.cs` | Id=19, `long Value` 직렬화·역직렬화 및 상세 XML 주석 |
| `CounterServer/CounterServer.csproj` | .NET 10 콘솔, ServerLib 참조, 테스트 어셈블리에만 내부 접근 허용 |
| `CounterServer/CounterState.cs` | 내부 클래스. 공유 필드와 원자적 증감·조회 |
| `CounterServer/CounterHandler.cs` | 내부 클래스. 헤더 검증, 패킷 분기, 상태 갱신·조회 응답 |
| `CounterServer/Program.cs` | 팩토리 생성, 콜백 등록, 루프백 시작, 오류 출력, 종료 시 `Stop()` |
| `CounterClient/CounterClient.csproj` | .NET 10 콘솔, ServerLib 참조, 테스트 어셈블리에만 내부 접근 허용 |
| `CounterClient/CounterScenario.cs` | 내부 실행기. 다중 연결, 시작 신호, 처리 완료 확인, 최종 검증·정리 |
| `CounterClient/Program.cs` | 학습용 설정 상수, 실행기 호출, 결과 출력과 종료 코드 |
| `CounterExample.Tests/CounterExample.Tests.csproj` | 기존과 동일한 xUnit 패키지 버전, 서버·클라이언트·ServerLib 참조 |
| `CounterExample.Tests/CounterPacketTests.cs` | 패킷 ID·본문 길이·직렬화 검증 |
| `CounterExample.Tests/CounterStateTests.cs` | 실제 상태 객체의 동시 갱신 정확성 검증 |
| `CounterExample.Tests/CounterEndToEndTests.cs` | 실제 핸들러·클라이언트 실행기를 이용한 루프백 검증 및 실패 경로 |
| `ClaudeCodeStudy.sln` | 신규 프로젝트 3개와 빌드 구성 등록 |
| `plan/contention_counter_0910.md` | 배경, 대안 비교, 구조, API 사용 예, 변경 파일, 검증 명령, 확장 후보 기록 |

설계 문서 작성은 구현 단계에 수행한다. 이번 응답에서는 “파일을 수정하지 말라”는 지시를 따른다. 문서 규칙 근거: [AGENTS.md:46](E:/project/ClaudeCodeStudy/AGENTS.md:46).

**3. 공개 API 영향**

- **추가:** `CounterQueryPacket`, `CounterValuePacket`과 해당 `IPacket` 구현 멤버.
- **기존 API 변경 없음:** 증감 패킷의 ID·본문, Transport, Interface, 팩토리, 프레이밍은 유지한다.
- 카운터 상태·핸들러·시나리오는 예제 프로젝트의 `internal` 구현으로 둔다. 테스트 접근을 위해 ServerLib 내부를 추가 공개하지 않는다.
- 의존성은 예제 → ServerLib이며, ServerLib가 예제를 참조하지 않는다.

**4. 동시성·메모리 정책**

- 모든 세션이 **동일한 `CounterState` 인스턴스**를 참조한다. 필드는 `static`으로 만들지 않아 테스트와 서버 인스턴스를 격리한다.
- 증감은 원자적 연산, 조회는 `Interlocked.Read`를 사용한다. 카운터 갱신을 단일 소비자 큐로 직렬화하지 않는다.
- 콜백은 세션별 수신 루프에서 실행되고, 서로 다른 세션 사이에는 동시 호출될 수 있다고 취급한다. 실제 CPU 실행의 겹침 정도는 스케줄러에 좌우되며 성능 수치로 보장하지 않는다.
- 증감 경로는 동기 갱신 후 `ValueTask.CompletedTask`를 반환한다. 패킷마다 로그나 작업 Task를 만들지 않는다.
- 증감 프레임 두 개는 실행 시작 시 한 번 직렬화한 작은 배열로 보관하고, 변경하지 않은 채 재사용한다.
- 조회 응답은 기존 송신 확장을 사용한다. 이 확장은 비동기 실패 시에도 `finally`에서 풀 버퍼를 반환한다. [PacketSendExtensions.cs:88](E:/project/ClaudeCodeStudy/ServerLib/Core/Serialization/PacketSendExtensions.cs:88)
- 수신 메모리는 콜백 안에서 검증·역직렬화하고 외부에 보관하지 않는다. 대기자에는 `long` 값만 전달한다.
- 조회 대기와 시작 신호는 `TaskCompletionSource`의 `RunContinuationsAsynchronously`를 사용한다. 연결별 조회 대기자는 송신 전에 게시하고, 응답·연결 종료 경합은 `Interlocked`와 `TrySet…`으로 처리한다.
- 연결·Task·대기자·배열의 초기 할당과 비동기 송신의 할당은 허용한다. 전체 실행에 무할당을 선언하지 않는다.

신규 public 멤버에는 Thread Safety·버퍼 소유권 및 생명주기·할당·Blocking을 XML `<remarks>`에 명시한다. 내부 예제 메서드에도 같은 설명 수준을 적용한다. 공유 필드, 인터페이스 연결, `Memory`/`Span`, `ValueTask`, 대기자 선언에는 실제 내부 동작에 근거한 인라인 주석을 작성한다. [AGENTS.md:93](E:/project/ClaudeCodeStudy/AGENTS.md:93), [AGENTS.md:124](E:/project/ClaudeCodeStudy/AGENTS.md:124)

**5. 예외·실패 경로**

- **잘못된 패킷:** ID, 선언된 본문 길이, 실제 길이를 먼저 검증한다. 증감·조회는 정확히 0바이트, 값 응답은 정확히 8바이트만 허용한다. 알 수 없는 ID·잘못된 방향의 패킷은 해당 연결의 오류로 처리한다.
- **검증을 별도로 하는 이유:** 현재 `Deserialize<T>`는 헤더를 건너뛰고 본문을 읽으며, 타입 ID 일치나 본문 전체 소비를 검사하지 않는다. [BinaryPacketSerializer.cs:59](E:/project/ClaudeCodeStudy/ServerLib/Core/Serialization/BinaryPacketSerializer.cs:59)
- **서버 처리 오류:** 카운터 변경 전에 검증을 마친다. 예외는 기존 세션 오류 처리로 전달하며 다른 세션은 계속 동작한다. [SocketPipelineSession.cs:199](E:/project/ClaudeCodeStudy/ServerLib/Core/Transport/SocketPipelineSession.cs:199)
- **연결·송신 실패 또는 중도 종료:** 해당 조회 대기를 실패시키고 전체 시나리오를 취소한다. 이미 적용된 증감은 롤백하지 않으며 자동 재송신하지 않는다. 재시도하면 중복 적용 여부를 알 수 없기 때문이다.
- **무응답:** 연결·송신·조회와 전체 실행에 유한한 타임아웃을 둔다. 단순히 `WaitAsync`만 종료하지 않고 실제 작업 취소와 연결 정리까지 수행한다.
- **부분 초기화 실패:** 이미 생성한 연결도 모두 dispose한다. 서버는 `finally`에서 `Stop()`한다.
- **검증 결과:** 일치 시 종료 코드 0, 불일치·통신 오류·타임아웃 시 비영 코드. 일부 작업 실패 후 PASS를 출력하지 않는다.
- **실행 전제:** 새 서버에서 단일 검증 실행을 수행한다. 재실행은 서버 재시작을 안내한다. 기본 작업량은 `long` 범위보다 충분히 작고, N·M·기대값 계산은 `checked`로 한다.

**6. 테스트 전략**

기존 에코 테스트는 그대로 유지한다. 임시 포트와 유한 대기를 사용하는 패턴은 [EchoEndToEndTests.cs:39](E:/project/ClaudeCodeStudy/EchoExample.Tests/EchoEndToEndTests.cs:39), [EchoEndToEndTests.cs:51](E:/project/ClaudeCodeStudy/EchoExample.Tests/EchoEndToEndTests.cs:51)을 따른다.

신규 테스트는 다음을 검증한다.

- **패킷:** 증감 ID=3·4와 본문 0, 조회 ID=18, 응답 ID=19와 본문 8, 응답의 0·음수·`long` 경계값 왕복.
- **상태:** 여러 작업이 같은 실제 `CounterState`를 갱신한 뒤 N−M 확인. 더하기만·빼기만·불균형 혼합을 포함해 상쇄에 의한 결함 은폐를 줄인다.
- **실제 통신:** 초기값 0, 단일 연결 증감, 여러 연결의 처리 완료 확인 후 최종 조회, 총 연산 0인 경우.
- **완료 확인 절차:** 한 연결의 마지막 명령을 테스트용 비동기 신호로 지연시켜, 송신 완료만으로 검증이 끝나지 않음을 확인한다. 고정 `Sleep`에 의존하지 않는다.
- **실패:** 잘못된 본문이 상태를 바꾸지 않음, 오류 연결 이후 정상 연결 사용 가능, 조회 중 서버 종료, 무응답 타임아웃.
- **코드 재사용:** 테스트에 서버·클라이언트 알고리즘을 복제하지 않고 실제 내부 핸들러와 실행기를 호출한다.
- **정리:** 실패하더라도 모든 작업을 관찰하고 연결·리스너를 정리한다. 임시 포트 재바인딩 충돌은 시작 단계에서만 제한적으로 재시도한다.

구현 후 실행 명령:

```powershell
dotnet build ClaudeCodeStudy.sln -c Release
dotnet test CounterExample.Tests/CounterExample.Tests.csproj -c Release --no-build
dotnet test ClaudeCodeStudy.sln -c Release --no-build
```

별도 터미널에서 수동 예제도 확인한다.

```powershell
dotnet run --project CounterServer -c Release --no-build
dotnet run --project CounterClient -c Release --no-build
```

기존 14개 통과는 제공된 기준 정보이며, 이번 계획 단계에서 재검증한 결과는 아니다.

**7. 단계별 구현 순서**

1. 패킷 ID 충돌을 재확인하고 조회 요청·응답 및 직렬화 테스트 추가.
2. `CounterState`와 원자적 갱신 테스트 구현.
3. 길이 검증을 포함한 실제 서버 핸들러와 `CounterServer/Program.cs` 구현.
4. 다중 연결·시작 신호·연결별 완료 확인·최종 조회를 갖춘 클라이언트 구현.
5. 실제 구현을 대상으로 E2E·타임아웃·연결 종료 테스트 추가.
6. 신규 프로젝트를 솔루션에 등록하고 주석·설계 문서 작성.
7. Release 빌드, 신규 테스트, 전체 테스트, 콘솔 예제 순서로 검증.

**8. 비범위**

- 인증·모니터링·티켓팅·웹 UI·DB 연동.
- 새 Transport, 프레임 형식 변경, 기존 패킷 수량 필드 추가.
- 벤치마크, 처리량 목표, CPU 경합 계측.
- 의도적으로 잘못된 `counter++` 모드와 비결정적 실패 시연.
- 실행 ID, 다중 검증 실행 격리, 원격 리셋, 재전송 중복 제거, 영속화.