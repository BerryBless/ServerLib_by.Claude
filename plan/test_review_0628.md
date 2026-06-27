# ServerLib.Tests 종합 코드 리뷰 결과

**날짜:** 2026-06-28
**감사 대상:** ServerLib.Tests (11개 파일, 176개 테스트)
**감사 그룹:**
- Group 1 — 직렬화 도메인: `SpanReaderWriterTests.cs`, `BinaryPacketSerializerTests.cs`, `PacketRoundTripTests.cs`, `PacketPoolTests.cs`
- Group 2 — 인증·DB·세션 도메인: `LoginServiceTests.cs`, `DbMetricsTests.cs`, `SessionRegistryTests.cs`, `SessionStateTests.cs`
- Group 3 — RPC·메트릭·티켓팅 도메인: `RpcDispatcherTests.cs`, `ServerMetricsTests.cs`, `TicketInventoryConcurrencyTests.cs`, `TicketPacketRoundTripTests.cs`

---

## 요약

| 구분 | Critical | Important | Minor |
|------|----------|-----------|-------|
| 품질 이슈 | 0건 | 4건 | 11건 |
| 커버리지 갭 | 5건 | 17건 | 13건 |
| **합계** | **5건** | **21건** | **24건** |

---

## 품질 이슈 (Critical)

*해당 없음*

---

## 품질 이슈 (Important)

### [QUALITY-I-01] PacketRoundTripTests.cs — 바디 없는 패킷 false-positive Assert

- **문제:** `IncrementPacket_roundtrip`, `DecrementPacket_roundtrip`, `StatsRequestPacket_roundtrip` 세 테스트가 `Assert.Equal((ushort)N, p2.PacketId)` 단독으로 검증한다. `PacketId`는 상수를 반환하는 프로퍼티이므로 직렬화기가 완전히 망가져도 항상 통과하는 false-positive가 된다. 역직렬화 동작과 무관하게 테스트가 통과한다.
- **권장 수정:** 직렬화된 버퍼의 헤더 4바이트를 직접 Assert(`packetId LE ushort == N`, `bodyLength == 0`)하거나, 최소한 버퍼 길이가 `HeaderSize(4)`임을 `Assert.Equal(4, buffer.Length)`로 검증할 것.
- **수정 대상 파일:** `ServerLib.Tests/PacketRoundTripTests.cs`

### [QUALITY-I-02] SpanReaderWriterTests.cs:Position_and_Remaining_track_correctly — 테스트 범위 오해 유발

- **문제:** 테스트명이 "track correctly"로 양쪽 타입을 모두 커버한다는 인상을 주지만, 실제로는 `SpanWriter`의 `Position`·`Remaining`만 검증하고 `SpanReader`의 동명 프로퍼티는 전혀 검증하지 않는다. 리뷰어가 `SpanReader`의 해당 프로퍼티가 테스트됐다고 오해할 수 있다.
- **권장 수정:** `SpanReader`의 `Position`·`Remaining` 검증을 별도 테스트 `SpanReader_Position_and_Remaining_track_correctly`로 분리하거나, 기존 테스트명을 `SpanWriter_Position_and_Remaining_track_correctly`로 한정 변경할 것.
- **수정 대상 파일:** `ServerLib.Tests/SpanReaderWriterTests.cs`

### [QUALITY-I-03] LoginServiceTests.cs:PasswordHasher_GenerateSeedSql — CI 속도 저해 및 순수 단위 테스트 위반

- **문제:** `PasswordHasher.Hash(password)` 호출 시 `iterations` 인수를 생략하여 기본값 100,000회 PBKDF2가 실행된다. 다른 모든 테스트는 `iterations: 1_000`을 명시하는 반면, 이 테스트만 100배 느린 반복 수로 수십 ms 이상 CI 시간을 낭비한다. 또한 `Console.WriteLine`으로 SQL을 출력하는 사이드이펙트가 있어 순수 단위 테스트 원칙에 위배된다.
- **권장 수정:** `[Fact(Skip = "manual seed tool — run locally")]`로 표시하거나 별도 Tools/DevHelper 프로젝트로 이동할 것.
- **수정 대상 파일:** `ServerLib.Tests/LoginServiceTests.cs`

### [QUALITY-I-04] TicketInventoryConcurrencyTests.cs:SweepExpired_releases_only_expired_slot_in_batch_context — 테스트명과 구현 불일치

