# 보스 몹 전투 컨텐츠 설계 문서

**날짜:** 2026-06-12  
**버전:** v1.0.0  
**관련 플랜:** `C:\Users\aaa\.claude\plans\server-calm-kay.md`

---

## 1. 배경 및 목적

기존 `Server/Program.cs`·`Client/Program.cs`는 라이브러리 검증용 카운터 데모(Increment/Decrement)였다. ServerLib 위에 실제 게임 컨텐츠를 얹어 라이브러리의 실용성을 증명하고자 **보스 몹 전투**를 구현했다.

**해결한 문제:**
- HP가 큰 몹 1마리를 서버가 호스팅하고, 여러 클라이언트가 동시에 딜을 넣어 잡는 멀티플레이 전투.
- 다중 I/O 스레드 동시 데미지 적용의 경쟁 조건(race condition) 처리.
- per-hit 브로드캐스트의 N×Hit 2차 증폭 문제 해소.

**확정 요구사항:**
| 요구사항 | 구현 방식 |
|---------|---------|
| HP·사망 실시간 브로드캐스트 | 주기(200ms) HP 브로드캐스트 + 사망 즉시 브로드캐스트 |
| 클라가 데미지 양을 패킷에 담아 전송 | `DamagePacket { int Amount }` |
| 몹 사망 시 풀 HP 리스폰 | `Interlocked.Exchange(ref _hp, _maxHp)` 후 세대 증가 |
| 세션별 누적 데미지·MVP 집계 | `ConcurrentDictionary<Guid,(label, dmg)>` in MobManager |

---

## 2. 설계 결정

| 결정 | 채택 | 대안 | 사유 |
|------|------|------|------|
| HP 동기화 | `long _hp` + `Interlocked.Add` | `lock` | OnReceived가 다중 I/O 스레드에서 동시 실행 → lock-free 원자 연산. lock은 경합 시 스레드 정지 유발. |
| 사망 1회 보장 | `Interlocked.CompareExchange(ref _deathHandled,1,0)` | 별도 lock | 여러 스레드가 `remaining<=0`을 동시에 볼 수 있음. CAS 승자 1개만 사망 처리, 패자는 즉시 반환. |
| HP 브로드캐스트 주기 | 200ms 백그라운드 루프 | per-hit 즉시 브로드캐스트 | per-hit은 N_clients × 총타격수 2차 증폭. 주기 방식으로 브로드캐스트율 고정(5회/초). |
| 딜 집계 소유권 | `MobManager` 내부 `ConcurrentDictionary` | `ISession.Context` 순회 | 단일 책임. 리스폰 시 `Clear()` 1회로 세대 초기화, 세션 컨텍스트 순회 불필요. |
| 안티치트 | `amount<=0` 무시, `amount>MaxHitDamage(10000)` 클램프 | 신뢰 클라이언트 | 악성 패킷이 HP 힐하거나 1샷하는 것 차단. |
| 게임 로직 위치 | `Server/MobManager.cs` (소비자 전용) | ServerLib 내 | 라이브러리는 전송·직렬화만. 의존성 방향(Core→Interface) 유지. |

### 사망/리스폰 동시성 순서 (위반 시 이중 리스폰 또는 세대 오염 발생)

```
1. 딜 스냅샷 → MVP 산출 → _onDeath(deathPkt) 호출
2. _damageBySession.Clear()            ← 세대 집계 초기화
3. Interlocked.Exchange(ref _hp, MaxHp) ← HP 복구
4. Interlocked.Increment(ref _generation)
5. Volatile.Write(ref _deathHandled, 0) ← 마지막에 게이트 해제 (HP 복구 이후에만)
```

> **세대 경계 미세 오차:** 4~5단계 사이 도착한 공격은 새 세대로 귀속되거나 유실될 수 있다. lock 없는 설계의 의도된 트레이드오프, 데모 범위에서 허용.

---

## 3. 컴포넌트 구조

```
ServerLib\Core\Serialization\Packets\
  DamagePacket.cs    (Id=5, struct, int Amount)       ← 클라→서버
  MobHpPacket.cs     (Id=6, struct, long+long+int)    ← 서버→클라 주기
  MobDeathPacket.cs  (Id=7, class,  int+long+string)  ← 서버→클라 사망

Server\
  MobManager.cs      (게임 로직 캡슐화, no deps on Interface/Core internals)
  Program.cs         (게임 호스트 + 주기 브로드캐스트 루프)

Client\
  Program.cs         (스레드별 고정 딜 공격자 + HP바·처치 수신)
```

```
[Client T0~Tn] --DamagePacket(Amount)--> [Server OnReceived]
                                              │ MobManager.ApplyDamage(id, label, amount)
                                              │   Interlocked.Add(ref _hp, -amount)
                                              │   _damageBySession.AddOrUpdate(...)
                                              │   remaining<=0 → TryHandleDeath (CAS)
                                              ▼
[모든 Client] <--MobHpPacket(200ms 루프)------ [백그라운드 Task]
[모든 Client] <--MobDeathPacket(즉시)---------- [onDeath 콜백 → Task.Run → BroadcastAsync]
```

---

## 4. 핵심 API

### 서버 — 데미지 수신 및 몹 상태 관리

