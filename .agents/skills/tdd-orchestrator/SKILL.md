---
name: tdd-orchestrator
description: "TDD(테스트 주도 개발) 하네스를 실행하는 오케스트레이터. 사용자 요구사항 입력 시 analyst(Red)→builder(Green)→qa(Refactor) 파이프라인을 에이전트 팀으로 조율하고, 최종에 harness-evolve로 진화 델타를 포착한다. 트리거: 'TDD 해줘', '테스트 먼저 작성', 'Red-Green-Refactor', '기능 구현해줘 (TDD)', 'TDD 사이클', '테스트 주도 개발'. 후속: '다음 기능 TDD', '테스트 추가해줘', 'TDD 재실행', '리팩토링 가이드', '진화 리포트 보여줘'."
---

# TDD Orchestrator

Red-Green-Refactor 사이클을 에이전트 팀으로 조율하는 TDD 하네스 오케스트레이터.

## 실행 모드: 에이전트 팀 (파이프라인 + 생성-검증 혼합)

```
analyst ──→ builder ──→ qa
(Red)       (Green)    (Refactor)
                ↑──────── 재작업 (최대 2회)
```

## 에이전트 구성

| 팀원 | 역할 | 스킬 | 핵심 출력 |
|------|------|------|---------|
| tdd-analyst | 실패 테스트 설계 (Red) | /tdd-red-phase | Tests/*.cs + Stub/*.cs |
| tdd-builder | 최소 구현 (Green) | /tdd-green-phase | Src/*.cs |
| tdd-qa | 런타임 검증 + Refactor | /tdd-refactor-phase | test_results.txt + refactor_guide.md |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1 진행
   - **존재 + 새 기능 요청** → 새 TDD 사이클: 기존 `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동
   - **존재 + "다음 기능"/"테스트 추가"** → 누적 실행: 기존 테스트 보존하며 신규 추가
   - **존재 + "리팩토링만"** → qa 단독 재실행

### Phase 1: 프로젝트 환경 설정

`_workspace/` 하위에 xUnit 테스트 프로젝트를 생성한다:

```bash
mkdir -p E:/project/dotnet_study/_workspace/{01_analyst/Tests,01_analyst/Src,02_builder/Src,03_qa/Src,03_qa,04_evolution}
```

`_workspace/TddSession.csproj` 생성 (처음 실행 시만):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>TddSession</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <!-- 분석가 테스트 + 스텁 -->
  <ItemGroup>
    <Compile Include="01_analyst/Tests/**/*.cs" />
    <Compile Include="01_analyst/Src/**/*.cs" />
  </ItemGroup>
  <!-- builder 구현 (스텁보다 우선) -->
  <ItemGroup>
    <Compile Remove="01_analyst/Src/**/*.cs" />
    <Compile Include="02_builder/Src/**/*.cs" />
  </ItemGroup>
  <!-- qa 리팩토링 코드 (있으면 builder 대체) -->
  <ItemGroup Condition="Exists('03_qa/Src')">
    <Compile Remove="02_builder/Src/**/*.cs" />
    <Compile Include="03_qa/Src/**/*.cs" />
  </ItemGroup>
