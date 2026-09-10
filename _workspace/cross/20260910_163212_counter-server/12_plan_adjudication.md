# 12_plan_adjudication — Plan 의견 조정 (라운드 1)

조정자: 오케스트레이터(메인 세션). 입력: 10_claude_plan.md, 10_codex_plan.md, 11_claude_check_of_codex_plan.md, 11_codex_check_of_claude_plan.md (meta status=success 확인).

## 합의 확인 (양측 독립 수렴 — 쟁점 아님)
Id=3·4 재사용 / 조회·응답 패킷 Id=18·19 신설 / 조회 본문 0B / 패킷 위치 ServerLib Packets / 헤더 id 기준 라우팅(Deserialize<T>가 id 미검증이므로) / 세션 순차 디스패치(`SocketPipelineSession.cs:197`)에 기반한 "연결별 완료 확인 후 최종 조회" 배리어 / 신규 `CounterExample.Tests` / Interlocked 기반 공유 상태.

## 조정 결정

| # | 쟁점 (출처) | 판정 | 근거 |
|---|---|---|---|
| D1 | 통합 베이스 계획 | **Codex 계획 채택** | 양측 동의. 범위가 작고(사용자 "간단하게") 실패 처리·실행 전제(서버 재시작)·시나리오 공유가 구체적. Claude 검토도 "재실행 정책은 내 계획에 없던 구멍" 인정 |
| D2 | unsafe(비원자) 토글 (Claude안 핵심 / Codex P-X7) | **기각 — 이번 범위 제외** | 요구는 "경합 발생 + 올바른 최종값"이며 P-X7 타당(과도 설계·SpinWait이 IO 스레드 직렬 정지). Claude 검토도 심각도 미부여·리스크 인정. **후속 확장 후보로 최종 보고에 기록** |
| D3 | 응답 본문 8B vs 16B | **16B 채택** (`Value`+`AppliedOps`, AppliedOps는 항상 Interlocked) | Codex 종합이 직접 권장("상쇄된 누락을 보완하는 진단값"). 8+8=16B, `checked` 기대값 계산과 함께 상쇄 은폐 방지 |
| D4 | Codex §6 "마지막 명령 지연" 테스트 (Claude P-C1: 자기모순) | **채택(P-C1) — 해당 테스트 항목 삭제** | 순차 디스패치 구조상 마지막 op 지연은 같은 연결의 조회도 지연시켜 의도를 증명 못함 + seam 파일이 §2에 없음. 배리어 정확성은 시나리오 공유(E2E가 실제 `CounterScenario` 실행) + 상태 동시성 테스트로 커버 |
| D5 | 클라 무응답·실패 종료 절차 (Codex P-X1) | **채택** | 베이스 계획 §5에 이미 구체화됨(전체 기한·취소 전파·전 연결 정리·비영 종료 코드) |
| D6 | 문서 갱신 누락 (Claude P-C2) | **채택** | 예제 목록 2줄 + plan 문서 표 1행을 **CLAUDE.md와 AGENTS.md 양쪽**에 추가 |
| D7 | 테스트 규모·기대값 명시 (Claude P-C3) | **채택** | 8연결 × (+1,000 / −750) = 최종 2,000, `checked` 계산, 타임아웃 수치 명시(베이스 계획 수치 유지) |
| D8 | 00_context 오기 (Codex 교정) | **채택** | `SessionContextExtensions` → 실제 `PacketSendExtensions.SendAsync<T>` |
| D9 | 조회 경로 주석 분리 (Codex P-X4) | **채택** | 증감=동기 완료 ValueTask / 조회=비동기 완료·조건부 할당 가능 — XML 주석에 정확히 반영 |
| D10 | 조회 송신 실패 처리 (Codex P-X5) | **채택** | SocketException=상대 종료 가정 금지(송신 타임아웃도 TimedOut 변환됨, `SocketPipelineSession.cs:375`) → 로그 + 해당 세션 종료 |
| D11 | 루프백 명시 (Codex P-X6) | **채택** | `listener.Start(port, IPAddress.Loopback)` — EchoServer 패턴 |
| D12 | 서버 종료 시 요약 출력 (Codex P-X8) | **채택** | 종료 출력은 참고용 `Value`만. 검증 판정은 클라이언트 배리어 이후 조회로 한정 |
| D13 | F6 교정 (Codex P-X9 + Claude 자기 수정) | **채택** | 서버는 정상 PING만 가로챔 — Id=250 미지 패킷 테스트는 유지, 근거 문장만 교정 |
| D14 | 워킹트리 서술 (Codex 진술) | **기각(사실 교정)** | `.claude/settings.local.json`은 전역 ignore 대상 — `git status --porcelain` 빈 출력 확인됨. 리뷰 범위 영향 없음 |
| D15 | 포트 9300 vs 9010, 패킷 명명 | **[취향] 베이스 유지** | 9300, `CounterQueryPacket`/`CounterValuePacket` |
| D16 | Claude [미해결 질문] 2건 | **해소** | unsafe 기각으로 초기 모드 질문 소멸. 클라이언트는 비인터랙티브 일괄 실행 + 종료 코드(베이스 계획) |

## 미해결
없음 (High 쟁점 0건, 전 항목 판정 완료).

## 통계
Codex→Claude 지적 9건: 채택 8(P-X1·2·3부분·4·5·6·7·8·9) / 부분채택 1(P-X3: 시나리오 공유 채택, 지연 테스트는 D4로 삭제). Claude→Codex 지적 7건: 채택 3(P-C1·2·3) / Low 4건 중 채택 2(sln 12행 경고, F6 유형) · 기각 1(워킹트리, D14) · 취향 처리 1.
