using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(SJValueLayoutBenchmarks).Assembly).Run(args);