</Project>
```

요구사항을 `_workspace/00_requirements.md`에 저장한다.

### Phase 2: 팀 구성

```
TeamCreate(
  team_name: "tdd-team",
  members: [
    {
      name: "tdd-analyst",
      agent_type: "tdd-analyst",
      model: "opus",
      prompt: "당신은 TDD 분석가입니다. /tdd-red-phase 스킬을 사용하여 _workspace/00_requirements.md를 읽고 실패하는 xUnit 테스트와 스텁을 작성한 후, tdd-builder에게 완료 SendMessage를 보내세요."
    },
    {
      name: "tdd-builder",
      agent_type: "tdd-builder",
      model: "opus",
      prompt: "당신은 TDD 구현자입니다. tdd-analyst로부터 완료 메시지를 받으면 /tdd-green-phase 스킬을 사용하여 최소 구현을 작성하고 tdd-qa에게 검증 요청 SendMessage를 보내세요. qa로부터 FAIL 메시지를 받으면 수정 후 재검증을 요청하세요 (최대 2회)."
    },
    {
      name: "tdd-qa",
      agent_type: "tdd-qa",
      model: "opus",
      prompt: "당신은 TDD QA입니다. tdd-builder로부터 검증 요청을 받으면 /tdd-refactor-phase 스킬을 사용하여 dotnet test를 실행하세요. 실패 시 tdd-builder에게 FAIL SendMessage를, 전체 통과 시 리더에게 PASS SendMessage를 보내세요."
    }
  ]
)
```

작업 등록:
```
TaskCreate(tasks: [
  {
    title: "Red — 실패 테스트 설계",
    description: "/tdd-red-phase 스킬로 테스트와 스텁 작성",
    assignee: "tdd-analyst"
  },
  {
    title: "Green — 최소 구현",
    description: "/tdd-green-phase 스킬로 테스트 통과 최소 코드 작성",
    assignee: "tdd-builder",
    depends_on: ["Red — 실패 테스트 설계"]
  },
  {
    title: "Refactor — 검증 및 리팩토링",
    description: "/tdd-refactor-phase 스킬로 dotnet test 실행 + 리팩토링 가이드",
    assignee: "tdd-qa",
    depends_on: ["Green — 최소 구현"]
  }
])
```

### Phase 3: 파이프라인 실행

**실행 방식:** 에이전트 팀이 SendMessage로 자체 조율.

**생성-검증 루프 규칙:**
- builder → qa: 구현 완료 시 검증 요청
- qa → builder: FAIL 시 피드백과 함께 재작업 요청 (최대 2회)
- 2회 초과 FAIL: 리더에게 에스컬레이션 → analyst 테스트 재설계 또는 요구사항 재확인

**리더 모니터링:**
- TaskGet으로 각 단계 진행 확인
- 10분+ 유휴 팀원에게 진행 상태 확인 SendMessage

### Phase 4: harness-evolve 실행

qa PASS 판정 후 `/harness-evolve` 스킬을 직접 실행한다:
- `_workspace/` 전체를 읽어 진화 델타를 포착
- `_workspace/04_evolution/evolution_report.md` 생성

### Phase 5: 정리

1. TeamDelete
2. `_workspace/` 보존 (다음 TDD 사이클의 회귀 테스트로 사용)
3. 결과 요약 보고:
   - Red 단계: N개 테스트 설계
   - Green 단계: N회 시도 (재작업 N회)
   - Refactor 단계: N개 개선 제안
   - 진화 델타: 핵심 발견 N개

---

## 산출물 구조

```
_workspace/
├── TddSession.csproj               ← xUnit 테스트 프로젝트
├── 00_requirements.md              ← 사용자 요구사항
├── 01_analyst/
│   ├── test_design.md              ← 테스트 설계 근거
│   ├── Tests/<Feature>Tests.cs     ← Red: 실패하는 테스트
│   └── Src/<Feature>.cs            ← 컴파일용 스텁
├── 02_builder/
│   ├── build_notes.md              ← 구현 결정 기록
│   └── Src/<Feature>.cs            ← Green: 최소 구현
├── 03_qa/
│   ├── test_results.txt            ← dotnet test 실행 결과
│   ├── refactor_guide.md           ← 리팩토링 가이드
│   └── Src/<Feature>.cs            ← Refactor: 개선된 코드 (선택)
└── 04_evolution/
    └── evolution_report.md         ← 진화 델타 리포트
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 요구사항 불명확 | analyst가 질문 목록 전달 → 오케스트레이터가 사용자에게 질문 |
| builder 2회 재작업 후 FAIL | 오케스트레이터가 analyst에게 테스트 재설계 지시 또는 사용자에게 에스컬레이션 |
| dotnet test 환경 오류 | 패키지 복원 실행 후 재시도: `dotnet restore _workspace/TddSession.csproj` |
| 빌드 오류 (네임스페이스 충돌 등) | .csproj의 Compile 항목 조정 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "정수 덧셈·나눗셈 Calculator 클래스를 TDD로 구현해줘"
2. Phase 1: TddSession.csproj 생성, 요구사항 저장
3. Red: analyst가 4개 테스트 + 스텁 작성 (Add 2개, Divide 2개)
4. Green: builder가 Add/Divide 최소 구현 (Divide는 0 체크 포함)
5. Refactor: qa가 `dotnet test` → 4/4 통과 → 매직 넘버 없음 확인 → PASS
6. harness-evolve: "나눗셈 0 처리"가 암묵적 요구사항으로 발견된 것 기록
7. 결과: 모든 산출물 + 진화 리포트

### 에러 흐름 (Green 실패)
1. builder가 Divide 구현에서 0 체크 누락
2. qa: `DivideByZeroTest` FAIL → builder에게 "b==0 처리 필요" 피드백
3. builder 재작업: `if (b == 0) throw new DivideByZeroException()` 추가
4. qa 재실행: 4/4 통과 → PASS
5. harness-evolve: "1회 재작업 후 Green" 기록
