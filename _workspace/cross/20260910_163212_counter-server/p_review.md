# Codex 프롬프트 템플릿 — 독립 코드 리뷰 (Phase 3 / 1차)

---

너는 이 리포지토리의 구현 결과를 독립 리뷰하는 역할이다. 다른 모델의 리뷰가 별도로 진행 중이며 서로 교차 검증할 예정이다. 네 판단만 작성하라.

## 확정 계획 (구현이 따라야 했던 기준)
# 13_final_plan — 경합 카운터 서버 통합 최종 계획 (라운드 1)

**구성: `10_codex_plan.md`를 베이스로 채택하고(D1), 아래 수정 사항을 적용한 것이 최종 계획이다.** 구현자는 두 문서를 함께 읽되, 충돌 시 본 문서가 우선한다. 각 수정의 근거는 `12_plan_adjudication.md`의 D번호.

## 베이스 계획 요약 (10_codex_plan.md)
- `IncrementPacket`(Id=3)·`DecrementPacket`(Id=4) 재사용, `CounterQueryPacket`(Id=18, 본문 0B)·`CounterValuePacket`(Id=19) 신설.
- 신규 프로젝트 3개: `CounterServer`(9300, 루프백), `CounterClient`, `CounterExample.Tests` + sln 등록.
- 서버: 인스턴스 `CounterState`(Interlocked), `CounterHandler`(헤더 id 라우팅 + 길이 검증 — `Deserialize<T>`가 id를 검증하지 않으므로 필수), Program은 EchoServer 패턴.
- 클라: `CounterScenario.RunAsync` 실행기(Program과 E2E 테스트가 공유), 8연결 × (+1,000/−750), 공통 시작 신호 → 연결별 마지막 명령 후 조회(=그 연결 처리 완료 배리어) → 전 연결 확인 후 최종 조회 → 기대값 2,000 검증, PASS/FAIL + 종료 코드.
- 실행 전제: 새 서버 + 단일 클라 1회 실행. 재실행은 서버 재시작 안내. `checked` 산술.
- 실패 처리: 전체 기한·취소 전파·전 연결 정리·비영 종료 코드, 잘못된 패킷은 길이·id 검증으로 차단.

## 베이스 대비 수정 사항 (전부 반영 필수)

1. **[D3] 응답 16B:** `CounterValuePacket` = `long Value` + `long AppliedOps` (16B). `CounterState`에 `AppliedOps`(적용된 증감 연산 총수) 추가 — **항상** `Interlocked.Increment`로 집계. 클라 최종 검증은 `Value == 8×(1000−750) = 2,000` **및** `AppliedOps == 8×(1000+750) = 14,000` (둘 다 `checked`). 두 값의 쌍은 비원자이므로 "정지 상태(배리어 이후) 조회"에서만 함께 단언한다는 주석 필수.
2. **[D4] 테스트 항목 삭제:** 베이스 §6 "완료 확인 절차 — 마지막 명령을 테스트용 비동기 신호로 지연" 항목은 구현하지 않는다(순차 디스패치 구조상 자기모순). 대신 E2E는 실제 `CounterScenario`를 호출해 배리어 경로 자체를 검증한다.
3. **[D6] 문서 갱신:** `CLAUDE.md`·`AGENTS.md` 모두에 예제 목록 항목(CounterServer 1줄·CounterClient 1줄)과 plan 문서 표에 `plan/contention_counter_0910.md` 1행 추가.
4. **[D9] XML 주석 정밀화:** 증감 경로 "동기 갱신 후 완료된 ValueTask, 무할당" / 조회 응답 경로 "송신 미완료 시 비동기 완료·조건부 할당 가능(`PacketSendExtensions.cs:78-88`)"으로 구분 서술. Blocking·Memory Allocation 주석에 그대로 반영.
5. **[D10] 조회 응답 송신 실패:** `SocketException`을 "상대 종료"로 단정하지 않는다(송신 타임아웃도 TimedOut으로 변환됨). 로그 후 해당 세션 종료. 자동 재송신 금지.
6. **[D12] 서버 종료 출력:** 아무 키 종료 시 참고용 `Value`만 출력(검증 판정 아님 — 정지 보장 없음).
7. **[D13] 미지 패킷 테스트:** Id=250 사용 유지. 근거 문장은 "서버 측은 정상 PING(0xFFFE)만 하트비트로 가로채며 그 외는 앱 경로 도달"로 교정.
8. **[D7] 수치 고정:** 연결 8, +1,000/−750, 기대 Value 2,000·AppliedOps 14,000, E2E 타임아웃은 기존 테스트 관례(5초) 또는 부하 규모 고려 시 30초 상한 — 구현자가 실측으로 결정하되 수치를 테스트 상수로 명시.
9. **[sln 함정]** `dotnet sln add` 후 구성 매핑이 기존 프로젝트(프로젝트당 12행: Any CPU/x64/x86 × Debug/Release)와 일치하는지 확인, 누락 시 수동 보완.
10. **[InternalsVisibleTo]** 예제 내부 타입(`CounterState`·`CounterHandler`·`CounterScenario`)을 테스트에서 쓰기 위한 접근 허용은 `EchoExample.Tests` 참조 관례(기존 csproj 방식)를 그대로 따른다.

