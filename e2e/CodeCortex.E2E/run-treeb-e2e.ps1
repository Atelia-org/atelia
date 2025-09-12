param(
  [string]$Engine = 'treeb',
  [string]$Sln = 'e2e/CodeCortex.E2E/CodeCortex.E2E.sln',
  [int]$Limit = 30,
  [int]$Offset = 0
)

$cli = 'src/CodeCortexV2.DevCli/CodeCortexV2.DevCli.csproj'

function run($q) {
  Write-Host "==== find '$q' (engine=$Engine) ====" -ForegroundColor Cyan
  dotnet run --project $cli -- $Sln find $q --engine $Engine --limit $Limit --offset $Offset
  Write-Host ""
}

$tests = @(
  'A.B.A.B',
  'A.B.A.B.R1',
  'A.B.A.B.R2',
  'G.Outer',
  'G.Outer`1',
  'G.Outer`1.Inner',
  'Inner',
  'My.Collections.Generic.List',
  'My.Collections.List',
  'List<T>',
  'X.Y.AA',
  'Y.X.AA',
  'Ns.Partials.P`1'
)

foreach ($q in $tests) { run $q }

