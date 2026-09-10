---
name: heap-allocation-scan
description: ".NET 10 서버 라이브러리 hot path의 불필요한 힙 할당을 정밀 탐지한다. boxing/unboxing, 루프 내 new, hot path LINQ, 클로저 강제 할당, string 루프 연산을 분석하고 JSON 결과를 _workspace/02_allocation_findings.json에 출력한다. heap-allocation-scanner 에이전트 전용 스킬."
---

# Heap Allocation Scan Skill

## 입력 읽기

`_workspace/00_input/source.txt`를 Read로 읽는다.
diff 형식이면 `+` 줄에 집중, 전체 파일이면 hot path 메서드를 먼저 식별한다.

## Hot Path 식별 (우선 분석 대상)

다음 패턴을 가진 메서드를 hot path로 마킹하고 **집중 분석**한다:

```csharp
// 시그니처 패턴
async Task/ValueTask Handle*(...)
async Task/ValueTask Process*(...)
async Task/ValueTask Parse*(...)
async Task/ValueTask Receive*(...)
async Task/ValueTask Read*(...)
async Task/ValueTask Write*(...)

// 어트리뷰트
[Benchmark]
[MethodImpl(MethodImplOptions.AggressiveInlining)]

// 루프 내부 (무조건 hot path)
for (...) { ... }
foreach (...) { ... }
while (...) { ... }
```

## 탐지 패턴 6종

### Pattern 1: Boxing / Unboxing

값 타입이 `object` 또는 인터페이스로 전달·저장되는 모든 지점.

```csharp
// CRITICAL: 제네릭 없는 컬렉션
ArrayList list = new ArrayList();
list.Add(42);           // int → object 박스화

// HIGH: 인터페이스 캐스팅
IComparable c = intValue;   // struct가 인터페이스로 → 박스화

// HIGH: 비제네릭 딕셔너리
Hashtable ht = new Hashtable();
ht["key"] = structValue;   // 박스화

// MEDIUM: string.Format 값 타입 인수 (.NET 6 미만)
string.Format("{0} {1}", intA, intB);  // 두 인수 박스화
// .NET 6+: 보간 문자열 $"{intA}" 는 InterpolatedStringHandler로 박스화 없음

// 탐지 정규식
object\s+\w+\s*=.*\b(int|long|bool|float|double|struct\b)
\bAdd\s*\(\s*\w+\s*\)  // 비제네릭 컬렉션의 Add
```

### Pattern 2: 루프 내 `new` (Heap Allocation in Loops)

```csharp
// CRITICAL: 루프 내 버퍼 할당
for (int i = 0; i < count; i++)
{
    var buffer = new byte[1024];    // 반복 할당
    var list = new List<int>();     // 반복 생성
    var response = new Response();  // 반복 생성
}

// HIGH: foreach 내 LINQ 체인 중간 컬렉션
foreach (var item in items)
{
    var filtered = item.Children.Where(...).ToList();  // 루프마다 List<T> 생성
}

// 수정: 루프 밖으로 이동하거나 재사용
byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
try
{
    for (int i = 0; i < count; i++)
    {
        Process(buffer.AsSpan(0, needed));
    }
}
finally { ArrayPool<byte>.Shared.Return(buffer); }
```

### Pattern 3: Hot Path LINQ

```csharp
// HIGH: 요청 처리 경로의 LINQ
public async ValueTask<Response> HandleRequestAsync(Request req)
{
    var items = _cache.Values
        .Where(x => x.IsActive)     // IEnumerable 래퍼 할당
        .Select(x => x.ToDto())     // 중간 열거자 할당
        .ToList();                  // List<T> 힙 할당
}

// MEDIUM: 단순 집계 LINQ (Any는 허용, Count는 주의)
if (list.Count() > 0)    // Count() 열거자 할당 → Any() 사용
if (!list.Any())         // OK - 최소 순회

// 허용 패턴 (보고 제외)
// - 초기화 코드의 LINQ
// - 설정 로드의 LINQ
// - 1회 호출 팩토리 메서드의 LINQ
```

### Pattern 4: 클로저 강제 힙 할당

```csharp
// HIGH: 루프 변수 캡처 → 클로저 객체 힙 할당
for (int i = 0; i < count; i++)
{
    tasks[i] = Task.Run(() => Process(i));  // i 캡처 → 클로저 힙 할당
}

// HIGH: 인스턴스 메서드 델리게이트 (매번 새 Delegate 객체)
list.Sort((a, b) => a.CompareTo(b));  // 람다마다 delegate 힙 할당

// 수정: static 람다 또는 캐싱된 delegate
private static readonly Comparison<Item> _comparison = (a, b) => a.CompareTo(b);
list.Sort(_comparison);  // 캐싱된 delegate 재사용

// 탐지: 루프 내 람다에서 루프 변수 또는 this 참조
```

### Pattern 5: String 루프 연산

```csharp
// HIGH: + 연산자 루프
string result = "";
foreach (var item in items)
    result += item.Name + ", ";  // 매 반복 새 string 할당

// MEDIUM: 루프 내 string.Concat / string.Join 반복 호출
for (int i = 0; i < n; i++)
    str = string.Concat(str, parts[i]);  // O(n²) 할당

// 수정
var sb = new StringBuilder(capacity: items.Count * 10);
foreach (var item in items)
{
    sb.Append(item.Name);
    sb.Append(", ");
}
string result = sb.ToString();

// 또는 최적화: Span 기반 조립
// ReadOnlySpan<char> 슬라이스로 직접 조립 (string 생성 0회)
```

### Pattern 6: 암묵적 배열·컬렉션 할당

```csharp
// HIGH: params 인수 호출마다 배열 생성
void Log(string msg, params object[] args)  // 호출마다 args[] 힙 할당
Log("value: {0}", intValue);  // 두 번 할당: args 배열 + 박스화된 intValue

// MEDIUM: 컬렉션 이니셜라이저의 초기 용량 미지정
var list = new List<int>();  // 기본 용량 4, 이후 realloc
// 수정: new List<int>(expectedCount);

// MEDIUM: yield return 생성기 (IEnumerable 상태 머신 힙 할당)
IEnumerable<int> GetValues() { yield return x; }  // hot path에서 사용 시
```

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **critical** | 요청당 수십 회+ 할당, 대형 배열, 누수 가능성 |
| **high** | 요청당 수 회 할당, boxing in loop, string concat |
| **medium** | 간헐적 할당, 소형 객체 반복 생성 |
| **low** | 이론적 개선 가능, 실측 영향 미미 |

## 출력 저장

완성된 JSON을 `_workspace/02_allocation_findings.json`에 Write한다.
버퍼/배열 관련 발견을 `pooling-enforcer`에게 SendMessage로 공유한다.
리더에게 완료를 알린다.