```csharp
// MobManager 생성
var mob = new MobManager(maxHp: 100_000, onDeath: deathPkt =>
{
    _ = Task.Run(async () =>
    {
        var buf = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + deathPkt.GetBodySize());
        try { serializer.Serialize(deathPkt, buf); await registry.BroadcastAsync(buf.AsMemory(...)); }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    });
});

// OnReceived에서 데미지 적용
listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort pktId, out _)) return ValueTask.CompletedTask;
    if (pktId == DamagePacket.Id)
    {
        var pkt = serializer.Deserialize<DamagePacket>(data.Span); // 헤더 포함 전체 프레임
        mob.ApplyDamage(session.SessionId, nickname, pkt.Amount);
    }
    return ValueTask.CompletedTask;
};

// 주기 HP 브로드캐스트
_ = Task.Run(async () =>
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(200, ct);
        var (hp, maxHp, gen) = mob.Snapshot();
        var hpPkt = new MobHpPacket { Hp = hp, MaxHp = maxHp, Generation = gen };
        var buf = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + hpPkt.GetBodySize());
        try { serializer.Serialize(hpPkt, buf); await registry.BroadcastAsync(buf.AsMemory(...)); }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
});
```

### 클라이언트 — 공격 + HP 바 수신

```csharp
// 스레드별 고정 딜, 1회 직렬화 후 버퍼 재사용(무할당 hot loop)
int damage = 10 + (i % 5) * 5; // T0=10, T1=15, ...
var dmgBuf = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + 4);
serializer.Serialize(new DamagePacket { Amount = damage }, dmgBuf);
var dmgMem = dmgBuf.AsMemory(0, PacketPool.HeaderSize + 4);
// hot loop
await conn.SendAsync(dmgMem, ct);

// HP 수신 (T0만 출력)
conn.OnReceived = data =>
{
    if (PacketPool.TryParseHeader(data.Span, out ushort pktId, out _) && pktId == MobHpPacket.Id && i == 0)
    {
        var hp = serializer.Deserialize<MobHpPacket>(data.Span);
        // HP 진행 바 출력
    }
    else if (pktId == MobDeathPacket.Id)
    {
        var death = serializer.Deserialize<MobDeathPacket>(data.Span);
        Console.WriteLine($"[처치] MVP={death.MvpName}  topDmg={death.TopDamage:N0}");
    }
    return ValueTask.CompletedTask;
};
```

---

## 5. 변경 파일 목록

| 파일 | 상태 | 내용 |
|------|------|------|
| `ServerLib/Core/Serialization/Packets/DamagePacket.cs` | **신규** | Id=5, struct, int Amount(4B). 클라→서버 공격 패킷. |
| `ServerLib/Core/Serialization/Packets/MobHpPacket.cs` | **신규** | Id=6, struct, long+long+int(20B). 서버→클라 주기 HP. |
| `ServerLib/Core/Serialization/Packets/MobDeathPacket.cs` | **신규** | Id=7, class, int+long+string. 서버→클라 사망/MVP. |
| `Server/MobManager.cs` | **신규** | 보스 몹 상태·lock-free 딜 집계·CAS 사망 처리. |
| `Server/Program.cs` | **재작성** | 게임 호스트: 레지스트리 강제 활성, MobManager, 200ms 브로드캐스트 루프. |
| `Client/Program.cs` | **재작성** | 공격자: 스레드별 고정 딜, HP바·처치 수신, [CLIENTSTATS] 유지. |
| `plan/mob_combat_0612.md` | **신규** | 이 문서. |
| `CLAUDE.md` | **수정** | 플랜 표·Program.cs 예제 설명 업데이트. |

---

## 6. 빌드 검증

```bash
# 1. 전체 솔루션 빌드
dotnet build

# 2. 서버 실행 (터미널 A)
dotnet run --project Server
# 기대: [Server] port 9100 — 보스HP=100,000 ...
# 200ms마다: [Monitor] sessions=N  hp=...  gen=...

# 3. 클라이언트 실행 (터미널 B)
dotnet run --project Client 8 5000
# 기대: T0에서 [HP] [████░░░░░░░░] ... 진행 바 출력
#       몹 사망 시: [처치] gen=1  MVP=전사-xxxx  topDmg=...
#       서버: [KILL] gen=1  mvp=전사-xxxx  topDmg=...

# 4. 다중 클라이언트 동시성 확인
# 터미널 C: dotnet run --project Client 4 5000
# 기대: HP가 음수로 새지 않고 정확히 리스폰, MVP가 최다 딜 스레드와 일치
```

**검증 체크포인트:**
- [ ] 경고 0개, 오류 0개로 빌드 성공
- [ ] 서버 기동 후 200ms 주기 `[Monitor]` 출력
- [ ] 클라 접속 즉시 현재 HP 수신(T0 HP 바 표시)
- [ ] 누적 딜로 HP 감소 → 사망 → `[KILL]` + 리스폰 반복
- [ ] 단일 세대에서 1회만 사망 처리(이중 리스폰 없음)
- [ ] `[CLIENTSTATS]` 출력으로 bytesPerPacket 측정 가능

---

## 7. 향후 확장 포인트

| 우선순위 | 항목 | 비고 |
|---------|------|------|
| 높음 | 딜 랭킹 상위 N 브로드캐스트 | `MobDeathPacket`에 Top-N 배열 추가 또는 별도 `RankPacket` |
| 높음 | 몹 → 클라 공격(역방향 데미지) | `MobAttackPacket(Id=8)` + 클라 HP 상태 |
| 중간 | 다중 몹/스폰 테이블 | `MobManager` 풀 또는 `Dictionary<int, MobManager>` |
| 중간 | 보상/경험치 패킷 | `RewardPacket(Id=9)` + 클라 누적 점수 |
| 낮음 | 몹 HP 바 TUI 렌더 | ANSI escape code로 서버 콘솔에 시각화 |
| 낮음 | RUDP 채널 연동 | 데미지 패킷에 신뢰 전송 보장 필요 시 |
