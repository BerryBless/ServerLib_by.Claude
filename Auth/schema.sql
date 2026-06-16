-- ============================================================
-- ClaudeCodeStudy — 인증 데이터베이스 스키마
-- MySQL 8.0+ / MariaDB 10.5+ 호환
-- 실행: mysql -u root -p < Auth/schema.sql
-- ============================================================

CREATE DATABASE IF NOT EXISTS gamedb
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_unicode_ci;

USE gamedb;

-- users: 사용자 자격증명 테이블
-- password_hash BINARY(32): PBKDF2-SHA256 32바이트 해시
-- salt          BINARY(16): per-user 랜덤 16바이트 salt
CREATE TABLE IF NOT EXISTS users (
    id            BIGINT       AUTO_INCREMENT PRIMARY KEY,
    username      VARCHAR(64)  NOT NULL UNIQUE,
    password_hash BINARY(32)   NOT NULL,
    salt          BINARY(16)   NOT NULL,
    created_at    DATETIME     DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ============================================================
-- 시드 사용자 생성 방법 (두 가지 중 선택)
-- ============================================================
--
-- [방법 A] appsettings.json 토글 사용 (권장, 최초 1회)
--   "Auth": { "SeedTestUser": true, "SeedUsername": "admin", "SeedPassword": "password123" }
--   → dotnet run --project AuthServer (또는 Server) 실행 시 자동으로 admin 사용자를 생성합니다.
--   → 생성 후 SeedTestUser를 false로 되돌리세요.
--
-- [방법 B] 테스트에서 해시 생성 후 수동 INSERT
--   dotnet test --filter "PasswordHasher_GenerateSeedSql"
--   → 출력된 INSERT 문을 복사해 여기에서 실행하세요.
-- ============================================================
