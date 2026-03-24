using BenchmarkDotNet.Running;
using Atelia.StringPool.Bench;

BenchmarkSwitcher.FromAssembly(typeof(StringPoolStoreBenchmarks).Assembly).Run(args);
