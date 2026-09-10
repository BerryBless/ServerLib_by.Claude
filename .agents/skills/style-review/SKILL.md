---
name: style-review
description: ".NET/C# 코드의 스타일과 유지보수성을 심층 감사한다. C# 네이밍 컨벤션, 메서드 복잡도, 중복, XML 문서화, 테스트 커버리지 갭을 평가하고 JSON 결과를 _workspace/02_style_findings.json에 출력한다. style-reviewer 에이전트가 사용하는 전용 스킬."
---

# Style Review Skill

## 입력 읽기

1. `_workspace/00_input/diff.txt`를 Read 도구로 읽는다
2. diff면 `+` 줄(추가된 코드)에 집중한다
3. 공개 API (public 멤버)를 특히 꼼꼼히 확인한다

## 감사 체크리스트

### 1. 네이밍 컨벤션 (C# 표준)

| 대상 | 규칙 | 나쁜 예 | 좋은 예 |
|------|------|--------|--------|
| 클래스/인터페이스 | PascalCase | `userService` | `UserService` |
| 인터페이스 | I 접두사 | `UserService` (인터페이스) | `IUserService` |
| 메서드 | PascalCase | `getUser()` | `GetUser()` |
| 지역 변수/파라미터 | camelCase | `UserName` | `userName` |
| 상수/static readonly | PascalCase | `MAX_SIZE`, `maxSize` | `MaxSize` |
| private 필드 | `_` 접두사 camelCase | `userName`, `UserName` | `_userName` |
| async 메서드 | `Async` 접미사 | `GetUser()` | `GetUserAsync()` |

**의미 없는 이름 탐지:**
- 단일 문자 (루프 변수 i/j 외): `a`, `b`, `x`, `temp`, `data`, `obj`
- 타입 반복: `List<User> userList`, `string nameString`

### 2. 메서드 복잡도

- **30줄 초과 메서드**: 분리 필요 신호
- **중첩 4단계 이상**: 보호 절(guard clause)로 평탄화 가능
- **파라미터 5개 이상**: 파라미터 객체(Parameter Object)로 리팩토링 권장

```csharp
// 화살촉 안티패턴 (나쁜 예)
if (condition1)
{
    if (condition2)
    {
        if (condition3)
        {
            // 핵심 로직
        }
    }
}

// 보호 절 (좋은 예)
if (!condition1) return;
if (!condition2) return;
if (!condition3) return;
// 핵심 로직
```

### 3. 코드 품질 지표

**매직 넘버/문자열:**
```csharp
// 나쁜 예
if (status == 2) ...
Thread.Sleep(5000);
var url = "https://api.example.com/v1";

// 좋은 예
if (status == OrderStatus.Confirmed) ...
private const int RetryDelayMs = 5000;
private const string ApiBaseUrl = "https://api.example.com/v1";
```

**죽은 코드:**
- `// TODO`, `// FIXME`, `// HACK` 주석 (기한 없는 것)
- 주석 처리된 코드 블록
- `if (false)` 또는 도달 불가 코드

**에러 처리 패턴:**
```csharp
// 나쁜 예: 빈 catch
try { ... } catch (Exception) { }

// 나쁜 예: 예외 삼키기
catch (Exception ex) { return null; }  // 로깅도 없음

// 나쁜 예: 일관성 없는 패턴 (일부는 예외, 일부는 null 반환, 일부는 bool)
```

### 4. XML 문서화

공개(public) 메서드, 클래스, 프로퍼티에 XML 주석이 없으면 보고한다.
단, 자명한 속성(`public string Name { get; set; }`)과 테스트 클래스는 예외.

```csharp
// 권장
/// <summary>
/// 사용자 ID로 활성 사용자를 조회한다.
/// </summary>
/// <param name="id">조회할 사용자 ID</param>
/// <returns>활성 사용자. 없으면 null.</returns>
public User? GetActiveUser(int id)
```

### 5. 테스트 커버리지 갭

diff에 새 공개 메서드가 추가됐는데 대응하는 테스트 파일에 새 테스트가 없으면 보고한다.
테스트 파일 위치: `*Tests.cs`, `*Spec.cs`, `*Should*.cs`, `*Given*.cs`

**커버리지 갭 패턴:**
- 예외 경로 미테스트 (happy path만 있음)
- 경계값 미테스트 (null, 빈 목록, 0, 최대값)
- 비동기 메서드 테스트 없음

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **high** | 명시적 버그 유발 가능 (빈 catch로 오류 은폐), 테스트 없는 복잡 비즈니스 로직 |
| **medium** | 가독성 심각 저해, 30줄+ 메서드, 네이밍 대규모 위반 |
| **low** | 미세 컨벤션 위반, 매직 넘버, 문서화 누락 |

## 출력

결과를 `_workspace/02_style_findings.json`에 Write 도구로 저장한다.
발견사항이 없으면 빈 배열과 score=100으로 저장한다.
저장 완료 후 리더에게 SendMessage로 완료를 알린다.
