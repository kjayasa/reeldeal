<#
.SYNOPSIS
  Resets cms.db so it contains exactly one admin with a known password.

.DESCRIPTION
  Run this before committing cms.db. It prompts for the password (hidden, entered twice)
  and pipes it to `dotnet run -- --reset-admin`, so the password never appears on the
  command line or in shell history. Every other admin account in the database is removed.

.EXAMPLE
  .\tools\set-admin.ps1 you@example.com
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Email
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

function Read-Plain([string]$prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$password = Read-Plain "Password for $Email (min 8 chars)"
if ($password.Length -lt 8) { Write-Error "Password must be at least 8 characters."; exit 1 }
$confirm = Read-Plain "Confirm password"
if ($password -ne $confirm) { Write-Error "Passwords don't match."; exit 1 }

Write-Host "Resetting admins in $repoRoot\cms.db ..."
Push-Location $repoRoot
try {
    $password | dotnet run -- --reset-admin $Email
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally { Pop-Location }

Write-Host ""
Write-Host "Done. cms.db now has a single admin ($Email). You can commit it." -ForegroundColor Green
Write-Host "Remember: deploying this cms.db over a live one resets production admins too (see README)." -ForegroundColor Yellow
