```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i9-12900K 3.19GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3


```
| Method               | Mean        | Error     | StdDev    | Ratio | RatioSD | Gen0     | Allocated  | Alloc Ratio |
|--------------------- |------------:|----------:|----------:|------:|--------:|---------:|-----------:|------------:|
| Tables               | 3,888.45 μs | 74.716 μs | 73.382 μs | 1.000 |    0.03 | 207.0313 | 3203.45 KB |       1.000 |
| Modules              | 1,786.60 μs | 35.394 μs | 31.376 μs | 0.460 |    0.01 |  56.6406 |  891.47 KB |       0.278 |
| TableTypes           |   194.53 μs |  2.154 μs |  1.909 μs | 0.050 |    0.00 |   8.3008 |  129.21 KB |       0.040 |
| ExtendedProperties   |   134.70 μs |  1.938 μs |  1.812 μs | 0.035 |    0.00 |   4.3945 |   68.13 KB |       0.021 |
| SequencesAndSynonyms |    12.04 μs |  0.176 μs |  0.147 μs | 0.003 |    0.00 |   0.5951 |    9.32 KB |       0.003 |