- **문제:** 테스트명은 "단일 배치 컨텍스트의 만료된 슬롯만 반납"을 암시하지만, 실제 구현은 두 개의 독립 컨텍스트(`ctx`, `ctx2`)를 사용한다. 코드 내부 주석 자체가 의도 포기("ctx 하나만 cap=2로 두 좌석 동시 예약 후 seat0만 TTL 초과 불가능")를 명시한다. 실제로 검증하는 시나리오는 "컨텍스트 1(expired) + 컨텍스트 2(fresh) = 1개만 반납"이다.
- **권장 수정:** 테스트명을 `SweepExpired_releases_ctx1_seat_not_ctx2_fresh_seat`으로 변경하고, 배치 컨텍스트 의도 검증은 별도 커버리지 갭 항목(GAP-M-12)으로 분리할 것.
- **수정 대상 파일:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs`

---

## 품질 이슈 (Minor) — 수정 제외, 기록만

### [QUALITY-M-01] SpanReaderWriterTests.cs:RoundTrip_Bool_true_and_false — AAA 위반

단일 `[Fact]` 안에 `WriteBool(true)/ReadBool()` 검증과 `WriteBool(false)/ReadBool()` 검증 두 Act+Assert가 혼재한다. 첫 번째 Assert 실패 시 두 번째 케이스를 실행하지 않아 진단 정보가 누락된다. `RoundTrip_Bool_true`, `RoundTrip_Bool_false`로 분리 권장.

### [QUALITY-M-02] PacketPoolTests.cs:Headers_pool_get_return_works — AAA 위반 및 약한 Reset 검증

단일 테스트 내 두 독립 시나리오(Get/Assert, Return+Get/Assert) 혼합이다. 추가로 `Return(header)` 후 `Get()`이 새 객체를 반환하는 경우에도 `PacketHeader()` 기본값이 0이므로 `Reset()` 호출 여부를 실제로 검증하지 못한다. `Headers_Get_returns_non_null`, `Headers_Return_resets_fields`로 분리 후 `Assert.Same`으로 인스턴스 동일성 확인 권장.

### [QUALITY-M-03] DbMetricsTests.cs:GetSnapshot_ZeroCounts_ReturnsZeroAverages — Count 필드 미검증

이름에서 "ZeroCounts"를 선언하지만 `s.MysqlCount`, `s.RedisSetCount`, `s.RedisGetCount`가 0인지는 검증하지 않는다. `GetSnapshot` 반환 Count 필드 계약이 미검증 상태다.

### [QUALITY-M-04] SessionRegistryTests.cs:Register_same_session_twice_keeps_count_as_1 — GetAll() 미검증

`Count == 1`만 확인하고 `GetAll()`이 1개 항목을 반환하는지 검증하지 않는다. 중복 없이 1개만 존재하는 계약을 완전히 보장하려면 `Assert.Single(registry.GetAll())` 추가 필요.

### [QUALITY-M-05] SessionStateTests.cs:GetHashCode_equals_value — 단일 값만 테스트

`SessionState.Connected`(value=1) 하나만 검증한다. 테스트명이 일반 규칙("GetHashCode는 Value를 반환")을 표현하지만 단일 케이스만 다룬다. `[Theory, InlineData]`로 5개 predefined 값 또는 최소 경계값(0, 4)을 추가 권장.

### [QUALITY-M-06] SessionStateTests.cs:Custom_below_or_equal_reserved_max_throws — 내부 경계값 누락

0, 4, -1만 테스트하여 예약 범위 내부(1, 2, 3)가 누락됐다. `Custom(1)`, `Custom(2)`, `Custom(3)`도 `ArgumentOutOfRangeException`을 던져야 하며 `[Theory, InlineData(-1), InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4)]`로 교체 권장.

### [QUALITY-M-07] SessionStateTests.cs:Equals_method_matches_operator — 중복 Assert

`a == b` / `a == c` 검증이 `Equality_operator_returns_true_for_same_value`·`Inequality_operator_returns_true_for_different_values`와 중복된다. `IEquatable<SessionState>.Equals`와 `object.Equals` 오버라이드를 구분하는 방향으로 리팩토링 권장.

### [QUALITY-M-08] RpcDispatcherTests.cs — 두 "unknown packetId" 테스트 중복

`Dispatch_unknown_packetId_does_not_throw`와 `Null_handler_slot_does_not_throw` 모두 "핸들러 null → 예외 없음"을 검증한다. 통과 경로(`handler == null → return`)가 동일하며 시나리오 차이보다 단언의 중복이 더 크다. `[Theory][InlineData]`로 통합하거나 하나를 제거 권장.

### [QUALITY-M-09] RpcDispatcherTests.cs:Register_packetId_in_range_succeeds — 약한 단언

`Assert.Null(ex)`로 예외 없음만 검증한다. 등록 성공 여부는 핸들러 호출 여부로 확인해야 의미가 있으며, `Dispatch_registered_handler_is_called_with_correct_body`가 이미 같은 시나리오를 더 완전하게 커버한다. 삭제하거나 "등록 후 즉시 디스패치 → 핸들러 호출 확인"으로 강화 권장.

### [QUALITY-M-10] TicketPacketRoundTripTests.cs:TicketResult_roundtrip_preserves_all_fields — RateLimited InlineData 누락

Theory의 `InlineData` 8개(Reserved/SoldOut/AlreadyReserved/NotReserved/Confirmed/PaymentFailed/Released/SeatTaken)에 `TicketStatus.RateLimited`(값=8)가 누락됐다. `TicketStatus_all_values_are_unique`만으로는 직렬화 라운드트립을 검증하지 못한다. `[InlineData(TicketStatus.RateLimited, 0, (byte)0xFF, 0)]` 추가 권장 (이 수정이 GAP-I-17도 해소).

### [QUALITY-M-11] ServerMetricsTests.cs 전체 — 단일 스레드 테스트만 존재

`ServerMetrics`는 Interlocked 기반으로 Thread-safe가 명시된 클래스이지만, 7개 테스트 전부 단일 스레드 순차 실행이다. 생산 환경에서 다수 IO 스레드가 동시 갱신하는 핵심 경로를 단일 스레드 테스트만으로 커버한다.

---

## 커버리지 갭 (Critical)

### [GAP-C-01] 대상: `SpanWriter.WriteString(string value, int precomputedByteCount)` 오버로드

- **누락 시나리오:** 이 오버로드를 직접 단위 테스트하는 케이스가 전혀 없다. 모든 문자열 포함 패킷 구현체(`LoginRequestPacket`, `LoginResponsePacket`, `AuthTokenPacket`, `EchoPacket`, `ChatPacket`, `MobDeathPacket`)가 이 오버로드만 사용한다. 단일-arg `WriteString(string)`에는 보안 회귀 테스트가 있는데 동일한 두 예외 경로(`precomputedByteCount > ushort.MaxValue`, 음수 `precomputedByteCount`)가 이 오버로드에서는 미검증이다.
- **제안 테스트명:**
  - `WriteString_precomputed_over_max_throws_ArgumentOutOfRangeException`
  - `WriteString_precomputed_negative_throws_ArgumentOutOfRangeException`
  - `WriteString_precomputed_roundtrip_matches_auto_overload`
- **추가 대상 파일:** `ServerLib.Tests/SpanReaderWriterTests.cs`

### [GAP-C-02] 대상: `PacketRoundTripTests` / `TicketReserveRequestPacket`

- **누락 시나리오:** 배치 포맷(`Count(1B) + [Row,Col 쌍 × Count]`) 직렬화·역직렬화 왕복 테스트 없음. `Count=0`(빈 요청), `Count=1`(단일 좌석), `Count=N`(배치) 세 경로 모두 미커버. `Serialize`에서 `Math.Min(Count, Math.Min(Rows.Length, Cols.Length))`로 배열 길이를 클램핑하는 특수 로직이 존재하지만 테스트 안전망이 전무하다.
- **제안 테스트명:**
  - `TicketReserveRequestPacket_single_seat_roundtrip`
  - `TicketReserveRequestPacket_batch_roundtrip`
  - `TicketReserveRequestPacket_count_zero_roundtrip`
- **추가 대상 파일:** `ServerLib.Tests/PacketRoundTripTests.cs`

### [GAP-C-03] 대상: `PacketRoundTripTests` / `TicketResultPacket`

- **누락 시나리오:** `Status(1B) + Count(1B) + Slots[Count](가변) + Remaining(1B)` 가변 배열 직렬화 왕복 테스트 없음. `Count=0`(실패, Slots=빈 배열), `Count>0`(성공, Slots 할당) 두 경로 모두 미커버. (TicketPacketRoundTripTests.cs의 `TicketResult_roundtrip_preserves_all_fields`는 별도 파일에 존재하며 해당 파일은 Group 3이 감사.)
- **제안 테스트명:**
  - `TicketResultPacket_confirmed_roundtrip`
  - `TicketResultPacket_failed_count_zero_roundtrip`
- **추가 대상 파일:** `ServerLib.Tests/PacketRoundTripTests.cs`

### [GAP-C-04] 대상: `PacketRoundTripTests` / `SeatMapResponsePacket`

- **누락 시나리오:** `Rows(1B) + Cols(1B) + States[Rows*Cols](가변)` 가변 배열 직렬화 왕복 테스트 없음. `Deserialize`에서 `Rows*Cols > byte.MaxValue` 검증 로직이 존재하지만 예외 경로도 미테스트. 역직렬화 시 `reader.ReadBytes(Rows*Cols).ToArray()` 힙 할당 경로도 미검증.
- **제안 테스트명:**
  - `SeatMapResponsePacket_2x3_roundtrip`
  - `SeatMapResponsePacket_rows_cols_overflow_throws_InvalidDataException`
- **추가 대상 파일:** `ServerLib.Tests/PacketRoundTripTests.cs`

### [GAP-C-05] 대상: `RpcDispatcher.DispatchAsync` — async 핸들러 예외 전파

- **누락 시나리오:** 핸들러가 예외를 던질 때 `await handler(...)` 결과로 예외가 호출자에게 전파된다. 현재 프로덕션 코드에는 try/catch가 없으므로 예외가 IO 루프까지 전파되어 세션 연결이 끊어질 수 있다. 이 동작이 의도인지 명세화된 테스트가 없으면 향후 핸들러 추가 시 무증상 회귀가 발생한다.
- **제안 테스트명:** `Handler_throwing_exception_propagates_to_caller`
  ```csharp
  dispatcher.Register(1, (_, _, _) => throw new InvalidOperationException("boom"));
  await Assert.ThrowsAsync<InvalidOperationException>(
      () => dispatcher.DispatchAsync(session, new byte[] { 0x01, 0x00 }).AsTask());
  ```
- **추가 대상 파일:** `ServerLib.Tests/RpcDispatcherTests.cs`

---

## 커버리지 갭 (Important)

### [GAP-I-01] 대상: `SpanReader.ReadRemainingBytes()`

- **누락 시나리오:** 직접 단위 테스트 없음. `StatsResponsePacket`의 Json 필드가 이 메서드에 의존하며, `_position`을 버퍼 끝으로 이동시키는 부수 효과 및 빈 버퍼에서의 동작이 미검증이다.
- **제안 테스트명:** `ReadRemainingBytes_returns_rest_of_buffer`, `ReadRemainingBytes_on_empty_buffer_returns_empty_span`
- **추가 대상 파일:** `ServerLib.Tests/SpanReaderWriterTests.cs`

### [GAP-I-02] 대상: `SpanWriter.WriteString("")` / `SpanReader.ReadString()` — 빈 문자열 왕복

- **누락 시나리오:** 빈 문자열(byteCount=0) 왕복 미테스트. 와이어에 `[0x00, 0x00]` 2바이트 기록 후 역직렬화 시 빈 string 반환 경계값이 미검증이다.
- **제안 테스트명:** `RoundTrip_String_Empty`
- **추가 대상 파일:** `ServerLib.Tests/SpanReaderWriterTests.cs`

### [GAP-I-03] 대상: `PacketRoundTripTests` / `LoginRequestPacket`

- **누락 시나리오:** Username + Password 두 문자열 직렬화 왕복 테스트 없음. `precomputedByteCount` 캐시 무효화 로직(setter 호출 시 `_usernameBytes = -1`)도 미검증이다.
- **제안 테스트명:** `LoginRequestPacket_roundtrip`, `LoginRequestPacket_empty_password_roundtrip`
- **추가 대상 파일:** `ServerLib.Tests/PacketRoundTripTests.cs`

### [GAP-I-04] 대상: `PacketRoundTripTests` / `LoginResponsePacket`

- **누락 시나리오:** `bool(Success) + string(Token)` 혼합 직렬화 왕복 테스트 없음. `Success=false` 시 Token 빈 문자열 경로 미검증.
- **제안 테스트명:** `LoginResponsePacket_success_roundtrip`, `LoginResponsePacket_failure_empty_token_roundtrip`
- **추가 대상 파일:** `ServerLib.Tests/PacketRoundTripTests.cs`

### [GAP-I-05] 대상: `PacketRoundTripTests` / `AuthTokenPacket`

- **누락 시나리오:** Token 문자열 직렬화 왕복 테스트 없음.
- **제안 테스트명:** `AuthTokenPacket_roundtrip`
- **추가 대상 파일:** `ServerLib.Tests/PacketRoundTripTests.cs`

### [GAP-I-06] 대상: `BinaryPacketSerializer.Deserialize<T>()` — 잘린 본문 예외 전파

- **누락 시나리오:** 헤더의 `bodyLength`가 실제 전달된 버퍼보다 큰 경우 `SpanReader.EnsureAvailable`이 `EndOfStreamException`을 발생시키는 전파 경로가 미테스트. 네트워크 단절 또는 악의적 헤더 조작 시 재현 가능한 경로다.
- **제안 테스트명:** `Deserialize_throws_when_body_truncated`
- **추가 대상 파일:** `ServerLib.Tests/BinaryPacketSerializerTests.cs`

### [GAP-I-07] 대상: `LoginService.LoginAsync` — CancellationToken 취소

- **누락 시나리오:** `ct`가 이미 취소된 상태로 전달될 때 `OperationCanceledException`이 전파되는지 검증 테스트 없음. `FindByUsernameAsync`, `Task.Run(..., ct)`, `StoreAsync(..., ct)` 세 지점 모두 ct를 수신한다.
- **제안 테스트명:** `LoginAsync_CancelledToken_ThrowsOperationCanceledException`
- **추가 대상 파일:** `ServerLib.Tests/LoginServiceTests.cs`

### [GAP-I-08] 대상: `LoginService.LoginAsync` — DbMetrics 계측 경로

- **누락 시나리오:** `BuildService` 헬퍼가 `dbMetrics` 인수를 항상 생략(null)하므로 `_dbMetrics?.RecordMysqlSelect(...)` 및 `_dbMetrics?.RecordRedisSet(...)` 분기가 전혀 실행되지 않는다. MySQL SELECT 지연·Redis SET 지연 기록 여부, 실패 경로에서 MySQL만 기록되고 Redis는 기록 안 되는지 미검증.
- **제안 테스트명:** `LoginAsync_WithDbMetrics_RecordsMysqlSelectLatency`, `LoginAsync_FailedAuth_WithDbMetrics_DoesNotRecordRedis`
- **추가 대상 파일:** `ServerLib.Tests/LoginServiceTests.cs`

### [GAP-I-09] 대상: `DbMetrics.RecordMysqlSelect` / `RecordRedisSet` / `RecordRedisGet` — 동시성

- **누락 시나리오:** 클래스 문서가 "Thread-safe — Interlocked"를 명시하지만 동시 호출 정확성을 검증하는 테스트가 전혀 없다. `Parallel.For(0, 100, _ => m.RecordMysqlSelect(1L))` 후 `s.MysqlCount == 100`인지 검증해야 Lock-free 구현의 정확성을 보장할 수 있다.
- **제안 테스트명:** `RecordMysqlSelect_Concurrent_CountIsAccurate`, `RecordRedisSet_Concurrent_CountIsAccurate`
- **추가 대상 파일:** `ServerLib.Tests/DbMetricsTests.cs`

### [GAP-I-10] 대상: `SessionRegistry.Unregister` — 존재하지 않는 ID

- **누락 시나리오:** 존재하지 않는 SessionId 전달 시 예외 없이 no-op인지 테스트 없음. 프로덕션 코드는 `TryRemove`를 사용해 안전하지만 계약이 테스트로 보장되지 않는다. 세션 연결 끊김 후 이중 Unregister 시나리오에서 재현 가능.
- **제안 테스트명:** `Unregister_nonexistent_id_does_not_throw`
- **추가 대상 파일:** `ServerLib.Tests/SessionRegistryTests.cs`

### [GAP-I-11] 대상: `SessionRegistry.Register` / `Unregister` — 동시성 레이스 조건

- **누락 시나리오:** 다수 스레드가 동시에 Register/Unregister를 호출할 때 Count가 음수가 되거나 예외가 발생하지 않는다는 보장이 테스트로 없다. ConcurrentDictionary 기반이라 Thread-safe이지만 동시성 계약이 명세화되지 않은 상태다.
- **제안 테스트명:** `ConcurrentRegisterUnregister_CountIsNonNegative`
- **추가 대상 파일:** `ServerLib.Tests/SessionRegistryTests.cs`

### [GAP-I-12] 대상: `RpcDispatcher.Register` — 동일 PacketId 이중 등록 동작

- **누락 시나리오:** `Register(5, h1)` 후 `Register(5, h2)` 호출 시 `_handlers[5]`가 조용히 `h2`로 교체된다. 이 덮어쓰기 동작이 의도적 설계인지, 예외를 던져야 하는지 명세화된 테스트가 없다.
- **제안 테스트명:** `Register_same_packetId_twice_last_handler_wins`
- **추가 대상 파일:** `ServerLib.Tests/RpcDispatcherTests.cs`

### [GAP-I-13] 대상: `ServerMetrics.OnBytesSent` / `OnBytesReceived` — 음수 count 입력

- **누락 시나리오:** `int count` 파라미터에 음수(-1 등)를 전달하면 `Interlocked.Add`가 누적값을 음수로 만든다. 프로덕션 코드에 가드가 없으며, 이 동작이 허용인지 명시된 테스트가 없다.
- **제안 테스트명:** `OnBytesSent_negative_count_decrements_total`, `OnBytesReceived_negative_count_decrements_total`
- **추가 대상 파일:** `ServerLib.Tests/ServerMetricsTests.cs`

### [GAP-I-14] 대상: `ServerMetrics` — 동시성 카운터 정확성

- **누락 시나리오:** N개 Task가 동시에 `OnPacketReceived`를 호출해 최종값이 N×호출수가 되는지 검증하는 테스트가 없다. Interlocked가 이론적으로 보장하지만, 내부 구현 변경 시 회귀를 탐지할 동시성 테스트가 필요하다.
- **제안 테스트명:** `Concurrent_increment_and_decrement_are_accurate`
- **추가 대상 파일:** `ServerLib.Tests/ServerMetricsTests.cs`

### [GAP-I-15] 대상: `TicketContext` Rate Limit — MaxReserveAttemptsPerWindow 정확 경계

- **누락 시나리오:** `TicketContext.MaxReserveAttemptsPerWindow = 10`, `RateLimitWindowMs = 60000` 필드가 존재하지만, 정확히 10회째 Reserve 시도는 허용되고 11회째에 `RateLimited`가 반환되는 경계 테스트가 없다. 핵심 비즈니스 규칙이므로 단위 테스트로 명세화되어야 한다.
- **제안 테스트명:** `RateLimit_exactly_10th_attempt_allowed_11th_returns_RateLimited`
- **추가 대상 파일:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs`

