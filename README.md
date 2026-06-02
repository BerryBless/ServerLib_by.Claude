# Multi-Agent System Architecture

고성능 .NET 서버 인프라 및 코드 품질 관리를 위한 멀티 에이전트 자동화 시스템 아키텍처입니다. 
본 시스템은 정밀한 역할 분담을 가진 전문 에이전트, 탐색 및 보조를 위한 유틸리티 에이전트, 그리고 이들을 지휘하는 하네스(오케스트레이터)의 3계층 구조로 작동합니다.

---

## 1. 하네스 (Orchestrators)
특정 도메인의 문제를 해결하기 위해 유관 전문 에이전트들을 유기적으로 결합하고 제어하는 최상위 워크플로우 레이어입니다.

| 오케스트레이터 스킬 | 파이프라인 구성 및 담당 에이전트 |
| :--- | :--- |
| `code-review-orchestrator` | `architecture` + `security` + `performance` + `style` 에이전트 병렬 리뷰 수행 |
| `concurrency-guard-orchestrator` | `lock-free-enforcer` ➔ `lock-justification-auditor` ➔ `deadlock-analyzer` ➔ `deadlock-reviewer` |
| `gc-guard-orchestrator` | `heap-allocation-scanner` + `pooling-enforcer` ➔ `allocation-peer-reviewer` (교차 검증) |
| `pipeline-architect-orchestrator`| `io-loop-designer` + `thread-dispatcher-designer` ➔ `pipeline-supervisor` ➔ `load-test-auditor` |
| `tdd-orchestrator` | `tdd-analyst` ➔ `tdd-builder` ➔ `tdd-qa` (Red ➔ Green ➔ Refactor 루프 제어) |

---

## 2. 전문 에이전트 (Specialized Agents)
각 분야의 정적 분석 및 설계 최적화에 특화된 도메인 전문가 에이전트 그룹입니다.

### 정적 분석 & 품질 보증 (QA)
* **`architecture-reviewer`**
  * SOLID 원칙, 레이어 경계 준수 여부, 결합도·응집도 분석 및 설계 패턴 적합성 감사
* **`security-reviewer`**
  * OWASP Top 10, SQL/Command Injection, 인증 결함 탐지 및 민감 정보 노출 방지
* **`style-reviewer`**
  * C# 네이밍 컨벤션, 메서드 복잡도(Cyclomatic Complexity), 중복 코드 및 XML 문서화 누락 평가

### 고성능 & 메모리 최적화 (GC 무중단 설계)
* **`performance-reviewer`**
  * 데이터베이스 N+1 쿼리, 동기 I/O 블로킹, 불필요한 힙 할당 및 비효율적 LINQ 구문 탐지
* **`heap-allocation-scanner`**
  * 값 타입 `boxing/unboxing`, 루프 내 `new` 할당, LINQ 남용, 클로저(Closure)로 인한 강제 할당 정밀 스캔
* **`pooling-enforcer`**
  * `ValueTask`, `Span<T>`, `ArrayPool<T>`의 올바른 사용 강제 및 미풀링(Unpooled) 버퍼 탐지
* **`allocation-peer-reviewer`**
  * `heap-allocation-scanner`와 `pooling-enforcer`가 제출한 보고서를 교차 검증하여 오탐지 필터링

### 동시성 & 데드락 분석 (Concurrency)
* **`deadlock-analyzer`**
  * `async`/`await` 비동기 메서드 내 데드락 정적 분석 (`.Result` 동기 블로킹, `lock` 블록 내부의 `await` 패턴 등)
* **`deadlock-reviewer`**
  * `deadlock-analyzer` 보고서 독립 검증 및 위양성/위음성(False positive/negative) 보완
* **`lock-free-enforcer`**
  * 불필요한 전통적 Lock 구문 탐지, `Interlocked` 계열 메서드 및 무잠금 `Channel<T>` 기반 대안 제시
* **`lock-justification-auditor`**
  * 어쩔 수 없이 전통적 락(`Monitor`, `Mutex` 등)을 사용한 소스 코드 내 정당화 주석(`// Justification: ...`) 존재 여부 감사

### 저지연 I/O & 네트워크 파이프라인
* **`io-loop-designer`**
  * `System.IO.Pipelines` API 기반 고성능 Zero-copy I/O 루프 설계 및 저수준 구현
* **`thread-dispatcher-designer`**
  * `Channel<T>` 및 `IThreadPoolWorkItem` 기반 고성능 락-프리 디스패처/라우터 아키텍처 설계
* **`load-test-auditor`**
  * `PipeReader`/`PipeWriter` 자원 누수, `AdvanceTo` 미호출, 백프레셔(Backpressure) 오작동 탐지
* **`pipeline-supervisor`**
  * I/O 루프 및 디스패처 설계 팀 감독, 인터페이스 협상 중재 및 코어 네트워크 아키텍처 통합

### 테스트 주도 개발 (TDD)
* **`tdd-analyst`**
  * 요구사항 검증을 위한 실패하는 Red 단계 테스트 케이스 설계 (`xUnit` 기반)
* **`tdd-builder`**
  * 실패한 테스트를 가장 최소한의 코드로 통과시키는 Green 단계 운영 코드 구현
* **`tdd-qa`**
  * 테스트 실행 결과 검증 및 Refactor 단계에서의 코드 품질 개선 가이드 제공

---

## 3. 유틸리티 에이전트 (Utility Agents)
컨텍스트 확보 및 리서치, 폴오프 백업을 지원하는 보조 에이전트입니다.

* **`Explore`**
  * 소스 코드 내 파일 패턴 및 심볼(Symbol) 검색 전용 에이전트 (Read-Only 안전 보장)
* **`Plan`**
  * 대규모 수정 전 구현 계획 설계 및 아키텍처 트레이드오프(Trade-off) 비용 분석
* **`general-purpose`**
  * 도메인을 넘나드는 복잡한 멀티스텝 백그라운드 조사 및 기술 리서치 수행
* **`claude`**
  * 위에서 명시된 특정 전문 에이전트의 롤에 해당하지 않는 예외 및 범용 태스크 처리
