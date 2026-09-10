# 00_context — 경합 카운터 서버 (교차 검증 개발)

## 사용자 요구사항 (원문)
> 간단하게 테스트용 프로젝트로 경합이 일어나는 서버 만들어봐. 예를들면 하나의 변수 더하기 패킷, 빼기 패킷을 만든다던가 하는식

## 해석된 요구사항
- **목적:** 여러 클라이언트가 동시에 하나의 공유 변수(카운터)를 더하기/빼기 패킷으로 갱신하는 **경합(contention) 시연·학습용 예제**. cross-verify 하네스의 첫 실전 검증 대상이기도 하다.
- 서버: 공유 카운터 1개. 더하기 패킷·빼기 패킷 수신 시 카운터 갱신. 다중 세션 동시 유입 = 경합 지점.
- 클라이언트: 여러 스레드/연결이 더하기·빼기를 동시 다발 송신해 경합을 실제로 유발하고, 최종 값이 기대값과 일치하는지 확인할 수 있어야 한다 (경합 처리가 올바르면 결정적 기대값이 나온다는 것이 학습 포인트).
- 규모: **작게.** EchoServer/EchoClient 수준의 학습용 예제 + 테스트.

## 기준 커밋
`3aa7a80205ae84f96f6fe780a3cbde255178faf0` (워킹트리 깨끗)

## 기존 자산 (중요 — 재사용 검토 대상)
- **`IncrementPacket`(Id=3)·`DecrementPacket`(Id=4)이 ServerLib에 이미 존재한다** (`ServerLib/Core/Serialization/Packets/`). 본문 없는 struct, "서버의 test 변수를 1 증감"이라는 XML 주석. 현재 이 패킷을 사용하는 예제·테스트는 없다. 재사용할지, 수량 필드가 필요해 새 패킷을 만들지는 계획에서 판단하라 (기존 Id 1~17, 0xFFFE/0xFFFF는 사용 중 — 신규 Id는 충돌 금지).
- 예제 패턴: `EchoServer/Program.cs`(리스너·OnReceived·역직렬화·응답), `EchoClient/Program.cs`(CreateClient·ConnectAsync·await using), 루프백 E2E 테스트 패턴: `EchoExample.Tests/EchoEndToEndTests.cs`(GetFreePort·TaskCompletionSource·WaitAsync 타임아웃).
- 라이브러리 진입점: `ServerNet.CreateListener()`/`CreateClient()`만 사용(Transport 구현체는 internal). 직렬화: `BinaryPacketSerializer`, `PacketPool`(헤더 4B `[Id(2)|BodyLength(2)]`), `SessionContextExtensions.SendAsync<T>` 확장.
- 솔루션: `ClaudeCodeStudy.sln` (.NET 10). 기존 테스트 14개 전부 통과 상태.

## 프로젝트 규칙 (CLAUDE.md 발췌 — 반드시 준수)
1. Interface는 순수 추상화만, Core는 구현만. 외부 소비자는 ServerNet 팩토리 인터페이스로만 사용.
2. 모든 public API·인터페이스에 상세 XML 문서 주석: Thread Safety·Memory Allocation(소유권/생명주기)·Blocking 여부 필수.
3. 네트워크·메모리 관련 선언부(Socket, Channel<T>, ArrayPool, Interlocked 대상 필드, SemaphoreSlim 등)에는 **내부 동작 메커니즘을 근거로 한 인라인 주석** 필수.
4. 동시성: lock-free(Interlocked·Channel) 우선. 전통 락 사용 시 정당화 주석 필수.
5. 새 기능 추가 시 예제(Program.cs)가 라이브러리 사용 예제 역할을 해야 한다.
6. 테스트는 xUnit, `dotnet test`로 실제 실행.

## 제약조건
- 서버 바인딩은 루프백(127.0.0.1) 전용, 포트는 기존 예제와 충돌하지 않게 (9000=Echo, 8080=EchoWeb 사용 중).
- 클라이언트 최종 검증이 가능해야 함: 총 N회 더하기·M회 빼기 후 카운터 == N−M 확인 경로 필요 (조회 패킷 또는 서버 응답 — 방식은 계획에서 결정).
- 솔루션에 새 프로젝트 추가 시 `ClaudeCodeStudy.sln`에 등록.
- 테스트는 기존 `EchoExample.Tests`에 추가하거나 새 테스트 프로젝트 — 계획에서 결정하되 근거 제시.

## 비범위 (non-goals)
- 모니터링·인증·티켓팅·웹 UI 연동 없음.
- 새 Transport·프로토콜 확장 없음 (기존 ServerLib public API만 사용).
- 벤치마크/성능 측정 하네스 없음 (경합 정확성 시연이 목적).