### [GAP-I-16] 대상: `TicketInventory.TryReserveBatch` — 빈 seatIds 배열 입력

- **누락 시나리오:** `seatIds.Length == 0`(n=0) 입력 시 프로덕션 코드 `if (n == 0 || n > ctx.Slots.Length)`에서 `(SeatTaken, 0)`을 반환한다. 이 경계 케이스를 검증하는 테스트가 없다.
- **제안 테스트명:** `TryReserveBatch_empty_seatIds_returns_seatTaken`
- **추가 대상 파일:** `ServerLib.Tests/TicketInventoryConcurrencyTests.cs`

### [GAP-I-17] 대상: `TicketResultPacket` — `TicketStatus.RateLimited` 직렬화 라운드트립 (`TicketPacketRoundTripTests.cs`)

- **누락 시나리오:** `TicketPacketRoundTripTests.cs`의 `TicketResult_roundtrip_preserves_all_fields` Theory에서 `TicketStatus.RateLimited`(값=8)가 누락됐다. 다른 8개 상태는 커버되지만 클라이언트가 수신하는 핵심 흐름인 RateLimited 상태의 직렬화 정확성을 보장하지 못한다. (QUALITY-M-10의 수정으로 함께 해소)
- **제안 테스트명:** `TicketResult_roundtrip_preserves_all_fields`에 `[InlineData(TicketStatus.RateLimited, 0, (byte)0xFF, 0)]` 추가
- **추가 대상 파일:** `ServerLib.Tests/TicketPacketRoundTripTests.cs`

