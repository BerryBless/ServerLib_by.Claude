# 송신 경로 할당 측정 결과 — P3-잔여①(송신당 CTS) 확정 (2026-06-08)

## 배경

"패킷을 IOCP로 처리하고 싶다"는 요청에서 출발했으나, 현재 코드는 이미 async 소켓 + `System.IO.Pipelines`로 IOCP를 투명하게 사용 중이다("IOCP 도입"은 성능 레버가 아님). 실제 목표는 **성능 병목 해결**이고 측정 전이었다. `plan/perf_review_0604.md`는 stale이며(P2·P7·P8·P3 무한 hang은 이미 코드에 반영됨), 코드 직접 확인으로 남은 후보는 **P3-잔여①: `SendTimeout` 설정 시 송신마다 `CancellationTokenSource` 할당**이었다.

이 문서는 **코드 수정 없이 살아있는 도구만으로** 해당 할당이 실제 핫패스 병목인지 A/B 실측으로 확정한 결과다.

## 측정 환경 / 방법

- 런타임: .NET 10 (Release 빌드), Windows 11, loopback(127.0.0.1) TCP
- 도구: `Client.exe`(부하 생성기) + 측정 계측(`[CLIENTSTATS]`). 삭제된 Benchmark/LoadTest 하네스는 복구하지 않음.
- 워크로드: `Client.exe 8 500000` = 8스레드 × 50만 = **400만 패킷** 송신(IncrementPacket/DecrementPacket, 4B 헤더)
- 격리: 하트비트 OFF(PONG 노이즈 제거), RTT 표시 OFF, idle timeout OFF → **순수 송신 경로**만 측정
- 1순위 지표: `bytesPerPacket` = `GC.GetTotalAllocatedBytes(precise:true)` 델타 ÷ 전송 패킷 수
- A/B: `Client.SendTimeoutSeconds` 토글 (30=ON / 0=OFF). 코드 변경 없이 config로 전환.

## 결과 (각 2회, 완전 일치)

| 지표 | Baseline (SendTimeout=30s) | 실험 (SendTimeout=0) | 변화 |
|------|---------------------------:|---------------------:|------|
| **bytesPerPacket** | **160.03 B** | **0.02 B** | **−99.99%** |
| 총 할당(4M pkt) | 640,118,392 B (≈610 MB) | ~99 KB | −610 MB |
| gen0 GC 횟수 | 34 | **0** | −34 |
| gen1 GC 횟수 | 1 | 0 | −1 |
| 처리량(pkt/s) | 280,799 / 286,682 | 272,019 / 289,622 | 사실상 동일 |

- 재현성: ON 2회 모두 `160.03 / gen0=34`, OFF 2회 모두 `0.02 / gen0=0` — 노이즈 없음.

## 해석

1. **P3-잔여① 확정.** 송신당 linked `CancellationTokenSource`(+타이머+등록)가 클라이언트 송신 핫패스 할당의 **사실상 전부**다(610 MB → 99 KB, gen0 34 → 0). 패킷당 정확히 **160 B**. perf 리뷰가 "P2 패킷당 박싱보다 더 큰 GC를 새로 유발"이라 경고한 그대로다.
2. **처리량은 이 테스트에서 안 변한다 — loopback 네트워크 바운드.** 따라서 이득은 "더 빠른 처리량"이 아니라 **GC 압력 제거**(gen0 churn 0화)다. 실배포(다수 세션·실네트워크)에서 이 160 B/패킷은 그대로 Gen0 압박 → GC 일시정지 → 꼬리지연으로 전이된다.
3. **서버측은 이 설정에서 송신 경로 미작동**(하트비트 OFF→PONG 없음, 에코·브로드캐스트 없음)이라 서버 CTS 할당은 발생 여지가 없어 측정 대상이 아니었다. 클라이언트 부하 루프가 송신 핫패스의 대표 측정점이며 결론은 클라이언트에서 결정적이다. 동일 코드 경로(`SocketPipelineSession.cs:304-307`)가 서버에도 있으므로 서버 송신(PONG/브로드캐스트) 활성 시 동일 할당이 재현된다.

## 결정 (계획의 4단계 규칙 적용)

bytesPerPacket이 OFF에서 160→0.02로 격감하고 gen0이 완전 제거됨 → **규칙상 "P3-잔여① 병목 확정" → 수정안 A로 진행**.

