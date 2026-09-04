using Xunit;

// EnvLoaderTests は Environment.SetEnvironmentVariable でプロセス全体の環境変数を書き換える。
// 環境変数はプロセス共有のリソースであり、テストを並列実行するとフレーキーになるため、
// アセンブリ全体でテストの並列実行を無効化する。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