---

## 커버리지 갭 (Minor) — 수정 제외, 기록만

### [GAP-M-01] 대상: `BinaryPacketSerializer.TryReadPacketLength` — bodyLength=0 반환값

`bodyLength=0`인 헤더에 대해 반환값이 정확히 `HeaderSize(4)`인지 미테스트. 제안 테스트명: `TryReadPacketLength_returns_header_size_when_body_zero`

### [GAP-M-02] 대상: `PacketRoundTripTests` / `TicketPayRequestPacket` — 등록 누락

바디 없는 패킷이지만 신규 티켓팅 도메인 패킷으로 PacketRoundTripTests에서 누락됐다. 제안 테스트명: `TicketPayRequestPacket_roundtrip`

### [GAP-M-03] 대상: `PacketRoundTripTests` / `SeatMapRequestPacket` — 등록 누락

바디 없는 패킷이지만 신규 티켓팅 도메인 패킷으로 PacketRoundTripTests에서 누락됐다. 제안 테스트명: `SeatMapRequestPacket_roundtrip`

### [GAP-M-04] 대상: `PacketPool.WriteHeader` — `packetId=ushort.MaxValue` 경계값

`packetId=65535` 경계값 미테스트. 제안 테스트명: `WriteHeader_packetId_max_is_valid`

