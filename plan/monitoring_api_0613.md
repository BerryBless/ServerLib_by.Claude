# 모니터링 API 설계 (FastAPI + 게임 서버 self-report)

**날짜:** 2026-06-13  
**버전:** v1.0

---

## 1. 배경 및 목적

게임 서버(`Server`, TCP :9000, 보스 몹 전투 호스트)의 운영 지표를 외부에서 실시간으로 확인할 수 있는 독립 모니터링 API와 대시보드를 구축한다.

**문제:** 기존 서버는 stdout `[STATS]` 라인으로만 지표를 출력하며, HTTP·파일·IPC 출구가 없어 외부 시스템에서 세션 수·메모리·CPU를 읽을 방법이 없었다.

**요구사항:**
- 원격·다수 게임 서버 지원 (중앙집중형 수집)
- ① 프로세스 스레드별 CPU, ② 메모리(프로세스+호스트), ③ 세션 수 수집
- FastAPI 기반 REST JSON + 미니 웹 대시보드 노출

---

## 2. 설계 결정

### 핵심 구조 결정

| 결정 | 채택 | 대안/이유 |
|------|------|-----------|
| 세션 수 연동 | **별도 관리 포트 9100** | 9000 재사용 시 모니터 접속이 게임 세션 카운트 오염 + 30s idle-timeout 단절 |
| 데이터 전달 | **게임 서버가 self-report** | 원격 모니터가 psutil로 원격 프로세스 읽기 불가(로컬 프로세스 전용) |
| CPU% 계산 | **서버 측 고정주기(1s) 백그라운드 샘플러** | TotalProcessorTime은 누적값, 요청당 계산 시 다중 모니터가 서로의 직전 스냅샷 오염 |
| 스탯 페이로드 | **JSON-in-packet 본문** | 가변 스레드 수 + 저빈도 관리 경로 → MobDeathPacket 문자열 선례 부합 |
| 호스트 CPU | **PerformanceCounter (Windows 우선)** | 비Windows: null 반환. GC.GetGCMemoryInfo()는 크로스플랫폼 |
| 수집 코드 위치 | **Server 프로젝트만** | ServerLib에 PerformanceCounter 등 플랫폼 의존 넣으면 Core 오염 |
| FastAPI 모니터 | **독립 서비스 (별도 프로세스)** | 게임 서버와 생명주기 분리, 다수 서버 중앙 집계 |

### 프로토콜 계약

```
와이어: [PacketId 2B LE] [BodyLength 2B LE] [Body N bytes]

StatsRequest  (Id=8): 4바이트, Body 없음
StatsResponse (Id=9): 4+N 바이트, Body = UTF-8 JSON 원시 바이트(길이 접두어 없음)

Python 요청:  struct.pack('<HH', 8, 0)
Python 수신:  hdr=read(4) → id,body_len=unpack('<HH',hdr) → body=read(body_len) → json.loads(body)
```

---

## 3. 컴포넌트 구조

```
ClaudeCodeStudy/
├── ServerLib/
│   └── Core/Serialization/Packets/
│       ├── StatsRequestPacket.cs   [신규] Id=8, struct, 본문 0바이트
│       └── StatsResponsePacket.cs  [신규] Id=9, class, JSON UTF-8 바이트 본문
├── AppConfig/
│   └── ServerConfig.cs             [수정] AdminPort:9100, MonitorSampleIntervalMs:1000
├── Server/
│   ├── appsettings.json            [수정] 동일 키 추가
│   ├── Server.csproj               [수정] System.Diagnostics.PerformanceCounter 패키지
│   └── Program.cs                  [수정] CPU 샘플러 + 관리 리스너 + JSON 스냅샷
└── monitor/
    ├── servers.json                 게임 서버 목록 + pollIntervalSec
    ├── requirements.txt             fastapi, uvicorn[standard]
    └── app/
        ├── protocol.py             바이너리 프레임 인코딩/디코딩
        ├── state.py                서버별 스냅샷 공유 저장소
        ├── collector.py            서버별 asyncio TCP 폴러
        ├── main.py                 FastAPI 앱 + 라우트 + lifespan
        └── dashboard.html          미니 웹 대시보드 (바닐라 JS, 2초 자동 갱신)
```

### 아키텍처 흐름

```
[게임 서버 :9100]  ──TCP(바이너리)──▶  [Collector Task]  ──캐시──▶  [FastAPI]  ──HTTP/HTML──▶  [브라우저]
   CPU 샘플러 Task                       asyncio 루프           REST JSON + 대시보드
   PerformanceCounter                     state.py
   Process.Threads delta                  servers.json
   GC.GetGCMemoryInfo                     /api/metrics
   StatsResponsePacket{Json=...}          /api/metrics/{name}
                                          /
                                          /healthz
```

---

## 4. 핵심 API

### 관리 패킷 (ServerLib)

