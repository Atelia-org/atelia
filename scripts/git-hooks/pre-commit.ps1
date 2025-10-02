# Optional pre-commit hook to catch whitespace and line-ending issues before they enter the repo
# Install: copy or symlink this file to .git/hooks/pre-commit (without extension) or invoke from a wrapper.
# Windows Git can execute PowerShell hooks if the file is called from a small sh wrapper (see install.ps1).

param()

$ErrorActionPreference = 'Stop'

# Resolve script path robustly (works when invoked via shim)
$ScriptPath = $MyInvocation.MyCommand.Path
if (-not $ScriptPath -or -not (Test-Path $ScriptPath)) {
  Write-Host "Pre-commit PowerShell script invoked incorrectly. Please reinstall hooks via scripts/git-hooks/install.ps1" -ForegroundColor Yellow
}

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
  # Run repository formatter on staged .cs files only (fast & minimal)
  if (Test-Path "$repoRoot/format.ps1") {
    Write-Info "Formatting staged C# files via ./format.ps1 -Scope staged"
    & "$repoRoot/format.ps1" -Scope staged | Out-Host
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
  } else {
    Write-Warn "format.ps1 not found at repo root. Skipping auto-format step."
  }

  Write-Info "Running pre-commit checks (whitespace & EOL)"

  # Auto-fix trailing whitespace
  $filesToClean = @(git diff --cached --name-only --diff-filter=ACM) |
    Where-Object {
      $_ -and
      (Test-Path $_)
    }

  Write-Warn "Auto-fixing trailing whitespace in $($filesToClean.Count) file(s)"
  if ($filesToClean.Count -gt 0) {
    $cleanedCount = 0
    foreach ($file in $filesToClean) {
      try {
        $content = Get-Content $file -Raw -ErrorAction Stop
        if ($null -ne $content -and $content -match '[ \t]+(\r?\n|$)') {
          # Remove trailing whitespace from each line, preserve line endings
          $cleaned = $content -replace '[ \t]+(\r?\n)', '$1' -replace '[ \t]+$', ''
          [System.IO.File]::WriteAllText($file, $cleaned, [System.Text.UTF8Encoding]::new($false))
          git add $file | Out-Null
          $cleanedCount++
        }
      } catch {
        Write-Warn "Failed to clean $file : $($_.Exception.Message)"
      }
    }
    if ($cleanedCount -gt 0) {
      Write-Info "Cleaned trailing whitespace from $cleanedCount file(s)"
    }
  }

  # Check staged snapshot for whitespace errors (should pass now after auto-fix)
  $stagedCheck = git diff --cached --check 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-Host $stagedCheck -ForegroundColor Red
    Write-Err "Whitespace/EOL issues in staged changes. Please fix or run the formatter."
    exit 1
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