### [GAP-M-05] 대상: `PacketPool.RentSendBuffer` — `minimumSize=0` 입력

`ArrayPool<byte>.Shared.Rent(0)` 동작 미검증. 제안 테스트명: `RentSendBuffer_zero_returns_non_null_buffer`

### [GAP-M-06] 대상: `SpanWriter.WriteString(null)` — null 입력 예외

null 입력 시 `ArgumentNullException` 발생 여부 미테스트. 제안 테스트명: `WriteString_null_throws_ArgumentNullException`

### [GAP-M-07] 대상: `LoginService.LoginAsync` — 빈 password 처리

`LoginAsync(validUser, "")` 호출 시 실패 반환 테스트 없음. 빈 username 테스트는 있으나 빈 password는 누락. 제안 테스트명: `LoginAsync_EmptyPassword_ReturnsFailure`

### [GAP-M-08] 대상: `DbMetrics` — 카운터 카테고리 격리

`RecordMysqlSelect` 호출이 `RedisSetCount`·`RedisGetCount`에 영향을 주지 않는 격리 테스트 없음. 제안 테스트명: `RecordMysqlSelect_DoesNotAffectRedisCounters`

### [GAP-M-09] 대상: `SessionRegistry.BroadcastAsync` — CancellationToken 취소

프로덕션 코드 주석이 "OperationCanceledException 의도적으로 전파"를 명시했지만 검증 테스트 없음. 제안 테스트명: `BroadcastAsync_CancelledMidway_PropagatesOperationCancelledException`