- **수정안 A (채택, 다음 단계):** 게이트가 세션당 송신 1건을 보장하므로 **세션별 재사용 `CancellationTokenSource` + `TryReset()`** 로 핫패스 할당 제거. 대상: `SocketPipelineSession.cs:287-316`, `SocketPipelineClient.cs:249-278`. 측정과 무관하게 안전한 순이득(floor).
  - **사전 검증(A 구현 시):** `CancelAfter` 후 타이머 미발화 정상 경로에서 non-linked CTS의 `TryReset()` 성공 여부 확인. 발화(=피어 죽음) 시는 teardown 경로라 재사용 비대상. caller 토큰 취소가능한 드문 경로는 기존 linked CTS 유지.
  - 검증 방법: 본 측정을 그대로 재실행(`SendTimeout=30` + 수정 A)해 `bytesPerPacket`이 ON 상태에서도 ~0으로 떨어지는지 확인 → 회귀 없이 hang 방어 유지.
- **수정안 B (보류):** 송신 큐(`Channel`) 리팩토링은 이번 측정으로 정당화되지 않음(처리량 병목 아님). `SendAsync` 버퍼 소유권 계약 재설계 지뢰가 있어, 향후 처리량/HOL이 실측 병목으로 확인될 때 별도 설계 사이클로.

## Fix A(A-max) 결과 — 적용 후 재측정 (2026-06-08)

수정안 A를 **A-max**(재사용 CTS + `TryReset()`, caller 토큰 비링크)로 구현(`SocketPipelineSession.cs`·`SocketPipelineClient.cs`: 재사용 `_sendTimeoutCts` 필드, timeout 분기 재작성, `DisposeAsync` 해제, XML 문서 갱신). 동일 워크로드(8스레드×50만=400만, SendTimeout=30s ON) 재측정:

| 지표 | Before (per-send CTS) | After (Fix A, ON) | 결과 |
|------|----------------------:|------------------:|------|
| **bytesPerPacket** | 160.03 | **0.02–0.03** | ON 상태에서도 OFF와 동일(무할당 달성) |
| gen0 GC(4M) | 34 | **0** | 송신 경로 GC 압력 제거 |
| 처리량(pkt/s) | ~283k | ~283–288k | 불변(loopback 바운드, 예상대로) |

**무결성:** 서버 graceful 종료 `[STATS] received=4,000,000 == sent`, `test=0`(증가 200만·감소 200만 균형 정확), 서버 gen0=0 → 할당 제거가 패킷 유실·손상·로직 오류를 유발하지 않음.

**시한 발화 회귀(필수 검증, 임시 하네스로 실증 후 제거):** 드레인하지 않는 피어 + `SendTimeout=1s`로 발화 강제 →
- 첫 시한: `SocketException(SocketErrorCode=TimedOut)` 정상 반환(64KB×4회로 송신 버퍼 포화 후 발화).
- 재할당 경로(직전 발화로 cts 취소됨 → 다음 송신 `TryReset` 실패 → 새 CTS): 크래시·`finally` 예외 없이 다시 `TimedOut` 반환.
- 판정 **PASS**. catch에서 `when (!cancellationToken...)` 필터를 제거(cts.Token만 관찰)했어도 시한→TimedOut 변환 계약 유지 확인.

**서버 송신 경로 검증(하트비트 ON 스모크):** 위 측정은 클라이언트 `SendAsync`만 실행하므로, 별도로 하트비트를 켜고(`Client.exe 2 3000000`) 클라 PING → 서버 `SocketPipelineSession.SendAsync`(재사용 CTS PONG 경로) → 클라 RTT 왕복을 실행했다. 결과: **RTT=0.1ms(>0), 예외 없음, bytesPerPacket=0.04·gen0=0, received=6,000,000==sent.** 두 SendAsync(클라이언트·세션) 모두 정상 실행 확인.

**결론:** P3-잔여①(송신당 160B) 제거 완료. SendTimeout의 hang 방어(P3 본래 목적)는 유지. 공개 계약 소폭 축소(caller 토큰이 in-flight 소켓 쓰기를 즉시 끊지 않고 SendTimeout 내로 bound)는 XML 문서에 명시.

## 남은 측정 계측 (코드에 잔존)

- `Client/Program.cs`: `[CLIENTSTATS]` 출력 + `ClientConfig.SendTimeoutSeconds` 토글(0=비활성) — 수정 A 전후 비교에 재사용.
- `Server/Program.cs`: `[STATS]`에 `allocBytes`(GC.GetTotalAllocatedBytes)·`gen0` 추가.
