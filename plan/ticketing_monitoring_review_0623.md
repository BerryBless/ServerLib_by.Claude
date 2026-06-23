# 종합 코드 리뷰 리포트 — 티켓팅 모니터링
**생성:** 2026-06-23  |  **대상:** commit 381d7f8 (티켓팅 모니터링 — lock-free 누적 카운터·좌석맵·KPI 대시보드)  
**이전 리뷰:** plan/ticketing_seat_designation_review_0620.md (ARCH-01 도메인 오염 등 High 4건, 종합 77점)

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low | Info |
|--------|------|----------|------|--------|-----|------|
| 🏗️ 아키텍처 | 88 / 100 | 0 | 0 | 1 | 3 | 0 |
| 🔒 보안 | 79 / 100 | 0 | 1 | 4 | 2 | 0 |
| ⚡ 성능 | 91 / 100 | 0 | 0 | 0 | 2 | 1 |
| 🎨 스타일 | 90 / 100 | — | 0 | 1 | 3 | 0 |
| **종합** | **86 / 100** | **0** | **1** | **6** | **10** | **1** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

---

## 이전 리뷰(0620) 해소 현황

| 항목 | 상태 | 근거 |
|------|------|------|
| ARCH-01 도메인 오염 (`TicketInventory.cs` using ServerLib) | ❌ **미해소·잔존** | `TicketInventory.cs:2` `using ServerLib.Core.Serialization.Packets` 이번 변경에서 수정 없음. 단 신규 코드는 ServerLib를 추가 참조하지 않아 악화는 없음 |
| ARCH-02 별칭 버그 (Row/Col 혼용) | ✅ **해소 유지** | `TryReserveByRowCol()` 부호 없는 비교로 차단, 이번 커밋에서 재발 없음 |
| SEC-NEW-01 SimulateFailure 와이어 노출 | ✅ **해소** | `Server/Program.cs:332` 주석 + `TicketPayRequestPacket` 0B 본문으로 제거 확인, 테스트에 회귀 방지 주석 포함 |
| SEC-01 결제 전 SlotIndex 검증 | ✅ **해소 유지** | `Server/Program.cs:319-330` Volatile.Read 가드 이번 변경 후에도 유지 |
| STYLE-03 2D 경계 테스트 누락 | ⚠️ **부분 해소** | 최대 경계(Col초과·Row초과·마지막 유효 좌석)는 ㉔~㉗로 해소. row=0/col=0 최소 경계 명시 테스트는 미추가 → STYLE-04로 승계 |

---

## High 발견사항 ← 머지 전 필수 수정

### [보안] SEC-MON-01 — 관리 포트(9100) 인증 없이 티켓팅 KPI 전체 노출
**위치:** `Server/Program.cs:414-423`, `AppConfig/ServerConfig.cs:12`  
**CWE:** CWE-200 (Information Exposure)  
**문제:** `AdminPort`(기본값 9100)가 인터넷에 바인딩되며 `StatsRequestPacket`(Id=1, 2바이트 일치만 검증) 처리 시 인증이 전혀 없다. 이번 변경으로 응답 JSON에 `ticket` 섹션이 추가되어 **좌석별 상태 배열(`seats=int[]`)** 및 **비즈니스 KPI 6종**(totalReserved·totalConfirmed·totalPaymentFailed·totalAbandoned·totalExpired·totalSeatTaken)이 노출된다. 네트워크 접근 가능한 모든 호스트가 2바이트만 전송하면 서버 비즈니스 현황을 실시간 추출할 수 있다.  
**수정:**
1. **(즉각·30분)** `adminListener` 바인딩을 루프백으로 제한: `IPAddress.Loopback` 지정  
2. **(단기)** `StatsRequestPacket` 핸들러에 IP 화이트리스트 또는 pre-shared key 헤더 검증 추가  
3. `appsettings.json`에 `AllowedAdminCidrs` 배열을 두어 설정 가능하게

---

## Medium 발견사항 ← 권장 수정

