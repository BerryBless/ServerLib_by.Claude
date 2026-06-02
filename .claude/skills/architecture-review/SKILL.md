---
name: architecture-review
description: ".NET/C# 코드의 아키텍처를 심층 감사한다. SOLID 원칙, 레이어 위반, 결합도·응집도, 의존성 방향, 설계 패턴을 체계적으로 분석하고 JSON 결과를 _workspace/02_architecture_findings.json에 출력한다. architecture-reviewer 에이전트가 사용하는 전용 스킬."
---

# Architecture Review Skill

## 입력 읽기

1. `_workspace/00_input/diff.txt`를 Read 도구로 읽는다
2. diff 형식이면 `+` 줄(추가)과 변경된 파일에 집중한다
3. 전체 파일 내용이면 전체를 분석한다

## 감사 체크리스트

### 1. SOLID 원칙

| 원칙 | 탐지 패턴 |
|------|----------|
| **SRP** | 클래스가 2가지 이상의 변경 이유를 가짐. 메서드가 데이터 접근 + 비즈니스 로직 + UI 포맷을 동시에 처리. |
| **OCP** | if/switch로 타입을 분기하면서 새 타입 추가 시 기존 클래스를 수정해야 함. 다형성이나 전략 패턴으로 해결 가능한 경우. |
| **LSP** | 오버라이드 메서드가 부모 계약(전제조건/결과조건)을 위반. 파생 클래스가 `NotImplementedException` 던짐. |
| **ISP** | 인터페이스가 구현체가 사용하지 않는 멤버를 강제. 하나의 인터페이스에 10개+ 메서드. |
| **DIP** | 상위 레이어가 구체 타입(`new ConcreteClass()`)을 직접 생성. 인터페이스 없이 하위 레이어에 직접 의존. |

### 2. 레이어 경계 위반

```
[Presentation] → [Application] → [Domain] → [Infrastructure]
```

- **컨트롤러에 비즈니스 로직**: Controller 메서드에 if/계산/도메인 규칙이 있는 경우
- **도메인에 인프라 의존**: Domain 엔티티/서비스가 DbContext, HttpClient, ILogger를 직접 참조
- **Application이 Infrastructure 구체 타입 참조**: 인터페이스 대신 구체 Repository 직접 사용
- **프로젝트 참조 방향**: `.csproj`에서 역방향 참조 (`Domain` → `Infrastructure`)

### 3. 결합도·응집도

- **갓 클래스**: 500줄+ 클래스, 10개+ 공개 메서드, 5개+ 서비스 의존성 주입
- **기능 편애**: 메서드가 자기 클래스보다 다른 클래스의 데이터를 더 많이 사용
- **서비스 로케이터 안티패턴**: `IServiceProvider.GetService<T>()` 런타임 해결
- **Shotgun Surgery**: 하나의 개념 변경이 여러 클래스에 흩어진 수정을 요구하는 구조

### 4. 설계 패턴

- **Repository 오용**: Repository가 비즈니스 로직을 포함, 또는 `IQueryable` 반환으로 캡슐화 파괴
- **팩토리 없는 복잡 객체 생성**: 의존성 많은 객체를 곳곳에서 `new`로 생성
- **Observer/Event 누수**: 구독 후 해제 없음, static 이벤트에 인스턴스 구독
- **Mediator 과용**: 단순 CRUD에 MediatR Command/Query 도입으로 오히려 복잡도 증가

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **critical** | 전체 아키텍처 붕괴, 레이어 역전, DIP 위반으로 테스트 불가 |
| **high** | 확장성 차단, 의존성 순환, 갓 클래스 (즉시 리팩토링 필요) |
| **medium** | SOLID 부분 위반, 응집도 저하 (다음 스프린트 내 수정 권장) |
| **low** | 패턴 미적용, 구조 개선 가능성 (기술부채 등록 수준) |

## 출력

결과를 `_workspace/02_architecture_findings.json`에 Write 도구로 저장한다.
발견사항이 없으면 빈 배열과 score=100으로 저장한다.
저장 완료 후 리더에게 SendMessage로 완료를 알린다.