```csharp
// 요청 (Python → 서버)
public struct StatsRequestPacket : IPacket { public const ushort Id = 8; ... }

// 응답 (서버 → Python)
public sealed class StatsResponsePacket : IPacket
{
    public const ushort Id = 9;
    public string Json { set; }    // UTF-8 인코딩 후 캐시
    ...
}
```

### JSON 스냅샷 스키마

```json
{
  "timestampUnixMs": 1718280000000,
  "sessions": 42,
  "mob": { "hp": 87654, "maxHp": 100000, "gen": 3 },
  "process": {
    "workingSetBytes": 52428800,
    "gcHeapBytes": 4194304,
    "threadCount": 12,
    "threadsTruncated": false,
    "threads": [{ "id": 1234, "cpuPercent": 12.5 }]
  },
  "host": {
    "logicalCores": 16,
    "cpuPerCorePercent": [5.2, 8.1, 3.0],
    "cpuTotalPercent": 5.4,
    "memoryLoadBytes": 8589934592,
    "totalAvailableMemoryBytes": 17179869184
  }
}
```

비Windows: `host.cpuPerCorePercent`, `host.cpuTotalPercent` → `null`.

### FastAPI 엔드포인트

```
GET /                     HTML 대시보드 (2초 자동 갱신)
GET /api/metrics          전체 서버 스냅샷 목록
GET /api/metrics/{name}   단일 서버
GET /healthz              { "status": "ok" }
```

### 관리 리스너 (Server/Program.cs)

```csharp
// 게임 세션 레지스트리와 완전 분리 — 세션 카운트 오염 없음
IServerListener adminListener = ServerNet.CreateListener();   // registry=null
adminListener.OnReceived = async (session, data) => {
    if (id == StatsRequestPacket.Id)
        await session.SendAsync(new StatsResponsePacket { Json = statsHolder.Json });
};
// IdleTimeout 미설정 — 모니터 5초 주기 폴링이 30s 게임 timeout에 끊기지 않음
adminListener.Start(cfg.AdminPort);   // :9100
```

---

## 5. 변경 파일 목록

| 파일 | 작업 | 핵심 내용 |
|------|------|----------|
| `ServerLib/Core/Serialization/Packets/StatsRequestPacket.cs` | 신규 | Id=8, struct, 0바이트 본문 |
| `ServerLib/Core/Serialization/Packets/StatsResponsePacket.cs` | 신규 | Id=9, class, UTF-8 JSON 본문 |
| `ServerLib/Core/Serialization/SpanReader.cs` | 수정 | `ReadRemainingBytes()` 헬퍼 추가 |
| `AppConfig/ServerConfig.cs` | 수정 | `AdminPort`, `MonitorSampleIntervalMs` |
| `Server/appsettings.json` | 수정 | `AdminPort:9100`, `MonitorSampleIntervalMs:1000` |
| `Server/Server.csproj` | 수정 | `System.Diagnostics.PerformanceCounter` 패키지 |
| `Server/Program.cs` | 수정 | CPU 샘플러 Task + 관리 리스너 + StatsHolder |
| `monitor/app/protocol.py` | 신규 | 바이너리 프레임 인코딩/디코딩 |
| `monitor/app/state.py` | 신규 | 서버별 스냅샷 공유 저장소 |
| `monitor/app/collector.py` | 신규 | 서버별 asyncio TCP 폴러 |
| `monitor/app/main.py` | 신규 | FastAPI 앱 + 라우트 |
| `monitor/app/dashboard.html` | 신규 | 미니 웹 대시보드 |
| `monitor/servers.json` | 신규 | 서버 목록 설정 |
| `monitor/requirements.txt` | 신규 | fastapi, uvicorn |

---

## 6. 빌드 검증

### C# 서버

```powershell
dotnet build ClaudeCodeStudy.sln
dotnet run --project Server
# 출력 확인: "[Server] port 9000", "[Admin] 관리포트 9100"
netstat -ano | findstr 9100   # LISTENING 확인
```

### FastAPI 모니터

```powershell
cd monitor
pip install -r requirements.txt
uvicorn app.main:app --port 8080
# GET http://localhost:8080/api/metrics
# GET http://localhost:8080/
```

### 엔드투엔드

1. 서버 실행 → 클라이언트 접속 → `/api/metrics`의 `sessions` 증가 확인
2. 모니터 연결이 게임 `ActiveSessionCount`에 포함되지 않음(관리 포트 분리 검증)
3. 스레드별 CPU%가 부하 시 상승, 호스트 per-core가 시스템 부하와 일치

---

## 7. 향후 확장 포인트

- `servers.json`에 서버 추가만으로 다수 게임 서버 모니터링 확장
- 히스토리 저장(InfluxDB·Prometheus push) — collector.py에 export 레이어 추가
- WebSocket SSE로 실시간 push 전환 (현재 2초 폴링)
- 관리 포트 TLS + 인증 토큰 (StatsRequestPacket에 토큰 필드 추가)
- StatsResponsePacket에 패킷 처리량(pkt/s) 필드 추가 (`windowPackets` 활용)
