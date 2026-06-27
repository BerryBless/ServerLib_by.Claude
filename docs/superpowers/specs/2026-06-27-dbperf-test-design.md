# DbPerfTest — DB 포함 성능 테스트 하네스 설계

**날짜:** 2026-06-27  
**상태:** 승인됨

---

## 1. 배경 및 목적

현재 `SoakTest` 하네스는 네트워크/세션 churn 안정성만 측정하며 DB는 측정 경로 밖에 있다. 실제로 DB가 사용되는 경로는:
- **write**: `LoginRequestPacket`(Id=10) → MySQL SELECT + PBKDF2 + Redis SET
- **read**: `AuthTokenPacket`(Id=12) → Redis GET (1 RTT)

이 하네스는 **MySQL+Redis를 실제 측정 경로에 넣고, 처리량(req/s)과 지연 백분위(p50/p95/p99)를 write/read 분리 보고**하는 것이 목표다.

### 핵심 도전: PBKDF2가 write RTT를 지배

로그인의 클라이언트 RTT는 PBKDF2(수십 ms, CPU-bound)가 지배하므로, RTT만 보면 DB 지연이 보이지 않는다. 이를 해결하기 위해 **서버측 DB 연산(MySQL SELECT, Redis SET/GET)을 별도 Stopwatch로 계측해 `[DBSTATS]` 라인으로 분리 출력**한다.

---

## 2. 설계 결정

### 접근법 비교

| 접근 | 측정 위치 | 채택 여부 |
|------|----------|---------|
| **A. 신규 DbPerfTest 하네스** | 클라 RTT + 서버측 DB 계측 | **✅ 채택** |
| B. SoakTest에 `--workload dbperf` 추가 | 클라 RTT만 | ❌ fire-and-pace 모델 오염 |
| C. BenchmarkDotNet 인프로세스 | LoginService 직접 | ❌ 네트워크 경로 우회 |

**A 채택 이유:** SoakTest의 검증된 child-process 패턴을 재사용하면서, closed-loop 클라이언트로 종단간 RTT를, 서버측 계측으로 순수 DB 지연을 분리할 수 있다. SoakTest의 fire-and-pace 모델을 오염시키지 않는다.

### 핵심 프로토콜 사실 (검증 완료)

- Id=10 로그인 → **Id=11 `LoginResponsePacket`** 응답. `Server\Program.cs:212-234`
- Id=12 AuthToken → **동일 Id=11** 응답. `Server\Program.cs:235-256`. fire-and-forget 아님.
- 헤더에 correlation id 없음 → **연결당 요청 1개 in-flight (closed-loop)**
- 기존 티켓팅 데모의 `Channel<byte[]>` inbox 패턴이 검증된 위치 기반 상관 방식

---

## 3. 컴포넌트 구조

### 신규 프로젝트: `DbPerfTest/`

```
DbPerfTest/
  DbPerfTest.csproj      # net10.0; ServerLib·Auth·AppConfig 참조
  Program.cs             # 오케스트레이터: child spawn → warmup → measure → report
  DbPerfOptions.cs       # CLI 파싱 + read-heavy/balanced 프리셋
  ServerProcess.cs       # Server.exe child-driver (SoakTest에서 적응)
  DbPerfClient.cs        # closed-loop 워커: login prelude → read/write loop
  LatencyRecorder.cs     # write/read 분리 long[] 지연 기록 + 백분위 계산
  DbPerfReport.cs        # 집계·Hard/Soft 판정·출력·exit code
docker-compose.yml       # redis:7 + mysql:8; 헬스체크 + 스키마 부트스트랩
```

### 서버측 DB 계측 수정 (경미)

| 파일 | 변경 | 방법 |
|------|------|------|
| `Auth\LoginService.cs` | MySQL SELECT, Redis SET 구간 계측 | Stopwatch + Interlocked 누적(합·횟수), zero-alloc |
| `Server\Program.cs` | Redis GET(게이트) 구간 계측 + `[DBSTATS]` 출력 | 모니터 루프 신규 라인 추가 |

**`[DBSTATS]` 라인 형식:**
```
[DBSTATS] mysqlSelectAvgUs=123 redisGetAvgUs=45 redisSetAvgUs=67 mysqlCount=100 redisGetCount=500 redisSetCount=100
```
- `[STATS]`와 동일한 ASCII key=value 형식 (머신 파싱 계약 준수)
- 누적 평균(microseconds) — PBKDF2 없는 순수 DB 왕복만 측정

---

## 4. 데이터 흐름

### 워커 1개 (closed-loop)

```
prelude:  LoginRequest(Id=10) 송신 → inbox에서 Id=11 await → token 캐시
warmup:   --warmup-seconds 동안 write/read 반복, 지연 기록 안 함
measure:
  ratio로 read/write 결정 (기본 80:20)
  write:  LoginRequest(Id=10) 송신 → inbox Id=11 await → write latency 기록
  read :  AuthToken(Id=12, 캐시 token) 송신 → inbox Id=11 await → read latency 기록
종료:     raw long[] 병합 → 정렬 → 백분위 계산
```

- **inbox**: `Channel<byte[]>` (unbounded MPSC, lock-free)
- `OnReceived`: `data.Span.ToArray()` 복사 후 enqueue (Span은 콜백 기간만 유효)
- **처리량** = 측정 구간 완료 요청수 / 측정 wall-clock

---

## 5. 정확성 설계

