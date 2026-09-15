/// <summary>Complete baseline output. / 完整基线输出。</summary>
internal sealed record BenchmarkReport(DateTimeOffset MeasuredAt, string OS, string Runtime, int LogicalProcessors, StartupResults Startup, CrudResults Crud, IReadOnlyList<SearchScaleResults> Search);

/// <summary>Startup and IPC distributions. / 启动与 IPC 分布。</summary>
internal sealed record StartupResults(Distribution ColdProfileReadyMs, Distribution WarmProfileReadyMs, Distribution IpcPingMs);

/// <summary>Encrypted CRUD results. / 加密 CRUD 结果。</summary>
internal sealed record CrudResults(OperationStats Set, OperationStats Get, OperationStats Delete, int ValueCharacters);

/// <summary>Sequential operation throughput and latency. / 顺序操作吞吐量与延迟。</summary>
internal sealed record OperationStats(double OperationsPerSecond, Distribution LatencyMs);

/// <summary>Search results for one database size. / 单个数据库规模的搜索结果。</summary>
internal sealed record SearchScaleResults(int TotalRecords, IReadOnlyList<SearchScenarioResults> Scenarios, IReadOnlyList<SearchPhaseResults> Phases);

/// <summary>One end-to-end search scenario. / 单个端到端搜索场景。</summary>
internal sealed record SearchScenarioResults(string Scenario, int CandidateRecords, int ResultCount, int Timeouts, Distribution LatencyMs);

/// <summary>Separated metadata and matcher timings. / 分离的元数据与匹配器计时。</summary>
internal sealed record SearchPhaseResults(string ScopeSelection, int CandidateRecords, Distribution MetadataLoadMs, Distribution FuzzyMatcherMs);

/// <summary>Nearest-rank latency distribution. / 最近秩延迟分布。</summary>
internal sealed record Distribution(int Samples, double Mean, double P50, double P95, double P99);

/// <summary>Repeated daemon resource-footprint results. / 重复的守护进程资源占用结果。</summary>
internal sealed record ResourceFootprintReport(DateTimeOffset MeasuredAt, string OS, string Runtime, int LogicalProcessors, string Method, IReadOnlyList<ResourceRun> IdleRuns, IReadOnlyList<ResourceRun> Search10kFuzzyRuns, string GuiNote);

/// <summary>One fixed-duration resource sampling run. / 一次固定时长的资源采样运行。</summary>
internal sealed record ResourceRun(int Samples, double WallSeconds, int CompletedOperations, double EquivalentCorePercent, double HostNormalizedCpuPercent, RangeDistribution WorkingSetMiB, RangeDistribution PrivateBytesMiB);

/// <summary>Nearest-rank distribution including observed extrema. / 包含观测极值的最近秩分布。</summary>
internal sealed record RangeDistribution(int Samples, double Mean, double Min, double P50, double P95, double P99, double Max);