## 테스트 전략 (확정)
- 패킷: Id·본문 길이(0/0/0/16)·`CounterValuePacket` 왕복(0·음수·`long.MinValue/MaxValue`).
- 상태: 실제 `CounterState` 동시 갱신 — 더하기만/빼기만/불균형 혼합, `Value`·`AppliedOps` 동시 단언.
- E2E(실제 소켓·실제 `CounterScenario`): 초기값 0 조회 / 단일 연결 증감 / 8연결 배리어 후 최종값·AppliedOps / 총 연산 0.
- 실패: 잘못된 본문 길이 → 상태 불변 + 해당 연결만 영향, 미지 Id=250, 조회 중 서버 종료, 무응답 타임아웃(실제 작업 취소·정리 포함).
- 실행: `dotnet build` → `dotnet test CounterExample.Tests` → `dotnet test ClaudeCodeStudy.sln`(기존 14개 유지 + 신규 전량 통과 + 실패 0).

## 게이트
기존 테스트 전량 유지·신규 전량 통과·실패 0. 설계 문서 `plan/contention_counter_0910.md` 작성(베이스 §2 목록에 포함).

## 확정 해석 (계획의 일부 — 위 항목과 동등한 구속력)
F-1 예제 타입 public·InternalsVisibleTo 미사용, F-2 응답 16B

## 기준 커밋과 변경 범위
- 기준 커밋: b005dab4094c244e4cc82875d09a21c63715d245
- 변경 diff (커밋되지 않은 변경·신규 파일 포함): _workspace/cross/20260910_163212_counter-server/30_diff.patch 파일을 직접 읽어라 (절대 경로: `E:\project\ClaudeCodeStudy\_workspace\cross\20260910_163212_counter-server\30_diff.patch`, 총 2,615줄이므로 나누어 읽어라). 신규 파일 목록:
```
 M AGENTS.md
 M CLAUDE.md
 M ClaudeCodeStudy.sln
 A CounterClient/CounterClient.csproj
 A CounterClient/CounterScenario.cs
 A CounterClient/Program.cs
 A CounterExample.Tests/CounterEndToEndTests.cs
 A CounterExample.Tests/CounterExample.Tests.csproj
 A CounterExample.Tests/CounterPacketTests.cs
 A CounterExample.Tests/CounterStateTests.cs
 A CounterServer/CounterHandler.cs
 A CounterServer/CounterServer.csproj
 A CounterServer/CounterState.cs
 A CounterServer/Program.cs
 A ServerLib/Core/Serialization/Packets/CounterQueryPacket.cs
 A ServerLib/Core/Serialization/Packets/CounterValuePacket.cs
 A plan/contention_counter_0910.md
```
- 관련 파일은 리포에서 직접 열어 전체 맥락을 확인하라.

