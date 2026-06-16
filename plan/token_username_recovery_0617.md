# 토큰 게이팅 시 Username 복원 — 2026-06-17

## 배경 및 목적

2026-06-16 AuthServer 독립 분리 사이클에서 게임서버 토큰 게이팅(`AuthTokenPacket` Id=12)을 추가했으나,
Redis 토큰 저장소가 토큰→userId만 보관하고 username은 저장하지 않았다.
그 결과 `AuthContext.Username`이 빈 문자열로 채워진 채 "차기 정교화 포인트"로 남아 있었다.

이 사이클에서 그 미완성을 정리한다: 로그인 시 userId와 함께 username을 Redis에 저장하고,
게이팅 경로에서 이를 복원해 `AuthContext.Username`을 올바르게 채운다.

## 설계 결정

### 저장 포맷: delimited String (`"{userId}:{username}"`) — Redis Hash 아님

| 비교 항목 | delimited String | Redis Hash |
|---------|-----------------|-----------|
| 저장 RTT | 1 (StringSet이 TTL 원자 첨부) | 2 (HashSet에 expiry 오버로드 없음 → 별도 KeyExpire) |
| 원자적 TTL | 예(단일 호출) | 아니오(set+expire 경합 창) |
| 레거시 키 호환성 | graceful: `Split(':',2)` → userId 파싱, username `""` | WRONGTYPE 예외 |
| 기존 XML "1 RTT/원자적 TTL" 주석 | 유효 유지 | 거짓이 되어 재작성 필요 |

userId는 `long`이라 항상 숫자 → 콜론 포함 불가(구분자 충돌 없음).
단일 스칼라 필드 추가에는 String이 엄격히 더 단순·저위험.

### 인터페이스 교체: `TryGetUserIdAsync` → `TryResolveAsync`

- 비-테스트 호출처가 `Server/Program.cs` 단 한 곳 → 추가 아닌 교체 (Interface는 순수 추상화 원칙)
- 신규 `readonly record struct TokenInfo(long UserId, string Username)` 도입
  - `LoginResult` 패턴과 동일: readonly record struct로 힙 할당 최소화
  - `AuthContext`(Token 필드 보유) 재사용은 레이어 스멜 → 전용 레코드

## 컴포넌트 구조

```
Auth/
  ITokenStore.cs         ← StoreAsync 시그니처 + TryResolveAsync 교체, TokenInfo 추가
  RedisTokenStore.cs     ← "{userId}:{username}" 포맷 저장/파싱
  LoginService.cs        ← StoreAsync(username 추가 전달)
  AuthContext.cs         ← XML 주석 갱신 (Username 빈 문자열 설명 제거)
Server/
  Program.cs             ← TryResolveAsync 호출, [GATE+] 로그 username 출력
ServerLib.Tests/
  LoginServiceTests.cs   ← FakeTokenStore 갱신, 기존 테스트 수정, 신규 라운드트립 테스트
```

## 핵심 API 변경

```csharp
// Auth/ITokenStore.cs
public readonly record struct TokenInfo(long UserId, string Username);

Task StoreAsync(string token, long userId, string username, TimeSpan ttl, CancellationToken ct = default);
Task<TokenInfo?> TryResolveAsync(string token, CancellationToken ct = default);

// Auth/RedisTokenStore.cs — StringSetAsync 단일 호출(원자적 TTL 유지)
await db.StringSetAsync($"auth:session:{token}", $"{userId}:{username}", ttl);
// TryResolveAsync — Split(':',2): [userId, username], 레거시 값은 [userId]만 → username=""
var parts = ((string)val!).Split(':', 2);
return new TokenInfo(userId, parts.Length > 1 ? parts[1] : string.Empty);

// Server/Program.cs — 게이팅 경로
var info = await tokenStore.TryResolveAsync(tok.Token);
session.Context = new AuthContext(info!.Value.UserId, info.Value.Username, tok.Token);
Console.WriteLine($"[GATE+] ... user={info.Value.Username}  userId={info.Value.UserId} ...");
```

## 변경 파일 목록

| 파일 | 변경 내용 |
|------|----------|
| `Auth/ITokenStore.cs` | `TokenInfo` 레코드 추가; `StoreAsync` username 파라미터 추가; `TryGetUserIdAsync` → `TryResolveAsync` 교체; XML 문서 갱신 |
| `Auth/RedisTokenStore.cs` | `StoreAsync`: `"{userId}:{username}"` 포맷 저장; `TryResolveAsync`: GET→Split 파싱·graceful 레거시; 인라인 주석 |
| `Auth/LoginService.cs` | `StoreAsync` 호출에 `user.Username` 추가 |
| `Auth/AuthContext.cs` | `Username` 파라미터 XML 주석 갱신("빈 문자열일 수 있습니다" 제거) |
| `Server/Program.cs` | `TryResolveAsync` 호출, `AuthContext` username 주입, `[GATE+]` 로그 username 출력 |
| `ServerLib.Tests/LoginServiceTests.cs` | `FakeTokenStore` Stored 튜플 Username 추가, `StoreAsync`/`TryResolveAsync` 갱신; `StoresTokenInTokenStore` arity 수정 + username 검증; 신규 라운드트립 테스트 |

## 빌드 검증

```
dotnet build    →  0 오류, 0 경고
dotnet test     →  93/93 통과 (기존 92 + 신규 1: LoginAsync_ValidCredentials_TokenStorePreservesUsername)
```

## 향후 확장 포인트

- 인증 서버 보안 강화: 속도 제한(B2), 인증 전 half-open 분리(B4) — `security_audit_0609.md` 미구현 권고
- 관리 포트 TLS + 인증 토큰: `monitoring_api_0613.md` 향후 확장 항목
- 게임플레이 확장: 딜 랭킹 `RankPacket`, 역방향 데미지 `MobAttackPacket(Id=8)`