| 포인트 | 내용 |
|--------|------|
| **Warmup 폐기** | `--warmup-seconds`(기본 5s): cold JIT + cold MySQL/Redis 풀 오염 방지 |
| **read-dominant 프리셋** | write는 PBKDF2 cap(코어당 수십/s)이라 DB 병목 측정 불가 → `--read-write-ratio 80:20` 기본 |
| **DB-ready 게이트** | compose 헬스체크(redis PING, mysqladmin ping) → 서버 기동 → `[STATS]` readiness |
| **PBKDF2 분리 보고** | write RTT(종단간)와 `[DBSTATS]` DB 지연을 별도 출력. "login p99"만 헤드라인으로 내지 않음 |
| **Coordinated omission** | closed-loop은 지연 스파이크 과소집계 — 리포트에 known caveat 명기 |

---

## 6. 판정 (`DbPerfReport`)

**Hard checks (FAIL → exit 1):**
- `Crash` — 서버가 `q` 전에 종료
- `ClientErrorRateHigh` — 에러율 > 5%
- `SessionLeak` — 종료 후 `sessions != 0`
- `ThroughputBelowTarget` — req/s < `--target-throughput` (설정 시)
- `LatencyAboveTarget` — read p99 또는 write p99 > `--target-p99-ms` (설정 시)
- `NoDbData` — `[DBSTATS]` 미수신 (vacuous PASS 방지)

**Soft (advisory):** `HeapGrowth` — 최종 heap > 4× baseline

**출력:**
```
[DbPerf] ── write ──  rps=12.3  p50=45ms  p95=72ms  p99=98ms  max=234ms
[DbPerf] ──  read ──  rps=89.4  p50= 2ms  p95= 4ms  p99= 8ms  max= 23ms
[DbPerf] ── dbstats ─  mysql_sel=38µs  redis_get=1.2µs  redis_set=1.1µs
[DbPerf] ── heap ────  baseline=12.3MB  final=13.1MB  growth=6.5%
[DbPerf] ⚠ known caveat: closed-loop은 지연 스파이크를 과소집계합니다
[DbPerf] PASS
```

**Exit codes:** 0=PASS / 1=FAIL / 2=하네스 초기화 실패

---

## 7. docker-compose

```yaml
services:
  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 2s  start_period: 5s

  mysql:
    image: mysql:8
    environment:
      MYSQL_ROOT_PASSWORD: password
      MYSQL_DATABASE: gamedb
    ports: ["3306:3306"]
    volumes:
      - ./Auth/schema.sql:/docker-entrypoint-initdb.d/schema.sql
    healthcheck:
      test: ["CMD", "mysqladmin", "ping", "-h", "localhost", "-ppassword"]
      interval: 3s  start_period: 30s
```

- 시드 유저: 서버 `SeedTestUser=true` 옵션으로 자동 처리
- MySQL 최초 init 느림(~20-30s) → 헬스체크로 DB-ready 게이트
- 재현: `docker compose up -d && dotnet run -c Release --project DbPerfTest -- --clients 20 --duration 30`

---

## 8. CLI

```
DbPerfTest — DB 포함 성능 테스트 하네스

공통:
  --clients N           동시 클라이언트 수       (기본: 20)
  --port N              게임 서버 포트           (기본: 9100)
  --duration N          측정 시간(초)            (기본: 30)
  --warmup-seconds N    warmup 폐기 시간(초)     (기본: 5)
  --read-write-ratio R  R=read:write, e.g. 80:20 (기본: 80:20)
  --attach              외부 서버 부착

판정 임계:
  --target-throughput N  req/s 하한 (미설정 시 무제한)
  --target-p99-ms N      p99 상한 ms (미설정 시 무제한)

서버 DB 오버라이드:
  --redis-conn STR       Redis 연결 문자열
  --mysql-conn STR       MySQL 연결 문자열
  --pbkdf-iterations N   PBKDF2 반복 횟수 (기본: 서버 설정 따름)

프리셋:
  --preset read-heavy    --read-write-ratio 95:5
  --preset balanced      --read-write-ratio 50:50
```

---

## 9. 변경 파일 목록

**신규:**
- `DbPerfTest/` (7파일)
- `docker-compose.yml`

**수정:**
- `Auth/LoginService.cs` — MySQL/Redis Stopwatch 계측 추가
- `Server/Program.cs` — 게이트 Redis GET 계측 + `[DBSTATS]` 라인 출력
- `CLAUDE.md` — 하네스 설명 갱신
- `plan/dbperf_test_0627.md` — 플랜 문서

---

## 10. 검증

1. `docker compose up -d` → services healthy
2. `dotnet build -c Release` → 0 오류
3. `dotnet run -c Release --project DbPerfTest -- --clients 20 --duration 30 --warmup-seconds 5`  
   → write/read 백분위 + `[DBSTATS]` 출력, exit 0
4. `--preset read-heavy` → DB 처리량 병목 관찰
5. `--target-p99-ms 1` → exit 1 (의도적 FAIL)
6. `dotnet test` → 기존 회귀 PASS

---

## 11. 향후 확장

- 좌석조회 MySQL/Redis 영속화 → 진짜 read-heavy 도메인
- HdrHistogram.NET 도입 (초고카운트 시 정밀 백분위)
- open-loop 부하 생성기 + correlation id 헤더 → coordinated omission 해소
- CI `docker compose` 서비스 컨테이너 통합
