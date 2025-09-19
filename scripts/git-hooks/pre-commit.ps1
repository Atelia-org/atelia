# Optional pre-commit hook to catch whitespace and line-ending issues before they enter the repo
# Install: copy or symlink this file to .git/hooks/pre-commit (without extension) or invoke from a wrapper.
# Windows Git can execute PowerShell hooks if the file is called from a small sh or cmd shim.

param()

Write-Host "Running pre-commit checks (whitespace & EOL)" -ForegroundColor Cyan

# 1) Check for problematic whitespace (tabs vs spaces etc.) and EOL errors
# git diff --check emits lines with whitespace errors and CRLF-in-LF-only contexts
$diffCheck = git diff --cached --check 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host $diffCheck -ForegroundColor Red
    Write-Error "Whitespace/EOL issues detected by git diff --check. Please fix and re-stage."
    exit 1
}

# 2) Optionally verify no CRLF sneaks into LF-only types according to .gitattributes
# Relying on .gitattributes conversion is usually enough. Uncomment to enforce hard-fail on CRLF bytes in staged text files.
# $files = git diff --cached --name-only --diff-filter=ACM
# foreach ($f in $files) {
#     # quick binary check
#     $isBinary = git check-attr -a -- $f | Select-String -Pattern "^$f: (binary|diff)" | ForEach-Object { $_.Matches.Groups[1].Value -contains 'binary' }
#     if ($isBinary) { continue }
#     $content = git show :$f
#     if ($content -match "\r\n") {
#         # allow exceptions: ps1, sln, bat, cmd
#         if ($f -notmatch '\.(ps1|sln|bat|cmd)$') {
#             Write-Error "CRLF found in LF-normalized file: $f"
#             exit 1
#         }
#     }
# }

exit 0
