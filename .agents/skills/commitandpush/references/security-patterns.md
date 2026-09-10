# 민감 정보 탐지 패턴 레퍼런스

## 금지 파일 — 이름/확장자 기준

```
# 환경 변수 파일
.env
.env.local
.env.development
.env.production
.env.staging
.env.test
# .env.example 은 허용 (샘플 파일)

# 인증서/키 파일
*.pem
*.key
*.p12
*.pfx
*.jks
*.cer
*.crt  (내용에 PRIVATE KEY 포함 시만)
id_rsa
id_rsa.pub  (개인키 아니지만 쌍으로 존재 시 경고)
id_ed25519
id_ecdsa
*.ppk

# 자격증명 설정 파일
credentials
credentials.json
credentials.yaml
secrets.json
secrets.yaml
secrets.yml
service-account.json
```

## 금지 콘텐츠 패턴 (diff +줄 스캔)

### 개인키/인증서
```regex
-----BEGIN (RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY( BLOCK)?-----
```

### AWS
```regex
AKIA[0-9A-Z]{16}
ASIA[0-9A-Z]{16}
aws_secret_access_key\s*[=:]\s*[A-Za-z0-9+/]{40}
AWS_SECRET\s*[=:]\s*\S+
```

### GitHub / GitLab / Bitbucket
```regex
ghp_[0-9a-zA-Z]{36}
gho_[0-9a-zA-Z]{36}
github_pat_[0-9a-zA-Z_]{82}
glpat-[0-9a-zA-Z\-]{20}
```

### JWT 토큰
```regex
eyJ[A-Za-z0-9-_=]{10,}\.[A-Za-z0-9-_=]{10,}\.[A-Za-z0-9-_.+/=]{10,}
```

### 하드코딩 비밀번호/키 (변수명 기반)
```regex
(password|passwd|pwd)\s*[=:]\s*["'][^"'$\{]{6,}["']
(secret|secret_key|secretkey)\s*[=:]\s*["'][^"'$\{]{8,}["']
(api_key|apikey|api-key)\s*[=:]\s*["'][A-Za-z0-9+/\-_]{16,}["']
(access_token|auth_token|bearer_token)\s*[=:]\s*["'][A-Za-z0-9+/\-_.]{20,}["']
(private_key|privatekey)\s*[=:]\s*["'][^"'$\{]{16,}["']
```
> 대소문자 무시(case-insensitive)로 적용

### 데이터베이스 연결 문자열
```regex
(mongodb|mongodb\+srv|postgresql|postgres|mysql|mssql|sqlserver|redis)://[^@\s]+:[^@\s]+@
Server=.+;(Password|Pwd)=[^;]{4,}
Data Source=.+;Password=[^;]{4,}
```

### 구글 / Stripe / Twilio / Slack
```regex
AIza[0-9A-Za-z\-_]{35}
ya29\.[0-9A-Za-z\-_]+
sk_(live|test)_[0-9a-zA-Z]{24}
AC[a-z0-9]{32}
SK[a-z0-9]{32}
xox[baprs]-([0-9a-zA-Z]{10,48}-)
```

### Azure / GCP
```regex
[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}  # GUID형 키 (Context 필요)
AZURE_.*?(KEY|SECRET|TOKEN|PASSWORD)\s*[=:]\s*\S+
```

## 허용 예외 (False Positive 방지)

아래 경우는 PASS 처리:
- 테스트용 플레이스홀더: `"YOUR_API_KEY"`, `"<REPLACE_ME>"`, `"your-secret-here"`, `"example"`, `"test"`, `"demo"`
- 주석 처리된 줄 (예: `// api_key = "real_key"` → 주석은 경고만)
- `.gitignore`에 이미 포함된 파일 유형 (단, 현재 diff에 포함되면 그래도 차단)
- 예시 파일: `*.example`, `*.sample`, `*.template`

## 심각도 분류

| 심각도 | 유형 | 처리 |
|--------|------|------|
| CRITICAL | 개인키, AWS 자격증명, 비밀번호 하드코딩 | 즉시 FAIL, 커밋 완전 차단 |
| HIGH | API 키, OAuth 토큰, DB 연결 문자열 | FAIL, 커밋 차단 |
| MEDIUM | 이메일 주소 노출, 내부 도메인명 | WARN, 사용자 확인 후 진행 가능 |
| LOW | .env 파일 추적(내용 없음), 키 변수명(값 없음) | INFO, 로그만 남김 |

CRITICAL/HIGH → 무조건 FAIL
MEDIUM → 사용자 선택
LOW → PASS (로그 포함)