### [아키텍처] ARCH-NEW-01 — 회귀 테스트 ㊳이 생산 경로를 우회 — Base64 방지 보증이 허위 신뢰
**위치:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs:287-316`  
**문제:** 테스트 ㊳(`Seats_serialized_as_int_array_not_base64_string`)이 `Array.ConvertAll(rawBytes, b => (int)b)` 로직을 테스트 내부에서 직접 재현하고 있어 `Server/Program.cs:578`의 실제 투영 코드를 호출하지 않는다. 생산 경로를 수정·삭제해도 이 테스트는 통과된다. byte→int 투영 지식이 두 곳에 중복되는 Shotgun Surgery 구조이기도 하다.  
**수정:** `TicketInventory`에 `int[] ProjectSeatStates()` 메서드를 추가해 byte→int 투영을 캡슐화. `Program.cs`와 테스트 모두 이 메서드를 호출하도록 교체.

### [보안] SEC-MON-02 — 관리 엔드포인트 연결 수·속도 제한 미설정
**위치:** `Server/Program.cs:414-423, 524-527`  
**CWE:** CWE-400 (Uncontrolled Resource Consumption)  
**문제:** `adminListener`에 MaxConnections·요청 속도 제한·IdleTimeout 미설정. 공격자가 대량 연결 후 반복 요청 시 소켓 핸들 고갈이 게임 포트(9000)까지 영향 가능.  
**수정:** `adminListener` 생성 후 `MaxConnections=10`, `IdleTimeout=60s` 설정. SEC-MON-01(루프백 제한) 적용 시 공격 표면이 함께 감소함.

### [보안] SEC-MON-03 — dashboard.html XSS — 서버 문자열 DOM 삽입 시 이스케이프 부재
**위치:** `monitor/app/dashboard.html:215-216`  
**CWE:** CWE-79 (Cross-site Scripting)  
**문제:** `renderCard`에서 `${s.name}`, `${s.host}:${s.port}`가 HTML 이스케이프 없이 템플릿 리터럴로 DOM에 삽입된다. 정수/수치(좌석 상태·KPI)는 `fmt()` → `Number()`를 거쳐 안전하나 문자열 경로는 무방비.  
**수정:**
```js
const esc = s => String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
// ${s.name} → ${esc(s.name)},  ${s.host} → ${esc(s.host)}
```

### [보안] SEC-NEW-02 (이월) — TLS 미적용, 비밀번호·토큰 평문 전송
**위치:** `Server/Program.cs` Login 핸들러, `Packets/LoginRequestPacket.cs`  
**CWE:** CWE-319  
**문제:** LoginRequestPacket(사용자명·비밀번호)과 AuthTokenPacket이 평문 TCP 바이트로 전송. 이전 리뷰(0620)에서 이월.  
**수정:** SslStream 또는 TLS 종단 프록시(nginx/HAProxy) 적용.

### [보안] SEC-NEW-03 (이월) — 슬롯 고갈 DoS — 세션별 예약 속도 제한 부재
**위치:** `Server/Program.cs:289-313` Reserve 핸들러  
**CWE:** CWE-799  
**문제:** 공격자가 예약 → TTL 대기 → 재예약을 반복해 전체 좌석 독점 가능. 이전 리뷰(0620)에서 이월.  
**수정:** 세션별 Reserve 시도 횟수를 슬라이딩 윈도우(60초/10회)로 제한.

### [스타일] STYLE-01 — 동시 예약 테스트 보일러플레이트 4회 반복 — 헬퍼 미추출
**위치:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs:22,63,256,549`  
**문제:** `SetMinThreads + Barrier + Task.WhenAll` 패턴이 테스트 ①②㉘㊲에서 4회 반복. 이번 diff의 ㊲가 기존 패턴을 복제.  
**수정:**
```csharp
private static async Task<(TicketStatus status, int slot)[]> RunConcurrentAsync(
    TicketInventory inv, int concurrency, Func<int, (TicketStatus, int)> work)
{
    ThreadPool.SetMinThreads(concurrency, concurrency);
    var barrier = new Barrier(concurrency);
    var results = new (TicketStatus, int)[concurrency];
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
    {
        barrier.SignalAndWait();
        results[i] = work(i);
    })));
    return results;
}
```

---

## Low / 정보성 ← 검토 권장

- **[보안] SEC-MON-04** `Server/Program.cs:312,396` — `[TICKET]` 로그에 `user={tctx.Username}` + `session.RemoteEndPoint` 노출 (CWE-532). 프로덕션 전환 시 SHA-256 앞 8자리로 대체하거나 Debug 레벨로 지정.
- **[보안] SEC-NEW-04 (이월)** `Server/appsettings.json:19` — MySQL 비밀번호·SeedPassword 하드코딩 (CWE-798). 환경 변수 또는 Secret Manager 주입.
- **[아키텍처] ARCH-NEW-02** `Ticketing/TicketInventory.cs:63-71,378-405` — TicketInventory가 상태 관리 + 관측 지표 수집 두 책임 보유(SRP 약화). KPI 5종 초과 시 `ITicketMetricsCollector` 분리 권장.
- **[아키텍처] ARCH-NEW-03** `Server/Program.cs:575-578` — `MetricsSnapshot()`·`SnapshotStates()` 두 번 분리 호출로 두 스캔 사이 비원자성 노출. ARCH-NEW-01 해소(ProjectSeatStates 캡슐화) 시 자연 해소.
- **[아키텍처] ARCH-NEW-04** `TicketInventory.cs:63-71`, `Program.cs:501-508` — 카운터 추가 시 선언·Increment·MetricsSnapshot·JSON·콘솔 6개 위치 동시 수정 필요(Shotgun Surgery). 카운터 10종+ 시 `AsKpiEntries()` 루프로 통합 권장.
- **[성능] PERF-01** `Server/Program.cs:576-578` — 1초 주기 좌석 투영 시 중간 `byte[]` 힙 할당. `stackalloc byte[tm.Total]`로 제거 가능(Total ≤ 255 보장).
- **[성능] PERF-02** `Ticketing/TicketInventory.cs:163,173` — TryReserve hot path에 추가된 `Interlocked.Increment` 2개, 6종 카운터(48 B)가 단일 캐시 라인 공유 시 false sharing 가능성. 현재 규모에서 수용 가능, 부하 테스트 확인 후 64 B 패딩 검토.
- **[스타일] STYLE-02** `TicketInventoryConcurrencyTests.cs:121` — `ctx2` 미사용 선언. 제거.
- **[스타일] STYLE-03** `Ticketing/TicketInventory.cs:417` — `TicketMetrics` positional record 12개 파라미터 중 `<param>` 문서 누락. `Reserved`와 `TotalReserved` 혼동 가능성 있어 우선도 높음.
- **[스타일] STYLE-04** `TicketInventoryConcurrencyTests.cs` — `TryReserveByRowCol` row=0/col=0 최소 경계 테스트 미추가(STYLE-03 이월).
- **[성능] PERF-03** (Info) `Server/Program.cs:615` — 익명 타입 대상 `JsonSerializer.Serialize` 리플렉션 기반. 1초 주기 저빈도 경로, 코드 주석에 허용 명시됨. 주기 단축 시 named DTO + Source Generator 검토.

