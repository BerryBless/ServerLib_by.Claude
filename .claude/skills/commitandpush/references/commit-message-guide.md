# 한국어 커밋 메시지 작성 가이드

## 전체 형식

```
{접두사}: {제목} (50자 이내)
(빈 줄)
- {상세 변경 1}
- {상세 변경 2}
(없으면 본문 생략)
(빈 줄)
Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

## 접두사 판단 트리

```
변경이 새 파일/기능 추가인가?
  YES → 추가
  NO → 기존 코드를 수정했는가?
    → 잘못된 동작을 고쳤는가?
        YES → 버그수정
        NO → 외부 동작 변화 없이 구조만 바꿨는가?
            YES → 리팩토링
            NO → 기능 개선/변경 → 수정
    → 문서/주석만 변경?
        YES → 문서
    → 테스트 파일만?
        YES → 테스트
    → 패키지/의존성만?
        YES → 의존성
```

## 접두사별 예시

```
추가: BinaryPacketSerializer 구현체 및 예제 패킷 추가

- SpanWriter/SpanReader ref struct LittleEndian 인코더/디코더 추가
- EchoPacket, ChatPacket 예제 패킷 구현
- IPacketSerializer 인터페이스에 where T : IPacket 제약 추가

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

```
수정: SocketPipelineSession 패킷 프레이밍 로직 개선

- TryReadPacket으로 완전한 패킷 단위 분리 처리
- AdvanceTo consumed/examined 분리로 부분 수신 대응

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

```
버그수정: TddSession.csproj 조건부 컴파일 경로 수정

- Exists() 절대 경로 → 상대 경로로 수정
- builder Session.cs 미포함 문제 해결

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

```
리팩토링: Session 클래스 Dispose 체크 원자성 개선

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

```
문서: ServerLib 인터페이스 XML 주석 Thread Safety 항목 추가

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

```
테스트: Session GC 억제 Roundtrip 테스트 10개 추가

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

```
의존성: BenchmarkDotNet 0.14.0 추가

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

## 제목 작성 규칙

1. **50자 이내** (한글 1자 = 2자, 영어 1자 = 1자로 계산)
2. **명령형 동사 선택**: 추가/제거/개선/수정/변경/통합/분리
3. **구체적**: "코드 수정" ❌ → "SocketPipelineSession 프레이밍 로직 개선" ✅
4. **마침표 없음**
5. **이슈 번호 있으면 끝에**: `수정: 로그인 오류 수정 (#123)`

## 본문 작성 규칙

1. 제목과 본문 사이 **반드시 빈 줄 1개**
2. 각 항목은 `- ` 로 시작
3. 72자 줄 제한 권장
4. **무엇을**보다 **왜**를 강조할 때 가치가 있음
5. 변경이 단순하면 본문 생략 가능

## 절대 금지 접두사

아래 접두사는 **어떤 상황에서도 사용 금지**:

| 금지 | 이유 | 대체 |
|------|------|------|
| `자동:` | 의미 없는 기계적 접두사 | 변경 유형에 맞는 접두사 사용 |
| `update:` | 영어 혼용 | `수정:` 또는 `리팩토링:` |
| `fix:` | 영어 혼용 | `버그수정:` |
| `add:` | 영어 혼용 | `추가:` |
| `chore:` | 영어 혼용 | 해당 유형의 한국어 접두사 |

**자동 커밋도 동일한 규칙 적용**: Claude가 자동으로 커밋 메시지를 생성할 때도
반드시 이 가이드의 접두사 판단 트리를 따른다. `자동:` 접두사는 금지.

## 복수 접두사 처리

여러 유형이 섞인 경우:
- 가장 중요한 변경 1개의 접두사 사용
- 나머지는 본문에 리스트로 기술
- 너무 많은 변경은 커밋을 분리하도록 제안

## 봇 서명 형식 (고정)

```
Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

- 항상 마지막 줄
- 본문 있을 때: 본문 후 빈 줄 + 서명
- 본문 없을 때: 제목 후 빈 줄 + 서명
- 절대 수정 금지
