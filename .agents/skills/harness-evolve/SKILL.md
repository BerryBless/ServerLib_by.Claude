---
name: harness-evolve
description: "TDD 사이클 완료 후 초기 요구사항 명세와 최종 구현 코드 사이의 델타(진화)를 포착한다. 요구사항 명세 vs 테스트 설계 vs 최소 구현 vs 리팩토링 후 코드의 변화를 추적하고, TDD 사이클에서 발견된 암묵적 요구사항과 설계 결정을 기록한다. 모든 TDD 하네스 실행 종료 시 자동 실행되며 /harness-evolve로 수동 호출도 가능."
---

# Harness Evolve Skill

TDD 사이클의 진화 궤적을 포착하여 초기 명세와 최종 코드 사이의 갭을 분석한다.

## 입력 읽기 (모두 Read)

```
_workspace/00_requirements.md          ← 초기 요구사항 (T=0)
_workspace/01_analyst/test_design.md   ← Red 단계 설계 근거
_workspace/01_analyst/Tests/           ← 설계된 테스트 집합
_workspace/02_builder/build_notes.md   ← Green 단계 구현 결정
_workspace/02_builder/Src/             ← 최소 구현
_workspace/03_qa/refactor_guide.md     ← Refactor 결정
_workspace/03_qa/Src/ (있으면)         ← 리팩토링 후 최종 코드
_workspace/03_qa/test_results.txt      ← 최종 테스트 결과
```

## 델타 포착 5개 차원

### Δ1: 요구사항 명확화 델타
초기 요구사항에서 모호했던 부분이 TDD 과정에서 어떻게 구체화됐는지.

```markdown
## 요구사항 명확화
| 초기 요구사항 | 구체화된 요구사항 | 발견 단계 |
|-------------|----------------|---------|
| "숫자를 더한다" | "int 범위 내 두 정수의 합을 반환한다" | Red (테스트 설계) |
| "잘못된 입력 처리" | "null 입력 시 ArgumentNullException, 0 나눗셈 시 DivideByZeroException" | Red |
```

### Δ2: 발견된 암묵적 요구사항
초기 명세에 없었지만 TDD 과정에서 발견된 요구사항.

```markdown
## 발견된 암묵적 요구사항
| 발견 사항 | 발견 단계 | 테스트 이름 | 처리 |
|---------|---------|-----------|-----|
| 빈 문자열 입력 처리 필요 | Red | Parse_EmptyString_ThrowsArgEx | 테스트 추가 |
| 음수 입력 허용 여부 | Green | (발견 후 analyst에게 질문) | 명세 보완 |
```

### Δ3: 구현 진화 궤적
코드가 어떻게 발전했는지 (Fake → 실제 → 리팩토링).

```markdown
## 구현 진화 궤적
| 단계 | 구현 방식 | 변화 이유 |
|------|---------|---------|
| Red | 스텁 (NotImplementedException) | 테스트 컴파일용 |
| Green (Fake) | `return 5` | 단일 테스트 통과 |
| Green (일반화) | `return a + b` | 복수 테스트 삼각측량 |
| Refactor | `return checked(a + b)` | 오버플로우 테스트 추가 |
```

### Δ4: 거부된 설계
TDD 과정에서 검토됐지만 명시적으로 거부된 구현.

```markdown
## 거부된 설계
| 거부 항목 | 거부 이유 | 단계 |
|---------|---------|-----|
| 결과 캐싱 | 현재 테스트 불필요 | Green |
| ICalculator 인터페이스 | YAGNI — 요구 없음 | Refactor |
| 오버플로우 체크 | 요구사항에 명시 없음 | Green |
```

### Δ5: 테스트 커버리지 갭
최종 코드에서 테스트로 커버되지 않는 경로.

```markdown
## 테스트 커버리지 갭
| 미커버 경로 | 위치 | 권장 조치 |
|-----------|------|---------|
| int.MaxValue + 1 오버플로우 | Add 메서드 | 다음 TDD 사이클에서 추가 |
| 네거티브 나눗셈 | Divide | 요구사항 명세 확인 필요 |
```

## 진화 요약 생성

```markdown
# TDD 진화 리포트
생성: {datetime}  |  기능: {feature name}

## TDD 사이클 요약
- Red: N개 테스트 설계 (Happy: N, Edge: N, Error: N)
- Green: N회 시도 (재작업: N회)
- Refactor: N개 개선 포인트 (적용: N개)

## 핵심 진화 포인트
1. {가장 중요한 발견}
2. {두 번째 중요한 발견}
3. ...

## 초기 명세 충실도
- 명세 요구사항 N개 중 N개 구현 완료 (N%)
- 발견된 암묵적 요구사항: N개
- 다음 사이클 추천 항목: N개

## 최종 테스트 현황
- 전체: N개 통과 / N개 설계
- 회귀: 없음 / N개 (상세: ...)

## 다음 TDD 사이클 추천
| 기능/케이스 | 근거 | 우선순위 |
|-----------|------|---------|
| ... | ... | High |
```

## 출력 저장

`_workspace/04_evolution/evolution_report.md`에 Write한다.
오케스트레이터에게 `{"status": "done", "evolution_report": "...", "next_cycle_items": [...]}` 전달.
