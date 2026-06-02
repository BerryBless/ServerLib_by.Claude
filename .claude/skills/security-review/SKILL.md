---
name: security-review
description: ".NET/C# 코드의 보안 취약점을 심층 스캔한다. OWASP Top 10, CWE 기반 분석으로 인젝션·인증·민감정보 노출·CSRF·권한 결함을 탐지하고 JSON 결과를 _workspace/02_security_findings.json에 출력한다. security-reviewer 에이전트가 사용하는 전용 스킬."
---

# Security Review Skill

## 입력 읽기

1. `_workspace/00_input/diff.txt`를 Read 도구로 읽는다
2. diff면 `+` 줄(추가된 코드)에 집중한다 — 제거된 코드(`-`)는 위협 감소로 긍정적일 수 있다
3. 하드코딩된 시크릿/자격증명은 추가·제거 모두 확인한다

## 감사 체크리스트

### 1. 인젝션 (CWE-89, 77, 90)

**SQL 인젝션 패턴:**
```csharp
// 위험: 문자열 연결
$"SELECT * FROM Users WHERE Name = '{name}'"
// 위험: string.Format
string.Format("SELECT ... WHERE id = {0}", id)
// 안전: 파라미터화
"SELECT * FROM Users WHERE Name = @name"
```

**커맨드 인젝션 패턴:**
- `Process.Start()` 또는 `ProcessStartInfo`에 사용자 입력 전달
- `cmd.exe /c` + 사용자 입력 연결

**EF Core 주의:**
- `FromSqlRaw()`에 보간 문자열 사용 → `FromSqlInterpolated()` 또는 파라미터 사용 필요

### 2. 민감 정보 노출 (CWE-312, 798, 532)

- 소스 코드 내 비밀번호, API 키, 연결 문자열 하드코딩
- `Console.WriteLine` / `_logger.LogInformation`에 비밀번호, 토큰, PII 기록
- `ToString()` 또는 직렬화 시 민감 필드 노출
- `[JsonIgnore]` / `[Newtonsoft.Json.JsonIgnore]` 누락된 민감 속성

### 3. 인증·권한 결함 (CWE-306, 862, 639)

- `[Authorize]` 어트리뷰트 누락된 컨트롤러/액션
- 사용자 ID를 요청 파라미터에서 직접 받아 DB 조회 (IDOR)
- JWT/쿠키 유효성 검사 우회 가능 코드
- 역할(Role) 검사 없이 관리 기능 접근 허용

### 4. 역직렬화 (CWE-502)

```csharp
// 위험
BinaryFormatter.Deserialize(stream)
JsonConvert.DeserializeObject(json, Type.GetType(typeName))  // 타입 제어 안 됨

// 위험: TypeNameHandling
new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto }
```

### 5. 암호화 결함 (CWE-327, 759)

- `MD5` / `SHA1` 단독 사용 (비밀번호 해싱 등)
- 하드코딩된 IV, Salt, 암호화 키
- `Random` 클래스를 보안 목적으로 사용 (→ `RandomNumberGenerator` 사용 필요)
- ECB 모드 사용

### 6. XSS / CSRF (CWE-79, 352)

- Razor 뷰에서 `Html.Raw()` 또는 `@:` + 사용자 입력
- `[ValidateAntiForgeryToken]` 누락된 POST 엔드포인트
- CORS 와일드카드 (`*`) 설정

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **critical** | 즉각적 익스플로잇 가능 (SQL 인젝션, 하드코딩 자격증명, 인증 우회) |
| **high** | 익스플로잇에 조건 필요하지만 데이터 손실/권한 탈취 가능 |
| **medium** | 정보 노출, 약한 암호화, 조건부 CSRF |
| **low** | 보안 모범 사례 미준수, 잠재적 위험 |

## 출력

결과를 `_workspace/02_security_findings.json`에 Write 도구로 저장한다.
발견사항이 없으면 빈 배열과 score=100으로 저장한다.
저장 완료 후 리더에게 SendMessage로 완료를 알린다.
