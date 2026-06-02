using System.Buffers.Binary;
using System.Text;

namespace ServerLib.Core.Serialization;

/// <summary>
/// <see cref="ReadOnlySpan{T}"/> 소스 버퍼에서 프리미티브를 LittleEndian으로 읽는 디코더입니다.
/// </summary>
/// <remarks>
/// <b>[Memory Allocation:]</b> Zero-allocation (ref struct, 스택 전용). 단, <see cref="ReadString"/>은
/// <see cref="string"/> 객체 생성으로 인해 1회 힙 할당이 발생합니다.
/// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>현재 읽기 위치(바이트 오프셋)입니다.</summary>
    public int Position => _position;

    /// <summary>버퍼에 남은 읽기 가능 바이트 수입니다.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>지정한 버퍼로 SpanReader를 초기화합니다.</summary>
    /// <param name="buffer">읽기 대상 버퍼입니다.</param>
    public SpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>1바이트 부호 없는 정수를 읽습니다.</summary>
    public byte ReadByte() => _buffer[_position++];

    /// <summary>1바이트 bool 값을 읽습니다 (0이 아니면 true).</summary>
    public bool ReadBool() => _buffer[_position++] != 0;

    /// <summary>2바이트 부호 있는 정수를 LittleEndian으로 읽습니다.</summary>
    public short ReadInt16()
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position));
        _position += 2;
        return value;
    }

    /// <summary>2바이트 부호 없는 정수를 LittleEndian으로 읽습니다.</summary>
    public ushort ReadUInt16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
        _position += 2;
        return value;
    }

    /// <summary>4바이트 부호 있는 정수를 LittleEndian으로 읽습니다.</summary>
    public int ReadInt32()
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position));
        _position += 4;
        return value;
    }

    /// <summary>8바이트 부호 있는 정수를 LittleEndian으로 읽습니다.</summary>
    public long ReadInt64()
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position));
        _position += 8;
        return value;
    }

    /// <summary>4바이트 단정밀도 부동소수를 LittleEndian으로 읽습니다.</summary>
    public float ReadFloat()
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_position));
        _position += 4;
        return value;
    }

    /// <summary>8바이트 배정밀도 부동소수를 LittleEndian으로 읽습니다.</summary>
    public double ReadDouble()
    {
        var value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.Slice(_position));
        _position += 8;
        return value;
    }

    /// <summary>
    /// 지정한 길이만큼 바이트를 소스 버퍼에서 Zero-copy 슬라이스로 반환합니다.
    /// </summary>
    /// <param name="length">읽을 바이트 수입니다.</param>
    /// <returns>소스 버퍼의 슬라이스입니다. 원본 버퍼가 유효한 동안만 사용 가능합니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 복사 없이 슬라이스를 반환합니다.
    /// 반환된 <see cref="ReadOnlySpan{T}"/>는 이 SpanReader의 원본 버퍼 수명에 종속됩니다.
    /// </remarks>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        var span = _buffer.Slice(_position, length);
        _position += length;
        return span;
    }

    /// <summary>
    /// [길이(2B ushort) + UTF-8 바이트] 형식의 문자열을 읽습니다.
    /// </summary>
    /// <returns>디코딩된 문자열입니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> <see cref="string"/> 객체 생성으로 인해 1회 힙 할당이 발생합니다.
    /// hot path에서 문자열을 반복 처리하는 경우 <see cref="ReadBytes"/>로 원시 UTF-8 스팬을 받아
    /// 직접 처리하는 것을 고려하세요.
    /// </remarks>
    public string ReadString()
    {
        ushort byteCount = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
        _position += 2;
        var value = Encoding.UTF8.GetString(_buffer.Slice(_position, byteCount));
        _position += byteCount;
        return value;
    }
}
