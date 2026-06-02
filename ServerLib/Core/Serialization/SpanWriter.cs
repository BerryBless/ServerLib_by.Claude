using System.Buffers.Binary;
using System.Text;

namespace ServerLib.Core.Serialization;

/// <summary>
/// <see cref="Span{T}"/> 목적지 버퍼에 프리미티브를 LittleEndian으로 직접 기록하는 인코더입니다.
/// </summary>
/// <remarks>
/// <b>[Memory Allocation:]</b> Zero-allocation. ref struct이므로 스택에만 존재하며 힙 할당이 없습니다.
/// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// <b>[Blocking:]</b> Non-blocking. 모든 연산이 동기 즉시 반환됩니다.
/// </remarks>
public ref struct SpanWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    /// <summary>현재 쓰기 위치(바이트 오프셋)입니다.</summary>
    public int Position => _position;

    /// <summary>버퍼에 남은 쓰기 가능 용량(바이트)입니다.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>지정한 버퍼로 SpanWriter를 초기화합니다.</summary>
    /// <param name="buffer">기록 대상 버퍼입니다. 충분한 크기를 사전에 확보해야 합니다.</param>
    public SpanWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>1바이트 부호 없는 정수를 기록합니다.</summary>
    public void WriteByte(byte value) =>
        _buffer[_position++] = value;

    /// <summary>bool 값을 1바이트(0 또는 1)로 기록합니다.</summary>
    public void WriteBool(bool value) =>
        _buffer[_position++] = value ? (byte)1 : (byte)0;

    /// <summary>2바이트 부호 있는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteInt16(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position), value);
        _position += 2;
    }

    /// <summary>2바이트 부호 없는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), value);
        _position += 2;
    }

    /// <summary>4바이트 부호 있는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), value);
        _position += 4;
    }

    /// <summary>8바이트 부호 있는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position), value);
        _position += 8;
    }

    /// <summary>4바이트 단정밀도 부동소수를 LittleEndian으로 기록합니다.</summary>
    public void WriteFloat(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.Slice(_position), value);
        _position += 4;
    }

    /// <summary>8바이트 배정밀도 부동소수를 LittleEndian으로 기록합니다.</summary>
    public void WriteDouble(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.Slice(_position), value);
        _position += 8;
    }

    /// <summary>
    /// 바이트 배열을 길이 접두어 없이 버퍼에 직접 복사합니다.
    /// </summary>
    /// <param name="value">복사할 바이트 데이터입니다.</param>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 버퍼 내부에서 직접 복사합니다.
    /// </remarks>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_buffer.Slice(_position));
        _position += value.Length;
    }

    /// <summary>
    /// 문자열을 [길이(2B ushort) + UTF-8 바이트] 형식으로 기록합니다.
    /// 최대 문자열 바이트 길이: 65535.
    /// </summary>
    /// <param name="value">기록할 문자열입니다.</param>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> UTF-8 인코딩 과정에서 임시 버퍼 할당 없이 목적지에 직접 기록합니다.
    /// </remarks>
    public void WriteString(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), byteCount, "UTF-8 인코딩 바이트 길이가 65535를 초과합니다.");
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), (ushort)byteCount);
        _position += 2;
        Encoding.UTF8.GetBytes(value, _buffer.Slice(_position, byteCount));
        _position += byteCount;
    }
}
