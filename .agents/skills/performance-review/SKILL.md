---
name: performance-review
description: ".NET/C# 코드의 성능 병목을 심층 탐지한다. N+1 쿼리, async/await 오용, 힙 할당 압박, LINQ 비효율, 캐싱 누락을 분석하고 JSON 결과를 _workspace/02_performance_findings.json에 출력한다. performance-reviewer 에이전트가 사용하는 전용 스킬."
---

# Performance Review Skill

## 입력 읽기

1. `_workspace/00_input/diff.txt`를 Read 도구로 읽는다
2. diff면 `+` 줄(추가된 코드)에 집중한다
3. 루프 내부, 데이터 접근 레이어, async 메서드를 우선 확인한다

## 감사 체크리스트

### 1. N+1 쿼리 패턴

```csharp
// 위험: 루프 안에서 DB 쿼리
foreach (var order in orders)
{
    var customer = _db.Customers.Find(order.CustomerId); // N+1 발생
}

// 위험: EF Core lazy loading + 루프
foreach (var order in orders)
{
    Console.WriteLine(order.Customer.Name); // 내비게이션 프로퍼티 lazy load
}

// 안전: eager loading
var orders = _db.Orders.Include(o => o.Customer).ToList();
```

**탐지 포인트:** 루프 내 LINQ 쿼리, 루프 내 `Find()` / `FirstOrDefault()`, 내비게이션 프로퍼티를 루프 안에서 처음 접근

### 2. 비동기 안티패턴

```csharp
// 위험: 스레드 풀 블로킹
var result = asyncMethod().Result;          // 데드락 위험
var result = asyncMethod().GetAwaiter().GetResult();
asyncMethod().Wait();

// 위험: async void (예외 캐치 불가)
public async void HandleEvent(...)

// 위험: Task.Run 과용 (I/O 작업에 사용)
var data = await Task.Run(() => _db.Users.ToList());

// 안전
var result = await asyncMethod();
public async Task HandleEventAsync(...)
```

### 3. LINQ 비효율

```csharp
// 위험: 필터 전 ToList()
var users = _db.Users.ToList().Where(u => u.IsActive);  // 전체 로드 후 필터

// 위험: Count() > 0
if (_db.Users.Count() > 0)  // Any()가 더 효율적

// 위험: Select 후 Where (순서 역전)
users.Select(u => expensiveTransform(u)).Where(u => u.IsValid)

// 위험: 중첩 루프에 IEnumerable 반복
foreach (var x in list)
    if (otherList.Contains(x))  // O(n²) - HashSet 사용 권장

// 안전
var users = _db.Users.Where(u => u.IsActive).ToList();
if (_db.Users.Any())
users.Where(u => u.IsValid).Select(u => expensiveTransform(u))
```

### 4. 메모리 관리

**IDisposable 미해제:**
```csharp
// 위험
var stream = new FileStream(...); // using 없음
var conn = new SqlConnection(...); // using 없음

// 안전
using var stream = new FileStream(...);
```

**string 연결 루프:**
```csharp
// 위험: O(n²) 복잡도
string result = "";
foreach (var item in items) result += item.ToString();

// 안전
var sb = new StringBuilder();
foreach (var item in items) sb.Append(item.ToString());
```

**이벤트 핸들러 누수:**
```csharp
// 위험: 구독 후 해제 없음 (장수 객체에 단수 객체 등록)
longLivedObject.Event += shortLivedObject.Handler;
// Dispose에서 -= 미실행
```

### 5. 캐싱 누락

다음 패턴이 반복 호출 경로에 있으면 캐싱을 고려한다:
- 설정 조회 (변경 빈도 낮음)
- 참조 데이터 조회 (코드/카테고리/열거형)
- 외부 API 호출
- 복잡한 집계 쿼리

`IMemoryCache` / `IDistributedCache` 미사용이면서 `HttpContext.RequestServices`나 생성자 주입에 `IMemoryCache`가 없는 서비스에서 위 패턴이 발견되면 보고한다.

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **critical** | 프로덕션 장애 수준: 무제한 쿼리, 데드락 가능, 메모리 무한 증가 |
| **high** | 응답 시간 수 초 증가, N+1 (수백~수천 쿼리), 스레드 풀 기아 |
| **medium** | 눈에 띄는 지연, 불필요한 CPU/메모리 소비 |
| **low** | 미미한 비효율, 캐싱 추가로 개선 가능한 부분 |

## 출력

결과를 `_workspace/02_performance_findings.json`에 Write 도구로 저장한다.
발견사항이 없으면 빈 배열과 score=100으로 저장한다.
저장 완료 후 리더에게 SendMessage로 완료를 알린다.
