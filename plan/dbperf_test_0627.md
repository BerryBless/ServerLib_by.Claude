# DB 포함 성능 테스트 하네스 (DbPerfTest)

## 배경 및 목적

기존 `SoakTest` 하네스는 네트워크/세션 churn 안정성만 측정하며 DB는 측정 경로 밖에 있다. 이 하네스는 **MySQL+Redis를 실제 측정 경로에 넣고 처리량(req/s)과 지연 백분위(p50/p95/p99)를 write/read 분리 보고**하는 것이 목표다.

- **write 경로:** `LoginRequestPacket`(Id=10) → MySQL SELECT + PBKDF2 + Redis SET
- **read 경로:** `AuthTokenPacket`(Id=12) → Redis GET (1 RTT)

PBKDF2(수십 ms, CPU-bound)가 write RTT를 지배하므로 서버측 DB 연산을 별도 Stopwatch로 계측해 `[DBSTATS]` 라인으로 분리 출력한다.

## 설계 결정

SoakTest의 child-process/`[STATS]` readiness/Hard 판정 패턴을 재사용하면서 **closed-loop 클라이언트**(연결당 1 in-flight)로 종단간 RTT를, 서버측 계측으로 순수 DB 지연을 분리한다.

프로토콜상 헤더에 correlation id가 없으므로 연결당 요청 1개 in-flight. 기존 티켓팅 데모의 `Channel<byte[]>` inbox 패턴을 그대로 활용한다.

## 컴포넌트 구조

```
DbPerfTest/
  DbPerfTest.csproj      — net10.0; ServerLib 참조
  Program.cs             — 오케스트레이터: child spawn → warmup → measure → report
  DbPerfOptions.cs       — CLI 파싱 (--clients, --duration, --read-write-ratio 등) + 프리셋
  ServerProcess.cs       — Server.exe child-driver, [STATS]/[DBSTATS] 파싱
  DbPerfClient.cs        — closed-loop 워커 (Channel inbox, 연결당 1 in-flight)
  LatencyRecorder.cs     — write/read 분리 long[] 지연 기록 + 백분위 계산
  DbPerfReport.cs        — 처리량+백분위 집계, Hard/Soft 판정, exit code
docker-compose.yml       — redis:7-alpine + mysql:8, 헬스체크 + 스키마 부트스트랩
```

### 서버측 DB 계측 (경미 수정)

| 파일 | 변경 |
|------|------|
| `Auth/DbMetrics.cs` | 신규: Interlocked lock-free 카운터 (MySQL SELECT / Redis SET / Redis GET) |
| `Auth/LoginService.cs` | MySQL SELECT, Redis SET 구간 Stopwatch 계측 추가 |
| `Server/Program.cs` | Redis GET(게이트) 계측 + `[DBSTATS]` 모니터 라인 추가 |

**`[DBSTATS]` 라인 형식:**
```
[DBSTATS] mysqlSelectAvgUs=123 redisGetAvgUs=45 redisSetAvgUs=67 mysqlCount=100 redisGetCount=500 redisSetCount=100
```

## 핵심 설계 포인트

| 포인트 | 내용 |
|--------|------|
| Warmup 폐기 | `--warmup-seconds`(기본 5s): cold JIT + cold MySQL/Redis 풀 오염 방지 |
| read-dominant 프리셋 | write는 PBKDF2 cap → `--read-write-ratio 80:20` 기본, `read-heavy`(95:5)/`balanced`(50:50) 프리셋 |
| Coordinated omission | closed-loop은 지연 스파이크 과소집계 — 리포트에 known caveat 명기 |
| PBKDF2 분리 보고 | write RTT(종단간)와 `[DBSTATS]` 순수 DB 지연 별도 출력 |

## 판정 (DbPerfReport)

**Hard checks (FAIL → exit 1):**
- `Crash` — 서버가 `q` 전에 종료
- `ClientErrorRateHigh` — 에러율 > 5%
- `SessionLeak` — 종료 후 `sessions != 0`
- `ThroughputBelowTarget` — req/s < `--target-throughput` (설정 시)
- `LatencyAboveTarget` — read/write p99 > `--target-p99-ms` (설정 시)
- `NoDbData` — `[DBSTATS]` 미수신 (vacuous PASS 방지)

**Soft (advisory):** `HeapGrowth` — 최종 heap > 4× baseline

**Exit codes:** 0=PASS / 1=FAIL / 2=하네스 초기화 실패

## 검증

```bash
docker compose up -d
dotnet run -c Release --project DbPerfTest -- --clients 20 --duration 30 --warmup-seconds 5
# → write/read 백분위 + [DBSTATS] 출력, exit 0

dotnet run -c Release --project DbPerfTest -- --preset read-heavy
# → read-dominant 부하, Redis GET 병목 관찰

dotnet run -c Release --project DbPerfTest -- --target-p99-ms 1
# → exit 1 (의도적 FAIL)
```

## 향후 확장

- 좌석조회 MySQL/Redis 영속화 → 진짜 read-heavy 도메인
- HdrHistogram.NET 도입 (초고카운트 시 정밀 백분위)
- open-loop 부하 생성기 + correlation id 헤더 → coordinated omission 해소
- CI `docker compose` 서비스 컨테이너 통합
