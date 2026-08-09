```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i9-12900K 3.19GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3


```
| Method               | Mean        | Error     | StdDev    | Ratio | Gen0     | Allocated  | Alloc Ratio |
|--------------------- |------------:|----------:|----------:|------:|---------:|-----------:|------------:|
| Tables               | 3,903.69 μs | 36.801 μs | 32.623 μs | 1.000 | 207.0313 | 3192.53 KB |       1.000 |
| Modules              | 1,795.47 μs | 15.237 μs | 14.253 μs | 0.460 |  56.6406 |  891.47 KB |       0.279 |
| TableTypes           |   191.45 μs |  1.739 μs |  1.453 μs | 0.049 |   8.3008 |  129.21 KB |       0.040 |
| ExtendedProperties   |   129.54 μs |  1.524 μs |  1.351 μs | 0.033 |   4.3945 |   68.13 KB |       0.021 |
| SequencesAndSynonyms |    12.02 μs |  0.179 μs |  0.158 μs | 0.003 |   0.5951 |    9.32 KB |       0.003 |