## 테스트 실행 결과 (구현자 제공)
```
================================================================================
20_test_results — 경합 카운터 서버 구현 검증 결과
실행일: 2026-09-10
작업트리: E:\project\ClaudeCodeStudy
게이트: 기존 테스트 전량 유지 + 신규 전량 통과 + 실패 0
판정: PASS (기존 14 유지 + 신규 26 전량 통과 + 실패 0)
================================================================================

[1] 빌드
--------------------------------------------------------------------------------
명령: dotnet build ClaudeCodeStudy.sln -c Release

원본 출력(단일 실행 전문, 최종 코드 기준):
  복원할 프로젝트를 확인하는 중...
  복원할 모든 프로젝트가 최신 상태입니다.
  ServerLib -> E:\project\ClaudeCodeStudy\ServerLib\bin\Release\net10.0\ServerLib.dll
  EchoServer -> E:\project\ClaudeCodeStudy\EchoServer\bin\Release\net10.0\EchoServer.dll
  EchoClient -> E:\project\ClaudeCodeStudy\EchoClient\bin\Release\net10.0\EchoClient.dll
  CounterServer -> E:\project\ClaudeCodeStudy\CounterServer\bin\Release\net10.0\CounterServer.dll
  CounterClient -> E:\project\ClaudeCodeStudy\CounterClient\bin\Release\net10.0\CounterClient.dll
  EchoExample.Tests -> E:\project\ClaudeCodeStudy\EchoExample.Tests\bin\Release\net10.0\EchoExample.Tests.dll
  EchoWeb -> E:\project\ClaudeCodeStudy\EchoWeb\bin\Release\net10.0\EchoWeb.dll
  CounterExample.Tests -> E:\project\ClaudeCodeStudy\CounterExample.Tests\bin\Release\net10.0\CounterExample.Tests.dll

  빌드했습니다.
      경고 0개
      오류 0개

  경과 시간: 00:00:00.99

결과: 오류 0 / 경고 0. 신규 프로젝트 3개 포함 전 8개 프로젝트 빌드 성공.

주: 첫 클린 빌드 시에는 ServerLib의 기존 CS0419 경고 10건(IServerListener.cs의
    <see cref="Start"/> 오버로드 모호 참조)이 출력된다. 이번 변경 이전부터 존재하던
    것이며 신규 코드와 무관하다(증분 빌드에서는 캐시되어 표시되지 않음).


[2] 신규 프로젝트 테스트
--------------------------------------------------------------------------------
명령: dotnet test CounterExample.Tests/CounterExample.Tests.csproj -c Release --no-build

원본 출력:
  E:\project\ClaudeCodeStudy\CounterExample.Tests\bin\Release\net10.0\CounterExample.Tests.dll(.NETCoreApp,Version=v10.0)에 대한 테스트 실행
  지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.

  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)

결과: 26 통과 / 0 실패 / 0 건너뜀.

내역:
  CounterPacketTests      12 (Fact 6 + Theory 6케이스)
  CounterStateTests        6
  CounterEndToEndTests     8
  ─────────────────────────
  합계                    26


[3] 전체 솔루션 테스트
--------------------------------------------------------------------------------
명령: dotnet test ClaudeCodeStudy.sln -c Release --no-build

원본 출력:
  E:\project\ClaudeCodeStudy\EchoExample.Tests\bin\Release\net10.0\EchoExample.Tests.dll(.NETCoreApp,Version=v10.0)에 대한 테스트 실행
  E:\project\ClaudeCodeStudy\CounterExample.Tests\bin\Release\net10.0\CounterExample.Tests.dll(.NETCoreApp,Version=v10.0)에 대한 테스트 실행
  지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.

  통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 63 ms - EchoExample.Tests.dll (net10.0)

  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)

결과: 40 통과 / 0 실패 (EchoExample.Tests 14 + CounterExample.Tests 26).
      기준(00_context.md:19)의 "기존 테스트 14개 전부 통과"가 그대로 유지됨.


[4] 반복 실행 (flaky 검증)
--------------------------------------------------------------------------------
명령: dotnet test ClaudeCodeStudy.sln -c Release --no-build   (연속 3회)

  === run 1 ===
  통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 56 ms - EchoExample.Tests.dll (net10.0)
  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)
  === run 2 ===
  통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 98 ms - EchoExample.Tests.dll (net10.0)
  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)
  === run 3 ===
  통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 70 ms - EchoExample.Tests.dll (net10.0)
  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)

결과: 3회 전부 40/40 통과. 재현 불안정(flaky) 없음.

[4-b] 리뷰 지적 수정 후 재검증 (연속 3회)
--------------------------------------------------------------------------------
아래 §[8]의 테스트 견고성 수정 3건을 적용한 뒤 동일 명령을 다시 3회 실행:

  === run 1 ===
  통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 59 ms - EchoExample.Tests.dll (net10.0)
  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)
  === run 2 ===
  통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 62 ms - EchoExample.Tests.dll (net10.0)
  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)
  === run 3 ===
  통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 68 ms - EchoExample.Tests.dll (net10.0)
  통과!  - 실패:     0, 통과:    26, 건너뜀:     0, 전체:    26, 기간: 1 s - CounterExample.Tests.dll (net10.0)

결과: 수정 후에도 3회 전부 40/40 통과. 테스트 수 변화 없음(26 유지).

※ 중간에 1회 실패가 있었고 원인을 제거했다(은폐하지 않고 기록):
   실패 테스트: CounterEndToEndTests.Scenario_ServerStopsMidRun_FailsFastWithoutClaimingPass
   메시지     : "서버가 중도 종료됐는데도 시나리오가 PASS를 반환했습니다."
   상황       : 신규 프로젝트 단독 실행에서는 통과했으나, 전체 솔루션 병렬 실행에서 실패.
   근본 원인  : 최초 설계가 "서버가 N번째 패킷을 받으면 listener.Stop()"이었는데,
                Stop()을 Task.Run으로 스케줄하는 동안 시나리오(4,000패킷 ≈ 30ms)가
                먼저 완주할 수 있는 경주 조건이었다(테스트 자체의 결함, 제품 코드 결함 아님).
   수정       : 시간이 아니라 프로토콜 진행 상태에 결부시켰다. 서버가 "두 번째 조회부터는
                응답하지 않고" 종료하도록 변경 — 첫 조회는 RunAsync의 기준값 조회이고
                두 번째부터는 배리어 조회이므로, 최소 한 연결은 반드시 확인 응답을 받지
                못한다. 시나리오는 구조적으로 완료 불가능해져 결정적으로 실패한다.
   재검증     : 위 [4]의 3회 반복 전부 통과.


[5] 수동 콘솔 데모
--------------------------------------------------------------------------------
명령:
  $srv = Start-Process CounterServer\bin\Release\net10.0\CounterServer.exe -PassThru
  CounterClient\bin\Release\net10.0\CounterClient.exe   # 종료 코드 캡처
  Stop-Process -Id $srv.Id -Force

주: 서버는 EchoServer 패턴대로 Console.ReadKey(intercept:true)로 종료를 대기하므로,
    stdin이 리다이렉트된 상태에서 실행하면 InvalidOperationException으로 즉시 죽는다.
    예제의 종료 설계를 바꾸지 않고 Start-Process로 서버에 자기 콘솔을 준다.

클라이언트 원본 출력:
  경합 카운터 클라이언트 — 다중 연결 동시 증감 검증
    대상       : 127.0.0.1:9300
    연결 수    : 8
    연결당 더하기/빼기 : 1,000 / 750
  ───────────────────────────────────────
    소요 시간   : 0.11초
    Value       : 기대 2,000 / 실제 2,000
    AppliedOps  : 기대 14,000 / 실제 14,000
  ───────────────────────────────────────
  PASS — 8개 연결이 동시에 갱신했지만 최종값이 기대값과 정확히 일치합니다.
         (서버가 Interlocked 원자 연산으로 갱신하므로 갱신 유실이 없습니다)

  EXITCODE=0

(2회 실행: 첫 실행 0.12초 / 최종 코드 재실행 0.11초. 둘 다 PASS·종료 코드 0)

결과: PASS, 종료 코드 0. 계획의 기대값(Value 2,000 / AppliedOps 14,000) 실측 일치.
      시작 시점 스냅샷이 (0,0)이었으므로 "서버 재시작 필요" 경고는 출력되지 않았다.


[6] 솔루션 구성 매핑 확인 (통합 계획 수정 9 — sln 함정)
--------------------------------------------------------------------------------
명령: grep -oE "\{[A-F0-9-]{36}\}\.(Debug|Release)" ClaudeCodeStudy.sln | cut -d. -f1 | sort | uniq -c

원본 출력:
       12 {0BEEE90F-A594-48DE-A1EA-31F9942CF665}
       12 {1DB6F59B-05AA-40D2-A736-6618A1154BE8}
       12 {24BB454D-D8E7-4717-AE40-E2C56C5B4574}
       12 {60BB2B73-6241-41F1-815E-F4249DF76642}
       12 {8F2AB260-0516-443D-8068-E4A0CC8B4BCE}
       12 {B70D156F-FCEE-41B6-A3A9-D018970E0196}
       12 {C66CEF9A-93F4-4C7D-B2DC-805C87C1B03A}
       12 {F6E361D0-3543-4808-B535-722D4C7AB73B}

결과: 8개 프로젝트 × 12행 = 96행. `dotnet sln add`가 Any CPU/x64/x86 × Debug/Release
      구성 매핑을 기존 프로젝트와 동일하게 생성했다. 수동 보완 불필요.


[7] 성능 참고 (검증 목적 아님)
--------------------------------------------------------------------------------
명령: dotnet test ... --filter "FullyQualifiedName~Scenario_EightConnections"
결과: 통과 1 / 기간 119 ms

8연결 × 1,750회 = 14,000 패킷 왕복이 루프백에서 약 0.12초.
E2E 타임아웃 상수 30초 대비 약 250배 여유가 있으므로, CI의 스케줄링 지연이
수십 배로 늘어도 타임아웃이 오탐을 내지 않는다.


[8] 테스트 견고성 수정 (구현자 자체 리뷰 반영, 제품 코드 변경 아님)
--------------------------------------------------------------------------------
전 3건 모두 CounterEndToEndTests.cs 한 파일 안의 테스트 코드 수정이며,
CounterServer/CounterClient/ServerLib 제품 코드는 건드리지 않았다.
수정 후 재검증은 위 [4-b] (3회 전부 40/40 통과).

(a) Scenario_ServerStopsMidRun — Stop() 중복 호출 제거 (잠재 결함)
    문제: 조건이 "두 번째 조회부터"였기 때문에 드롭되는 배리어 조회마다
          Task.Run(listener.Stop)이 스케줄되어 최대 4회 + finally 1회가 겹칠 수 있었다.
          Stop()은 활성 세션을 순회하며 DisposeAsync().GetResult()를 호출하므로
          두 스레드가 동시에 들어가면 이중 Dispose로 ObjectDisposedException이 날 수 있고,
          그 예외가 실제 단언 결과를 가려 혼란스러운 실패로 표면화된다.
    수정: 카운터 값이 정확히 2일 때만 Stop()을 1회 스케줄하고 그 Task 핸들을 보관.
          finally는 그 Task를 await한다(스케줄된 적이 없으면 그때만 직접 Stop()).
    근거: 이 테스트의 결정성은 "응답을 드롭한다"에서 나오고, Stop()은 fail-fast 단언을
          의미 있게 만드는 역할뿐이므로 1회면 충분하다.

(b) Scenario_UnresponsiveServer — "정리까지 마쳤는지"를 실제로 검증
    문제: 계획의 실패 항목은 "무응답 타임아웃(실제 작업 취소·정리 포함)"인데,
          기존 단언은 TimeoutException이 마진 안에 던져졌다는 것만 확인했다.
          RunAsync의 finally 정리 루프가 일부 연결을 빠뜨려도 통과한다.
    수정: 테스트 리스너에 OnClientDisconnected를 등록해 해제 수를 Interlocked로 집계하고,
          TimeoutException을 잡은 뒤 "연결 수(2)만큼 해제됨"을 신호로 대기·단언한다.
          → "반환했다"가 "반환했고 소켓 2개가 실제로 정리됐다"로 강화됨.

(c) RawCounterClient.OnReceived — 검증 없는 Deserialize 제거
    문제: 헬퍼가 id·길이 검증 없이 Deserialize<CounterValuePacket>을 호출했다.
          짧은 프레임이 오면 EndOfStreamException이 콜백 밖으로 새어 수신 루프가 죽고,
          명확한 단언 실패가 "타임아웃까지 hang"으로 퇴화한다.
    수정: 대기자를 먼저 회수한 뒤 id(19)·본문(16B)·프레임(20B)을 검증하고,
          위반 시 예외를 던지지 않고 waiter.TrySetException으로 전달한다.
          (CounterConnection이 이미 따르던 원칙 — 구현 노트 §2.4 — 을 헬퍼에도 적용)
```

## 지시
다음 축으로 리뷰하라: 버그(정확성), 회귀 가능성, 보안, 성능(핫패스 할당·블로킹), 요구사항 충족(확정 계획 대비), 테스트 누락.

각 지적은 반드시 이 형식으로: `[R-X#] 심각도(High/Med/Low) | 파일:라인 | 발생 조건 | 영향 | 근거 | 수정 방향`

- 코드 근거 없는 지적은 내지 마라. 동시성 주장은 재현 조건을 구체적으로 기술하라.
- 스타일 선호는 [취향]으로 분리하고 심각도를 매기지 마라.
- 계획에 없는 변경(범위 이탈)이 diff에 있으면 별도 섹션으로 지적하라.

마지막에 `## 종합`: High 지적 수, 병합 가능 여부에 대한 의견. 한국어로 작성하고 파일을 수정하지 마라.
