using BenchmarkDotNet.Running;
using Atelia.RevisionCommit.Bench;

BenchmarkSwitcher.FromAssembly(typeof(CompactionCommitBenchmarks).Assembly).Run(args);
