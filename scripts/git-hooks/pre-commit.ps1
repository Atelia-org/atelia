# Optional pre-commit hook to catch whitespace and line-ending issues before they enter the repo
# Install: copy or symlink this file to .git/hooks/pre-commit (without extension) or invoke from a wrapper.
# Windows Git can execute PowerShell hooks if the file is called from a small sh wrapper (see install.ps1).

param()

$ErrorActionPreference = 'Stop'

function Write-Info($m){ Write-Host $m -ForegroundColor Cyan }
function Write-Warn($m){ Write-Host $m -ForegroundColor Yellow }
function Write-Err($m){ Write-Host $m -ForegroundColor Red }

# Resolve repo root
$repoRoot = (& git rev-parse --show-toplevel) 2>$null
if (-not $repoRoot) {
  Write-Err "Not a git repository (cannot locate repo root)."
  exit 1
}

Push-Location $repoRoot
try {
  Write-Info "Running pre-commit checks (whitespace & EOL)"
  # Check staged snapshot for whitespace errors first (fast fail)
  $stagedCheck = git diff --cached --check 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-Host $stagedCheck -ForegroundColor Red
    Write-Err "Whitespace/EOL issues in staged changes. Please fix or run the formatter."
    exit 1
  }

  # Run repository formatter on staged .cs files only (fast & minimal)
  if (Test-Path "$repoRoot/format.ps1") {
    Write-Info "Formatting staged C# files via ./format.ps1 -Scope staged"
    pwsh -NoProfile -ExecutionPolicy Bypass -File "$repoRoot/format.ps1" -Scope staged | Out-Host
    if ($LASTEXITCODE -ne 0) {
      Write-Err "Formatter failed. Aborting commit."
      exit 1
    }

    # If formatter changed tracked files, restage modifications
    $unstaged = @(git diff --name-only) | Where-Object { $_ }
    if ($unstaged.Count -gt 0) {
      Write-Info "Restaging tracked modifications after formatting"
      git add --update | Out-Null
    }

    # Re-check staged snapshot after formatting/restaging
    $stagedCheck2 = git diff --cached --check 2>&1
    if ($LASTEXITCODE -ne 0) {
      Write-Host $stagedCheck2 -ForegroundColor Red
      Write-Err "Whitespace/EOL issues remain in staged snapshot after formatting. Aborting commit."
      exit 1
    }
  } else {
    Write-Warn "format.ps1 not found at repo root. Skipping auto-format step."
  }

  # Optional strict CRLF enforcement (disabled by default)
  # To enable, uncomment the block below. It forbids CRLF in LF-normalized files, allowing .ps1/.sln/.bat/.cmd.
  <#
  $files = @(git diff --cached --name-only --diff-filter=ACM) | Where-Object { $_ }
  foreach ($f in $files) {
    $isBinary = (git check-attr -a -- $f) -match ": binary: set"
    if ($isBinary) { continue }
    $blob = git show :$f
    if ($blob -match "\r\n" -and ($f -notmatch '\\.(ps1|sln|bat|cmd)$')) {
      Write-Err "CRLF found in staged LF-normalized file: $f"
      exit 1
    }
  }
  #>

  exit 0
}
finally {
  Pop-Location
}