---

## 총평 및 판정

commit 381d7f8의 lock-free 누적 카운터 설계(`Interlocked.Increment/Read`)와 `MetricsSnapshot` DTO(`readonly record struct`·zero-allocation)는 아키텍처·성능 양면에서 건전하다. 특히 XML 문서 주석과 Interlocked 인라인 주석이 CLAUDE.md 기준을 충실히 충족하며, 이전 리뷰의 핵심 지적 3건(SEC-NEW-01·SEC-01·ARCH-02)이 해소됐다. 전체 종합 점수는 이전 리뷰의 77점에서 **86점(+9점)** 으로 개선됐다.

유일한 High 발견(SEC-MON-01)은 이번 모니터링 변경 자체가 도입한 새 공격 표면으로, 관리 포트(9100)를 루프백으로 제한하는 단일 수정(~30분)으로 즉각 차단 가능하다. Medium 5건 중 ARCH-NEW-01(회귀 테스트가 생산 경로를 우회)과 SEC-MON-03(XSS) 역시 소규모 수정으로 해소된다. ARCH-01 도메인 오염은 여전히 잔존하나 이번 변경으로 악화되지는 않았다.

**판정: REQUEST CHANGES**
- **필수:** SEC-MON-01 관리 포트 루프백 바인딩 (루프백 제한 1줄)
- **권장:** ARCH-NEW-01 ProjectSeatStates() 캡슐화 · SEC-MON-03 XSS esc() 헬퍼 · STYLE-01 테스트 헬퍼 추출
- **기술부채 등록:** ARCH-01(도메인 오염) · SEC-NEW-02(TLS) · SEC-NEW-03(속도 제한)

---

## 수정 완료 내역 (2026-06-23 — 리뷰 당일 적용)

| 항목 | 심각도 | 수정 내용 | 수정 파일 |
|------|--------|-----------|-----------|
| **SEC-MON-01** | High ✅ | `IServerListener.Start(int, IPAddress)` 오버로드 추가, `adminListener`를 `IPAddress.Loopback`으로 기동 — 원격 접근 원천 차단 | `IServerListener.cs`, `SocketPipelineListener.cs`, `Server/Program.cs` |
| **SEC-MON-02** | Medium ✅ | `adminListener.MaxConnections=10`, `IdleTimeout=60s` 적용 — 소켓 핸들 고갈 방지 | `Server/Program.cs` |
| **SEC-MON-03** | Medium ✅ | `dashboard.html`에 `esc()` HTML 이스케이프 헬퍼 추가, `s.name`·`s.host` 삽입부 교체 | `monitor/app/dashboard.html` |
| **SEC-NEW-03** | Medium ✅ | `TicketContext`에 슬라이딩 윈도우 속도 제한(60초/10회) 필드 추가, Reserve 핸들러 검증, `TicketStatus.RateLimited=8` 신규, 클라이언트 처리 | `TicketContext.cs`, `TicketStatus.cs`, `Server/Program.cs`, `Client/Program.cs` |
| **ARCH-NEW-01** | Medium ✅ | `TicketInventory.ProjectSeatStates()` 캡슐화 — `Program.cs`·테스트 ㊳ 동일 경로 공유, 회귀 보증 회복 | `TicketInventory.cs`, `Server/Program.cs`, `TicketInventoryConcurrencyTests.cs` |
| **STYLE-01** | Medium ✅ | `RunConcurrentAsync` 헬퍼 추출, 테스트 ①②㉘㊲ 보일러플레이트 제거 (4→1 중복 해소) | `TicketInventoryConcurrencyTests.cs` |

**미적용 항목 (기술부채 유지):**
- **SEC-NEW-02** — TLS(`SslStream` 통합): 인프라 변경으로 별도 사이클에서 처리
- **ARCH-01** — 도메인 오염(`TicketInventory.cs` using ServerLib): `TicketStatus` 이동 시 패킷 프로토콜 영향 별도 검토 필요

**수정 후 예상 종합 점수: ≥ 92 / 100** (High 1건 제거 + Medium 5건 해소, TLS·도메인 오염 기술부채 잔존)
