# 패킷 직렬화 설계 문서

**작성일:** 2026-06-02  
**대상 경로:** `ServerLib/Core/Serialization/`  
**상태:** 구현 완료

---

## 1. 배경 및 목적

`IPacketSerializer` 인터페이스와 4바이트 패킷 헤더 구조(PacketId 2B + BodyLength 2B)는 이미 정의되어 있었으나, 실제 직렬화·역직렬화 구현체가 없었다.

**목적:** 외부 라이브러리(MessagePack, Protobuf 등) 없이 `Span<byte>` 기반 커스텀 바이너리 직렬화를 구현하여 ProudNet과 동일한 LittleEndian 바이너리 프로토콜을 채택한다.

---

## 2. 설계 결정

### 2.1 직렬화 포맷 선택

| 후보 | 장점 | 단점 | 선택 여부 |
|------|------|------|---------|
| 커스텀 바이너리 (Span) | Zero-allocation, 외부 의존성 없음, 완전한 제어 | 직접 구현 필요 | **✅ 채택** |
| MessagePack | 빠름, 스키마 진화 지원 | NuGet 의존성, 학습 비용 | ❌ |
| Protobuf | 강력한 스키마, 언어 중립 | 복잡한 설정, 코드 생성 필요 | ❌ |
| JSON (Utf8JsonWriter) | 가독성 | 크기 큼, 느림 | ❌ |

### 2.2 패킷 바이너리 구조

```
┌─────────────────────────────────────────────┐
│  Header (4 bytes, LittleEndian)             │
│  [PacketId: 2B] [BodyLength: 2B]            │
├─────────────────────────────────────────────┤
│  Body (BodyLength bytes)                    │
│  순서대로 직렬화된 필드들                       │
│  문자열: [길이 2B(ushort)] + [UTF-8 바이트]   │
└─────────────────────────────────────────────┘
```

- **인코딩:** LittleEndian (ProudNet과 동일)
- **문자열:** `[ushort byteCount] + [UTF-8 bytes]`, 최대 65535바이트
- **헤더 크기:** `PacketPool.HeaderSize = 4`

### 2.3 Zero-allocation 전략

| 연산 | 할당 여부 | 이유 |
|------|---------|------|
| `SpanWriter` 생성 | Zero | ref struct, 스택 전용 |
| `SpanReader` 생성 | Zero | ref struct, 스택 전용 |
| `Serialize<T>` | Zero | 대여 버퍼에 직접 기록 |
| `Deserialize<T>` (struct) | Zero | 스택 할당 |
| `Deserialize<T>` (class) | 1회 | `new T()` |
| `ReadString` | 1회 | `string` 객체 생성 불가피 |
| 분산 세그먼트 병합 | ArrayPool | 복사 최소화 |

---

## 3. 컴포넌트 구조

```
ServerLib/Core/Serialization/
├── SpanWriter.cs              ← ref struct, Span<byte> 인코더
│                                 BinaryPrimitives 기반 LittleEndian 기록
├── SpanReader.cs              ← ref struct, ReadOnlySpan<byte> 디코더
│                                 BinaryPrimitives 기반 LittleEndian 읽기
├── IPacket.cs                 ← 직렬화 계약 인터페이스
│                                 PacketId / Serialize / Deserialize / GetBodySize
├── BinaryPacketSerializer.cs  ← IPacketSerializer 구현체
│                                 PacketPool 헤더 유틸리티 재사용
└── Packets/
    ├── EchoPacket.cs          ← PacketId=1, Message(string)
    └── ChatPacket.cs          ← PacketId=2, Sender+Content(string)
```

### 컴포넌트 의존 관계

```
BinaryPacketSerializer
  ├── IPacketSerializer (Interface)
  ├── PacketPool (헤더 파싱/기록 재사용)
  ├── SpanWriter (직렬화)
  └── SpanReader (역직렬화)

EchoPacket / ChatPacket
  └── IPacket
      ├── SpanWriter (Serialize)
      └── SpanReader (Deserialize)
```

---

## 4. 핵심 API

### 4.1 IPacket 구현 패턴

