---
name: pipeline-architect-orchestrator
description: "System.IO.Pipelines 기반 고성능 서버 라이브러리의 IO 루프·스레드 디스패처를 감독자 패턴으로 설계하고 부하 테스트 감사까지 수행하는 오케스트레이터. 트리거: 'Pipelines 설계', 'IO 루프 구현', '디스패처 설계', 'Zero-copy 서버', 'Kestrel 패턴', '고성능 IO', 'PipeReader 설계', 'Channel 디스패처'. 후속 작업: '다시 설계', 'IO 루프 재작업', '디스패처 수정', '감사 재실행', '이전 결과 개선'."
---

# Pipeline Architect Orchestrator

System.IO.Pipelines 기반 IO 루프와 Channel<T> 기반 스레드 디스패처를 감독자 패턴으로 설계·검증하는 오케스트레이터.

## 실행 모드: 에이전트 팀 (감독자 패턴)

```
[오케스트레이터] → TeamCreate
    └── [pipeline-supervisor] (감독자/리더)
            ├── 감독: [io-loop-designer] (워커 1)
            ├── 감독: [thread-dispatcher-designer] (워커 2)
            └── 위임: [load-test-auditor] (검증자)
```

감독자가 두 워커의 설계를 동적으로 조율하고, 품질 게이트 통과 후 감사자에게 위임한다.

## 에이전트 구성

| 팀원 | 에이전트 타입 | 역할 | 출력 |
|------|-------------|------|------|
| pipeline-supervisor | pipeline-supervisor | 감독, 조율, 통합 | `04_pipeline_architecture.md` |
| io-loop-designer | io-loop-designer | IO 루프 설계 | `02_io_loop/IoLoop.cs` |
| thread-dispatcher-designer | thread-dispatcher-designer | 디스패처 설계 | `02_dispatcher/ThreadDispatcher.cs` |
| load-test-auditor | load-test-auditor | 부하 테스트 감사 | `03_load_test_audit.md` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1 진행
   - **존재 + 특정 재작업** ("IO 루프 다시") → 부분 재실행: 해당 워커만 재할당
   - **존재 + 새 요구사항** → 새 실행: `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동

### Phase 1: 설계 브리프 수집

사용자 입력에서 다음을 파악하여 `_workspace/00_design_brief.md`에 저장:

```markdown
# Pipeline 설계 브리프

## 서버 요구사항
- 프로토콜: [HTTP/WebSocket/Custom Binary/기타]
- 예상 동시 연결: [N개]
- 예상 처리량: [N rps / N msg/s]
- 메시지 최대 크기: [N KB]

## 성능 목표
- 레이턴시 목표: [N ms p99]
- 메모리 한계: [N MB per connection]
- GC 일시정지 허용: [있음/없음]