### [GAP-M-10] 대상: `SessionState.Equals(object?)` — null 및 이종 타입

null 및 이종 타입(예: `string`) 전달 시 false 반환 여부 미테스트. 박싱 후 컨테이너 비교 시나리오에서 문제 발생 가능. 제안 테스트명: `Equals_object_null_returns_false`, `Equals_object_wrong_type_returns_false`

### [GAP-M-11] 대상: `SessionState(int)` 생성자 — 음수 값 허용 계약

음수 값 직접 생성(`new SessionState(-1)`)은 예외를 던지지 않음(Custom(-1)과 달리). 이 의도적 허용이 테스트로 보장되지 않아 향후 생성자에 검증이 추가될 경우 회귀 탐지 불가. 제안 테스트명: `Constructor_negative_value_is_allowed`

### [GAP-M-12] 대상: `TicketInventory.SweepExpired` — 단일 배치 컨텍스트 부분 만료

단일 컨텍스트가 두 좌석을 배치 보유 중 하나만 TTL 초과할 때, SweepExpired가 만료된 슬롯만 처리하고 나머지는 보존하는지 검증 테스트 없음. TTL을 극소(1ms)로 설정하고 seat 0만 예약 → Sleep → seat 1 예약 → SweepExpired 순서로 구성 가능. 제안 테스트명: `SweepExpired_partial_batch_context_only_expired_slot_released`

