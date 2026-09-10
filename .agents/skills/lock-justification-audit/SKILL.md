---
name: lock-justification-audit
description: ".NET 10 서버 코드의 모든 전통적 락(lock/Monitor/ReaderWriterLockSlim/Mutex) 위치에 표준 정당화 주석([LOCK-REQUIRED])이 존재하고 내용이 충분한지 감사한다. 주석 미비는 CI 차단 수준으로 보고한다. lock-justification-auditor 에이전트가 사용하는 전용 스킬."
---

# Lock Justification Audit Skill

## 입력 읽기

1. `_workspace/00_input/source.txt`를 Read로 읽는다
2. `lock-free-enforcer`로부터 `necessary_locks` 목록을 참조한다 (있으면 우선 감사)

## 탐지 대상

다음 락 패턴 위치를 모두 기록한다:

```
lock\s*\(.*\)\s*\{
Monitor\.Enter\s*\(
new\s+ReaderWriterLockSlim
_rwLock\.EnterWriteLock|_rwLock\.EnterReadLock|_rwLock\.EnterUpgradeableReadLock
new\s+Mutex\(
```

## 필수 주석 표준

각 락 사용 직전(0~5줄 이내)에 다음 블록이 존재해야 한다:

```csharp
// ===== [LOCK-REQUIRED] =====
// WHY-LOCK: {구체적 이유}
// CONTENTION-OPT: {컨텐션 최소화 조치}
// ===========================
```

**허용 변형:** 주석 구분선(=====) 형식이 다소 달라도 `[LOCK-REQUIRED]`, `WHY-LOCK:`, `CONTENTION-OPT:` 세 키워드가 모두 존재하면 형식 조건은 충족으로 간주한다.

## 내용 품질 평가

### WHY-LOCK 평가

**Accepted (충분):**
- 복합 필드 동시 업데이트 이유 명시: "두 컬렉션을 원자적으로 갱신해야 함 — Interlocked는 단일 참조만 교체 가능"
- ABA 문제 설명: "CAS 루프 시 ABA 위험으로 Interlocked.CompareExchange 부적합"
- 외부 리소스 언급: "소켓 핸들 접근은 OS 수준 직렬화 필요"

**Rejected (불충분):**
- "동시성 보호 위해" — 이유가 아니라 목적
- "thread-safe" — Interlocked로 충분한지 검토 흔적 없음
- 비어 있음 — `WHY-LOCK: ` 다음 내용 없음

### CONTENTION-OPT 평가

**Accepted (충분):**
- 락 범위 최소화 명시: "DB I/O는 락 외부로 이동, 락 내부는 3줄 이내"
- RWLS 선택 이유: "읽기 95%·쓰기 5% 비율, ReaderWriterLockSlim 선택으로 읽기 병렬 허용"
- 낙관적 동시성 적용: "먼저 락 없이 시도 후 충돌 시에만 lock 진입"

**Rejected (불충분):**
- "없음", "N/A", "해당 없음" — 검토 자체를 하지 않았음
- "최적화 완료" — 구체적 내용 없음

## 심각도 분류

| 심각도 | 조건 |
|--------|------|
| **HIGH** | `[LOCK-REQUIRED]` 주석 자체가 없음 — 정당화 시도 흔적 없음 |
| **MEDIUM** | 주석은 있으나 WHY-LOCK 또는 CONTENTION-OPT 중 하나가 불충분 |
| **LOW** | 주석 형식이 표준과 다르지만 내용은 충분한 경우 |

## 실제 코드 예시

**HIGH (주석 없음):**
```csharp
lock (_syncRoot)
{
    _items.Add(item);
    _index[item.Id] = item;
}
```

**MEDIUM (CONTENTION-OPT 불충분):**
```csharp
// ===== [LOCK-REQUIRED] =====
// WHY-LOCK: 두 컬렉션을 원자적으로 업데이트해야 함
// CONTENTION-OPT: 없음
// ===========================
lock (_syncRoot) { ... }
```

**Accepted:**
```csharp
// ===== [LOCK-REQUIRED] =====
// WHY-LOCK: _items와 _index 두 컬렉션을 동시에 일관된 상태로 유지해야 함.
//           Interlocked는 단일 참조 교체만 원자적이므로 부적합.
// CONTENTION-OPT: 락 범위를 2줄로 최소화. 유효성 검사(O(n))는 락 진입 전 수행.
//                 읽기가 쓰기보다 10배 많아 ReaderWriterLockSlim 채택 검토했으나
//                 쓰기 시 두 컬렉션 동시 접근으로 UpgradeableLock 복잡도 증가 우려.
// ===========================
lock (_syncRoot) { ... }
```

## 출력 저장

완성된 JSON을 `_workspace/02_lockjustification_findings.json`에 Write한다.
완료 후 리더에게 SendMessage로 알린다.