## 제약사항
- .NET 버전: [net10.0]
- 특이 사항: [...]
```

브리프가 불충분하면 사용자에게 핵심 항목(프로토콜, 처리량 목표)만 질문한다.

### Phase 2: 팀 구성

```
TeamCreate(
  team_name: "pipeline-design-team",
  members: [
    {
      name: "pipeline-supervisor",
      agent_type: "pipeline-supervisor",
      model: "opus",
      prompt: "당신은 파이프라인 설계 팀의 감독자입니다. _workspace/00_design_brief.md를 읽고 io-loop-designer와 thread-dispatcher-designer에게 설계 작업을 할당하세요. 두 워커의 품질을 모니터링하고, 인터페이스를 협상하며, 완성 후 load-test-auditor에게 감사를 위임하고 최종 아키텍처 문서를 작성하세요."
    },
    {
      name: "io-loop-designer",
      agent_type: "io-loop-designer",
      model: "opus",
      prompt: "당신은 IO 루프 설계자입니다. 감독자로부터 설계 지시를 받으면 /io-loop-design 스킬을 사용하여 System.IO.Pipelines 기반 IO 루프를 설계하고 _workspace/02_io_loop/IoLoop.cs에 저장하세요."
    },
    {
      name: "thread-dispatcher-designer",
      agent_type: "thread-dispatcher-designer",
      model: "opus",
      prompt: "당신은 스레드 디스패처 설계자입니다. 감독자로부터 설계 지시를 받으면 /thread-dispatch-design 스킬을 사용하여 Channel<T> 기반 디스패처를 설계하고 _workspace/02_dispatcher/ThreadDispatcher.cs에 저장하세요."
    },
    {
      name: "load-test-auditor",
      agent_type: "load-test-auditor",
      model: "opus",
      prompt: "당신은 부하 테스트 감사자입니다. 감독자로부터 감사 요청을 받으면 /load-test-audit 스킬을 사용하여 IO 루프와 디스패처 코드를 감사하고 _workspace/03_load_test_audit.md에 저장하세요."
    }
  ]
)
```

작업 등록 (감독자 패턴 — 감독자가 중앙에서 동적 할당):
```
TaskCreate(tasks: [
  {
    title: "파이프라인 설계 총괄",
    description: "브리프 분석 → 워커 할당 → 품질 모니터링 → 감사 위임 → 통합",
    assignee: "pipeline-supervisor"
  },
  {
    title: "IO 루프 설계",
    description: "/io-loop-design 스킬로 PipeReader/Writer IO 루프 구현",
    assignee: "io-loop-designer",
    depends_on: ["파이프라인 설계 총괄 (감독자 지시 대기)"]
  },
  {
    title: "스레드 디스패처 설계",
    description: "/thread-dispatch-design 스킬로 Channel<T> 디스패처 구현",
    assignee: "thread-dispatcher-designer",
    depends_on: ["파이프라인 설계 총괄 (감독자 지시 대기)"]
  },
  {
    title: "부하 테스트 감사",
    description: "/load-test-audit 스킬로 두 설계 코드 감사",
    assignee: "load-test-auditor",
    depends_on: ["IO 루프 설계", "스레드 디스패처 설계"]
  }
])
```

### Phase 3: 감독자 주도 설계 실행

감독자가 다음 순서로 팀을 이끈다:

**Step 1 — 인터페이스 협상 (병렬 시작 전)**
```
감독자 → io-loop-designer: {"action": "propose-interface", "message_type": "ParsedMessage"}
감독자 → thread-dispatcher-designer: {"action": "confirm-interface", "pipe_capacity": N}
양측 합의 → 감독자가 _workspace/02_interface_contract.cs 작성
```

**Step 2 — 병렬 설계 (팬아웃)**
```
감독자 → io-loop-designer: {"action": "design-io-loop", "brief": "...", "interface": "..."}
감독자 → thread-dispatcher-designer: {"action": "design-dispatcher", "expected-tps": N}
```

**Step 3 — 품질 게이트 (각 워커 완료 시)**
```
감독자가 산출물 파일 Read → 체크리스트 확인
합격 → 다음 단계
불합격 → {"action": "revision-required", "issues": [...]} SendMessage
```

**Step 4 — 감사 위임**
```
두 워커 품질 게이트 통과 →
감독자 → load-test-auditor: {"action": "audit-requested", "artifacts": [...]}
```

**Step 5 — 통합**
```
감사 완료 → 감독자가 _workspace/04_pipeline_architecture.md 작성
```

**감독자 개입 조건:**
- 워커가 10분+ 무응답: 구체적 지침과 함께 SendMessage
- 재작업 요청 2회 후 미해결: 감독자가 해당 부분 직접 보완
- BLOCK 감사 결과: 해당 워커에게 재작업 지시 (1회 한도)

### Phase 4: 정리

1. TeamDelete
2. `_workspace/` 보존
3. 최종 아키텍처 문서 경로 안내

---

## 산출물 구조

```
_workspace/
├── 00_design_brief.md              ← 설계 요구사항
├── 02_interface_contract.cs        ← IO 루프 ↔ 디스패처 인터페이스
├── 02_io_loop/
│   └── IoLoop.cs                   ← IO 루프 구현 (io-loop-designer)
├── 02_dispatcher/
│   └── ThreadDispatcher.cs         ← 디스패처 구현 (thread-dispatcher-designer)
├── 03_load_test_audit.md           ← 부하 테스트 감사 결과
└── 04_pipeline_architecture.md     ← 통합 아키텍처 문서 (감독자)
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 워커 응답 없음 | 감독자가 재지시 → 재응답 없으면 해당 파트 감독자가 직접 처리 |
| 인터페이스 합의 실패 | 감독자가 중재안 직접 제시 |
| BLOCK 감사 후 재작업 실패 | 감사 보고서에 미해결 BLOCK 명시하고 APPROVE 불가 판정 |
| 설계 브리프 불충분 | 핵심 항목만 사용자에게 질문 (프로토콜, 목표 처리량) |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "TCP 이진 프로토콜 서버 파이프라인 설계해줘, 100k rps 목표"
2. Phase 1: 브리프 작성 (TCP/이진/100k rps/net10.0)
3. 인터페이스 협상: `ParsedMessage { ReadOnlySequence<byte> Payload; long ConnectionId; }`
4. IO 루프 설계: FillPipeAsync + ReadPipeAsync + 16KB 백프레셔
5. 디스패처 설계: BoundedChannel(capacity:1000) + 8 워커 + struct Work Item
6. 품질 게이트: 두 설계 모두 통과
7. 부하 감사: APPROVE (CRITICAL 0건)
8. 아키텍처 문서 생성

### 에러 흐름 (BLOCK 발견)
1. IO 루프 초안에서 `reader.AdvanceTo` 누락 → CRITICAL
2. 감독자가 io-loop-designer에게 재작업 지시
3. 재작업 후 AdvanceTo 추가 → 재감사 → APPROVE
4. 아키텍처 문서에 "초기 설계에서 AdvanceTo 누락 → 수정 완료" 기록