### [GAP-M-13] 대상: `TicketResultPacket.Serialize` — `Slots = null` 입력

`Count > 0`이지만 `Slots = null`인 상태로 `Serialize`를 호출하면 `NullReferenceException` 발생. `Slots` 프로퍼티가 public이라 외부에서 null 설정 가능하며 가드가 없다. 제안 테스트명: `TicketResult_serialize_with_null_slots_throws_NullReference` (또는 프로덕션에 null 가드 추가)

---

## 수정 대상 요약 (Tasks 5·6용)

### Task 5 (품질 수정): Important 품질 이슈 4건

| ID | 대상 파일 | 핵심 작업 |
|----|-----------|----------|
| QUALITY-I-01 | `PacketRoundTripTests.cs` | 바디 없는 패킷 3개 — PacketId Assert를 버퍼 헤더 직접 검증으로 교체 |
| QUALITY-I-02 | `SpanReaderWriterTests.cs` | `Position_and_Remaining_track_correctly` — SpanReader 검증 추가 또는 테스트명 한정 |
| QUALITY-I-03 | `LoginServiceTests.cs` | `PasswordHasher_GenerateSeedSql` — `[Fact(Skip)]` 표시 또는 이동 |
| QUALITY-I-04 | `TicketInventoryConcurrencyTests.cs` | `SweepExpired_releases_only_expired_slot_in_batch_context` — 테스트명 수정 |

