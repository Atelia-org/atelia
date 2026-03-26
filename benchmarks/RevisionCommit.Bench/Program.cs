using BenchmarkDotNet.Running;
using Atelia.RevisionCommit.Bench;

BenchmarkSwitcher.FromAssembly(typeof(CommitBenchmarks).Assembly).Run(args);
