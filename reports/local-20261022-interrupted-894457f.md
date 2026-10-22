# Partial real-file performance run

- **Run ID:** `local-20261022-interrupted-894457f`
- **Commit:** [`894457f`](https://github.com/squid-farm/talewright/commit/894457f9c6525c8ea5584066db16daf2bc0a9314)
- **Branch:** `feature/57-real-file-performance-benchmarks`
- **Status:** Interrupted; not a baseline
- **Environment:** Linux Mint 22.3; AMD Ryzen 9 7940HS; .NET 10.0.13; 16 logical / 8 physical cores

> These timings are partial local measurements with concurrent system activity. Do not treat them as a comparable release baseline or evidence of a regression.

## Completed BenchmarkDotNet core cases

45 of 60 cases completed (15 methods × 4 corpus tiers); the process was stopped during the next case. BenchmarkDotNet did not emit its final CSV. Means below are parsed from completed case summaries. P95 is nearest-rank from that case's completed workload samples.

| Tier | Method | Mean | P95 (completed samples) | N |
|---|---|---:|---:|---:|
| Gigantic | LoadFromDirectory | 68.901 ms | 76.6628 ms | 27 |
| Gigantic | LoadAndCompileDirectory | 1.367 s | 2.1476 s | 29 |
| Gigantic | LoadFromZip | 285.220 ms | 350.0241 ms | 30 |
| Gigantic | LoadAndCompileZip | 3.037 s | 4.1695 s | 30 |
| Gigantic | CompileInMemory | 1.233 s | 1.4957 s | 25 |
| Gigantic | StartupAndFirstFrame | 14.957 ms | 15.0722 ms | 23 |
| Gigantic | SerializeSaveAtHistoryDepth | 148.389 us | 151.9735 us | 26 |
| Gigantic | RestoreAtHistoryDepth | 273.688 ms | 286.3628 ms | 27 |
| Gigantic | FileSaveWriteAndReadAtHistoryDepth | 8.728 ms | 925.4446 us | 30 |
| Gigantic | CliStoryOnlyPublish | 1.653 s | 1.7138 s | 29 |
| Gigantic | GenerateAndCleanTypicalDiskCorpus | 12.726 ms | 22.2351 ms | 30 |
| Gigantic | FirstProcessCliValidation | 2.132 s | 2.6400 s | 28 |
| Gigantic | ChooseAtHistoryDepth | 62.233 us | 139.9460 us | 29 |
| Gigantic | ChooseFromBoundedHub | 90.650 us | 192.7090 us | 26 |
| Gigantic | BackAndForwardAtHistoryDepth | 162.109 ms | 166.9808 ms | 24 |
| Large | LoadFromDirectory | 39.023 ms | 40.2762 ms | 27 |
| Large | LoadAndCompileDirectory | 296.684 ms | 330.2416 ms | 27 |
| Large | LoadFromZip | 80.805 ms | 98.2146 ms | 30 |
| Large | LoadAndCompileZip | 322.180 ms | 352.7618 ms | 26 |
| Large | CompileInMemory | 239.433 ms | 246.1994 ms | 28 |
| Large | StartupAndFirstFrame | 3.038 ms | 3.9821 ms | 30 |
| Large | SerializeSaveAtHistoryDepth | 62.381 us | 67.9646 us | 30 |
| Large | RestoreAtHistoryDepth | 83.383 ms | 92.0848 ms | 25 |
| Large | FileSaveWriteAndReadAtHistoryDepth | 768.112 us | 872.0973 us | 30 |
| Large | CliStoryOnlyPublish | 433.839 ms | 443.2675 ms | 27 |
| Large | GenerateAndCleanTypicalDiskCorpus | 5.308 ms | 7.0180 ms | 30 |
| Large | FirstProcessCliValidation | 820.318 ms | 843.7533 ms | 28 |
| Large | ChooseAtHistoryDepth | 80.868 us | 174.8110 us | 30 |
| Large | ChooseFromBoundedHub | 121.633 us | 157.2740 us | 28 |
| Large | BackAndForwardAtHistoryDepth | 68.039 ms | 73.0183 ms | 27 |
| Medium | LoadFromDirectory | 10.133 ms | 10.7577 ms | 30 |
| Medium | LoadAndCompileDirectory | 38.433 ms | 40.1133 ms | 30 |
| Medium | LoadFromZip | 20.722 ms | 24.3537 ms | 30 |
| Medium | LoadAndCompileZip | 45.158 ms | 47.4552 ms | 27 |
| Medium | CompileInMemory | 36.245 ms | 38.5652 ms | 28 |
| Medium | StartupAndFirstFrame | 110.230 us | 112.5787 us | 27 |
| Medium | SerializeSaveAtHistoryDepth | 12.359 us | 12.5316 us | 23 |
| Medium | RestoreAtHistoryDepth | 8.187 ms | 8.8725 ms | 26 |
| Medium | FileSaveWriteAndReadAtHistoryDepth | 802.233 us | 958.4835 us | 30 |
| Medium | CliStoryOnlyPublish | 76.741 ms | 85.4656 ms | 28 |
| Medium | GenerateAndCleanTypicalDiskCorpus | 6.976 ms | 7.8638 ms | 26 |
| Medium | FirstProcessCliValidation | 357.569 ms | 382.6389 ms | 26 |
| Medium | ChooseAtHistoryDepth | 826.093 us | 841.3710 us | 30 |
| Medium | ChooseFromBoundedHub | 118.853 us | 154.1390 us | 26 |
| Medium | BackAndForwardAtHistoryDepth | 19.186 ms | 29.2189 ms | 28 |

## Completed exact/over-limit boundary cases

| Boundary | Mean | P95 | Allocated |
|---|---:|---:|---:|
| count-exact | 14,754.3 μs | 18,391.7 μs | 84282.49 KB |
| count-over-one | 17,877.4 μs | 24,226.4 μs | 84283.3 KB |
| depth-exact | 1,680.4 μs | 2,007.7 μs | 221.99 KB |
| depth-over-one | 1,540.0 μs | 1,830.9 μs | 138.56 KB |
| file-exact | 31,743.3 μs | 33,507.8 μs | 30870.19 KB |
| file-over-one | 10,153.8 μs | 11,001.1 μs | 10242.61 KB |
| total-exact | 301,276.5 μs | 313,916.5 μs | 307916.69 KB |
| total-over-one | 303,344.1 μs | 407,974.6 μs | 287373.35 KB |
| zip-count-exact | 22,399.9 μs | 26,637.4 μs | 84202.2 KB |
| zip-count-over-one | 427.4 μs | 497.9 μs | 649.47 KB |
| zip-file-exact | 26,846.2 μs | 28,455.7 μs | 30877.05 KB |
| zip-file-over-one | 12,491.3 μs | 12,954.2 μs | 10336.04 KB |
| zip-total-exact | 281,587.3 μs | 306,532.3 μs | 307928.1 KB |
| zip-total-over-one | 232,364.0 μs | 244,314.8 μs | 287380.78 KB |

## Not completed / excluded

- The core suite stopped at 45/60 completed cases.
- Independent-dimension output from the earlier attempt contained only `NA` rows due to the metadata path issue and is excluded.
- Browser, Photino, and publication integration workloads were not run.
- Corpus verification passed for all four tiers before benchmarking; release build passed with zero warnings/errors.
- The raw interrupted core log and successful boundary CSV are retained in `evidence/`.