```csharp
public sealed class MyPacket : IPacket
{
    public const ushort Id = 10;
    public ushort PacketId => Id;

    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;

    // 헤더 제외 본문 크기 계산
    public int GetBodySize() =>
        4 +                                    // int: 4바이트
        2 + Encoding.UTF8.GetByteCount(Name); // string: 2(길이) + UTF-8

    public void Serialize(ref SpanWriter w)
    {
        w.WriteInt32(Number);
        w.WriteString(Name);
    }

    public void Deserialize(ref SpanReader r)
    {
        Number = r.ReadInt32();
        Name = r.ReadString();
    }
}
```

### 4.2 직렬화 (송신)

```csharp
var serializer = new BinaryPacketSerializer();
var packet = new EchoPacket { Message = "Hello" };

// 버퍼 대여 → 직렬화 → 전송 → 반납
int totalSize = PacketPool.HeaderSize + packet.GetBodySize();
var rented = ArrayPool<byte>.Shared.Rent(totalSize);
try
{
    int written = serializer.Serialize(packet, rented);
    await session.SendAsync(rented.AsMemory(0, written));
}
finally
{
    ArrayPool<byte>.Shared.Return(rented);
}
```

### 4.3 역직렬화 (수신)

```csharp
// SocketPipelineListener.OnReceived 콜백 내부
listener.OnReceived = async (session, data) =>
{
    // 1. 헤더에서 PacketId 읽어 라우팅
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return;

    // 2. PacketId 기반 분기
    if (packetId == EchoPacket.Id)
    {
        var packet = serializer.Deserialize<EchoPacket>(data.Span);
        // ...처리...
    }
};
```

### 4.4 패킷 프레이밍 (SocketPipelineSession)

수신 루프가 `TryReadPacket`으로 완전한 패킷 경계를 감지한다.

```
PipeReader.ReadAsync()
  → TryReadPacket(ref buffer, out packet)
      → PacketPool.TryParseHeader(header) → bodyLength
      → buffer.Length >= HeaderSize + bodyLength ?
          YES → packet = buffer.Slice(0, totalLength)
                buffer = buffer.Slice(totalLength)
          NO  → 더 많은 데이터 대기 (AdvanceTo consumed, examined)
  → OnReceived(packet)  ← 항상 완전한 패킷만 전달
```

---

## 5. 변경 파일 목록

| 파일 | 유형 | 내용 |
|------|------|------|
| `ServerLib/Core/Serialization/SpanWriter.cs` | 신규 | LittleEndian 인코더 |
| `ServerLib/Core/Serialization/SpanReader.cs` | 신규 | LittleEndian 디코더 |
| `ServerLib/Core/Serialization/IPacket.cs` | 신규 | 직렬화 계약 |
| `ServerLib/Core/Serialization/BinaryPacketSerializer.cs` | 신규 | IPacketSerializer 구현 |
| `ServerLib/Core/Serialization/Packets/EchoPacket.cs` | 신규 | 예제 패킷 |
| `ServerLib/Core/Serialization/Packets/ChatPacket.cs` | 신규 | 예제 패킷 |
| `ServerLib/Interface/IPacketSerializer.cs` | 수정 | `where T : IPacket` 제약 추가 |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | 패킷 프레이밍 추가 |
| `ServerLib/Core/Transport/SocketPipelineClient.cs` | 수정 | 패킷 프레이밍 추가 |
| `Server/Program.cs` | 수정 | 직렬화 연동 에코 서버 예제 |
| `Client/Program.cs` | 수정 | 직렬화 패킷 전송 예제 |

---

## 6. 빌드 검증

```bash
dotnet build ClaudeCodeStudy.sln
dotnet run --project Server    # 포트 9000 에코 서버
dotnet run --project Client    # EchoPacket 전송 후 에코 수신
```

---

## 7. 향후 확장 포인트

- `RpcDispatcher`와 `BinaryPacketSerializer` 통합 — PacketId 자동 라우팅
- `Rpc.Generator` Source Generator로 `IPacket` 구현체 자동 생성
- `SpanWriter`/`SpanReader`에 `Guid`, `DateTime`, 배열 타입 추가
- `SocketPipelineSession.TryReadPacket`을 공용 `PacketFramer` 정적 클래스로 분리
