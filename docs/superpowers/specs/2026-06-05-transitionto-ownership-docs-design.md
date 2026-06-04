# 설계: TransitionTo 상태 소유권 문서화 (E5)

**날짜:** 2026-06-05
**출처:** 성능 우선 코드 리뷰(`plan/perf_review_0604.md`) 권장 항목 **E5**
**상태:** 설계 승인됨 → 구현 계획 대기

## 배경 및 목적

`ISession.TransitionTo(SessionState)`는 public이라, 콜백으로 `ISession`을 받은 소비자가 세션 상태머신을 임의로 바꿀 수 있다. 탐색 결과:

- **라이브러리가 transport 생명주기를 구동**: `SocketPipelineListener`가 `Connected`(수락 후, `:204`)·`Disconnected`(해제 시, `:196`)를 설정.
- **하드 가드는 하나뿐**: CAS가 "`Disconnected`에서의 부활"만 막는다(`SocketPipelineSession.cs:93`). 그 외 전환은 모두 허용.
- **`SessionState`**: predefined 0~4(`Connecting`/`Connected`/`Authenticated`/`Disconnecting`/`Disconnected`) + `Custom(≥5)`.

**핵심 긴장:** `TransitionTo`는 두 용도를 겸한다 — (1) 소비자의 정당한 *앱 레벨 상태 마킹*(`Authenticated`/`Custom`), (2) 라이브러리 소유의 *transport 생명주기*. 소비자가 transport 상태를 직접 설정하면 보고 상태와 실제 소켓 상태가 어긋난다(예: `Disconnected`로 강제했으나 소켓은 살아있음).

**결정(브레인스토밍):** 코드/API를 바꾸지 않고 **소유권 규약을 문서로 명시**한다. E5는 LOW이며, 앱/transport 분리 강제(내부 전환 경로)나 State 읽기전용화는 변경 대비 이득이 작아 이번 범위에서 배제한다.

**목표:** `TransitionTo`·`SessionState` 문서에 "transport 상태는 라이브러리 소유, 소비자는 `Authenticated`/`Custom`만"을 명확히 기재해 오용을 예방한다.

**비목표(YAGNI):**
- 소비자 호출에서 transport 상태 전환을 코드로 거부(내부 전환 경로 분리) — 별도 사이클로 보류.
- `TransitionTo`를 인터페이스에서 제거하고 State 읽기전용화 — 앱 상태 마킹 기능 상실.
- 동작/시그니처/가드 변경 — 없음.

## 설계 결정

| 항목 | 채택 | 대안(미채택) | 사유 |
|------|------|------------|------|
| 오용 방지 수단 | XML/README 문서 규약 | 코드 검증(transport 거부) | E5 LOW, 코드 변경 대비 이득 작음; 정당한 앱 마킹 보존 |
| 동작 | 불변(CAS 부활 차단 유지) | FSM 전이 규칙 | 과도 |
| API | 시그니처 불변 | 인터페이스에서 제거 | 비파괴, 앱 상태 마킹 유지 |

## 컴포넌트 구조 (문서만)

```
ServerLib/Interface/
├─ ISession.cs        TransitionTo <remarks>에 "[상태 소유권]" 절 추가
└─ SessionState.cs    predefined 상태에 transport/앱 소유권 라벨
README.md             ISession.TransitionTo / SessionState 절에 노트
```

구현체(`SocketPipelineSession`/`StubSession`)·테스트·동작 불변.

## 핵심 변경 (문서 문구)

### ISession.TransitionTo `<remarks>` 추가 절
```
<b>[상태 소유권:]</b> transport 생명주기 상태(<see cref="SessionState.Connecting"/>·<see cref="SessionState.Connected"/>·
<see cref="SessionState.Disconnecting"/>·<see cref="SessionState.Disconnected"/>)는 서버 라이브러리가 소유·구동합니다.
소비자가 이 상태로 직접 전환하면 보고 상태와 실제 소켓 상태가 어긋날 수 있습니다.
소비자는 <see cref="SessionState.Authenticated"/> 또는 <see cref="SessionState.Custom(int)"/>(앱 레벨) 상태만 설정하십시오.
하드 강제는 Disconnected 부활 차단(CAS)뿐이며, 그 외는 규약입니다.
```

### SessionState — predefined 상태 소유권 라벨
각 static 필드 `<summary>`에 한 구절 추가(예):
- `Connecting`/`Connected`/`Disconnecting`/`Disconnected` → "(transport — 라이브러리 소유)"
- `Authenticated` → "(앱 레벨 — 소비자 설정 가능)"
- `Custom(int)` → 기존 설명에 "(앱 레벨)" 명시

### README
`ISession` 표의 `TransitionTo` 행(또는 인접 노트)에:
> 상태 소유권: transport 상태(Connecting/Connected/Disconnecting/Disconnected)는 라이브러리 소유. 소비자는 `Authenticated`/`Custom`만 설정 권장.

## 변경 파일 목록

| 파일 | 종류 | 내용 |
|------|------|------|
| `ServerLib/Interface/ISession.cs` | 수정 | `TransitionTo` `<remarks>`에 상태 소유권 절 |
| `ServerLib/Interface/SessionState.cs` | 수정 | predefined 상태에 소유권 라벨 |
| `README.md` | 수정 | TransitionTo/SessionState 소유권 노트 |

비변경: 구현체, 테스트, 동작, 시그니처, CAS 가드.

## 빌드 검증

```
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release   # 문서 cref 컴파일 확인
dotnet test  E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release   # 기존 52 회귀(동작 불변)
```

(신규 테스트 없음 — 순수 문서 변경이라 검증 대상은 "기존 52 통과 + 문서 cref 유효".)

## 향후 확장 포인트

- 운영 중 transport 상태 오용이 실제 문제로 드러나면, 공개 `TransitionTo`는 앱 상태만 허용(검증)하고 라이브러리는 `internal` 전환 경로를 쓰는 분리안을 별도 사이클로 구현(브레인스토밍 옵션 A).
