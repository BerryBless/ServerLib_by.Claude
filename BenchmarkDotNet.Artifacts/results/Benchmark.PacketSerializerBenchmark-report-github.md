```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8457)
Intel Core i9-14900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.300
  [Host] : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2


```
| Method                                    | Job      | Runtime   | IterationCount | LaunchCount | WarmupCount | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------ |--------- |---------- |--------------- |------------ |------------ |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| &#39;new byte[] + Encoding.GetBytes&#39;          | .NET 9.0 | .NET 9.0  | Default        | Default     | Default     |         NA |         NA |        NA |     ? |       ? |     NA |        NA |           ? |
| &#39;ArrayPool.Rent + Span (Zero-Allocation)&#39; | .NET 9.0 | .NET 9.0  | Default        | Default     | Default     |         NA |         NA |        NA |     ? |       ? |     NA |        NA |           ? |
| &#39;HeaderParse from Span (Zero-Allocation)&#39; | .NET 9.0 | .NET 9.0  | Default        | Default     | Default     |         NA |         NA |        NA |     ? |       ? |     NA |        NA |           ? |
|                                           |          |           |                |             |             |            |            |           |       |         |        |           |             |
| &#39;new byte[] + Encoding.GetBytes&#39;          | ShortRun | .NET 10.0 | 3              | 1           | 3           | 21.6525 ns | 16.5956 ns | 0.9097 ns | 1.001 |    0.05 | 0.0068 |     128 B |        1.00 |
| &#39;ArrayPool.Rent + Span (Zero-Allocation)&#39; | ShortRun | .NET 10.0 | 3              | 1           | 3           |  5.7605 ns |  1.1972 ns | 0.0656 ns | 0.266 |    0.01 |      - |         - |        0.00 |
| &#39;HeaderParse from Span (Zero-Allocation)&#39; | ShortRun | .NET 10.0 | 3              | 1           | 3           |  0.1976 ns |  0.1231 ns | 0.0067 ns | 0.009 |    0.00 |      - |         - |        0.00 |

Benchmarks with issues:
  PacketSerializerBenchmark.'new byte[] + Encoding.GetBytes': .NET 9.0(Runtime=.NET 9.0)
  PacketSerializerBenchmark.'ArrayPool.Rent + Span (Zero-Allocation)': .NET 9.0(Runtime=.NET 9.0)
  PacketSerializerBenchmark.'HeaderParse from Span (Zero-Allocation)': .NET 9.0(Runtime=.NET 9.0)
