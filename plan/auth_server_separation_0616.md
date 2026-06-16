# 인증 서버 분리 (AuthServer) + 게임서버 토큰 게이팅 — 2026-06-16

## 배경 및 목적

로그인 로직이 게임 Server(9000) 프로세스 안에만 존재해 독립 확장·운영이 불가했음.
사용자 요구: ① 독립 프로세스(AuthServer.exe, 포트 9200) + ② 게임서버 로그인 병행 유지
                + ③ 게임서버 Redis 토큰 게이팅 추가.

## 완료 상태

빌드: 0 오류, 0 경고 (9개 프로젝트 전체)
테스트: 92/92 통과

## 주요 변경 파일

| 파일 | 변경 내용 |
|------|----------|
| Auth/Auth.csproj | 신규 공유 라이브러리 (net10.0, MySqlConnector+Redis) |
| Auth/*.cs | Server/Auth/*.cs → 이동·public 승격 (8개 파일) |
| Auth/schema.sql | 동일 내용 복사 |
| AuthServer/AuthServer.csproj | 신규 Exe (net10.0, Auth+ServerLib+AppConfig 참조) |
| AuthServer/Program.cs | 단일목적 로그인 리스너 (port 9200) |
| AuthServer/appsettings.json | 신규 설정 파일 |
| Auth/ITokenStore.cs | TryGetUserIdAsync 추가 |
| Auth/RedisTokenStore.cs | TryGetUserIdAsync 구현 |
| ServerLib/.../AuthTokenPacket.cs | Id=12, 바디=[Token 길이(2B)|Token UTF-8] |
| AppConfig/AuthServerConfig.cs | 신규 POCO (Port=9200, MaxConnections, Auth) |
| AppConfig/ServerConfig.cs | RequireAuth=false 토글 추가 |
| AppConfig/ClientConfig.cs | AuthPort=9200, EnableAuthGating=false 추가 |
| Server/Server.csproj | Auth 참조 추가, InternalsVisibleTo 제거 |
| Server/Program.cs | RequireAuth 게이팅 가드 + AuthToken 검증 분기 |
| Client/Program.cs | EnableAuthGating 데모 prelude 추가 |
| ServerLib.Tests/*.csproj | Server→Auth 참조 교체 |
| ServerLib.Tests/LoginServiceTests.cs | FakeTokenStore.TryGetUserIdAsync 추가 |
| ClaudeCodeStudy.sln | Auth·AuthServer 프로젝트 등록 |

## E2E 수동 테스트 절차

1. Redis + MySQL 기동
2. AuthServer 기동 (SeedTestUser=true 1회 → 이후 false):
   dotnet run --project AuthServer
3. Server 기동 (RequireAuth=true):
   dotnet run --project Server -- "Server:Features:RequireAuth=true"
4. Client 기동 (EnableAuthGating=true):
   dotnet run --project Client -- "Client:Features:EnableAuthGating=true"
   → T0: 9200 로그인 → 토큰 → 9000에 AuthTokenPacket → [GATE+] → DamagePacket 수락
5. 기존 데모 재확인: RequireAuth=false, EnableAuthGating=false 기본값으로 정상 동작 확인