### Task 6 (갭 추가): Critical 5건 + Important 17건 = 22건 신규 테스트

**Critical (5건) — 즉시 추가:**

| ID | 추가 대상 파일 | 제안 테스트 수 |
|----|--------------|---------------|
| GAP-C-01 | `SpanReaderWriterTests.cs` | 3개 (`WriteString_precomputed_*`) |
| GAP-C-02 | `PacketRoundTripTests.cs` | 3개 (`TicketReserveRequestPacket_*`) |
| GAP-C-03 | `PacketRoundTripTests.cs` | 2개 (`TicketResultPacket_*`) |
| GAP-C-04 | `PacketRoundTripTests.cs` | 2개 (`SeatMapResponsePacket_*`) |
| GAP-C-05 | `RpcDispatcherTests.cs` | 1개 (`Handler_throwing_exception_propagates_to_caller`) |

**Important (17건) — 순차 추가:**

| ID | 추가 대상 파일 | 제안 테스트 수 |
|----|--------------|---------------|
| GAP-I-01 | `SpanReaderWriterTests.cs` | 2개 (`ReadRemainingBytes_*`) |
| GAP-I-02 | `SpanReaderWriterTests.cs` | 1개 (`RoundTrip_String_Empty`) |
| GAP-I-03 | `PacketRoundTripTests.cs` | 2개 (`LoginRequestPacket_*`) |
| GAP-I-04 | `PacketRoundTripTests.cs` | 2개 (`LoginResponsePacket_*`) |
| GAP-I-05 | `PacketRoundTripTests.cs` | 1개 (`AuthTokenPacket_roundtrip`) |
| GAP-I-06 | `BinaryPacketSerializerTests.cs` | 1개 (`Deserialize_throws_when_body_truncated`) |
| GAP-I-07 | `LoginServiceTests.cs` | 1개 (`LoginAsync_CancelledToken_*`) |
| GAP-I-08 | `LoginServiceTests.cs` | 2개 (`LoginAsync_WithDbMetrics_*`) |
| GAP-I-09 | `DbMetricsTests.cs` | 2개 (`Record*_Concurrent_CountIsAccurate`) |
| GAP-I-10 | `SessionRegistryTests.cs` | 1개 (`Unregister_nonexistent_id_does_not_throw`) |
| GAP-I-11 | `SessionRegistryTests.cs` | 1개 (`ConcurrentRegisterUnregister_CountIsNonNegative`) |
| GAP-I-12 | `RpcDispatcherTests.cs` | 1개 (`Register_same_packetId_twice_last_handler_wins`) |
| GAP-I-13 | `ServerMetricsTests.cs` | 2개 (`OnBytes*_negative_count_*`) |
| GAP-I-14 | `ServerMetricsTests.cs` | 1개 (`Concurrent_increment_and_decrement_are_accurate`) |
| GAP-I-15 | `TicketInventoryConcurrencyTests.cs` | 1개 (`RateLimit_exactly_10th_attempt_*`) |
| GAP-I-16 | `TicketInventoryConcurrencyTests.cs` | 1개 (`TryReserveBatch_empty_seatIds_*`) |
| GAP-I-17 | `TicketPacketRoundTripTests.cs` | InlineData 1개 추가 (`TicketStatus.RateLimited`) |
