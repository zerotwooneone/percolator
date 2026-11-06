<#
.SYNOPSIS
    Linter and verifier for source file character encodings and line endings.
.DESCRIPTION
    Scans files in the repository to ensure:
    1. Valid UTF-8 encoding (rejects UTF-16, UTF-32, and non-UTF byte sequences).
    2. Prohibits UTF-8 Byte Order Mark (BOM) unless explicitly allowed.
    3. Detects mixed line endings within the same file.
    4. Enforces the specified line ending standard (default: CRLF for Windows).
    Optionally fixes violations with the -Fix switch.
.PARAMETER Path
    The root path to scan. Defaults to the repository source directory.
.PARAMETER LineEnding
    The expected line ending: 'crlf' (default) or 'lf'.
.PARAMETER Fix
    Automatically fix encoding and line-ending violations in place.
.PARAMETER AllowBom
    Allow UTF-8 Byte Order Mark (BOM). Default is $false.
#>
[CmdletBinding()]
param(
    [string]$Path = "$PSScriptRoot/..",
    [ValidateSet('crlf', 'lf')]
    [string]$LineEnding = 'crlf',
    [switch]$Fix,
    [switch]$AllowBom
)

$targetPath = [System.IO.Path]::GetFullPath($Path)
Write-Host "=== Source Code Encoding & Line-Ending Linter ===" -ForegroundColor Cyan
Write-Host "Scanning path: $targetPath"
Write-Host "Expected line ending: $($LineEnding.ToUpper())"
Write-Host "Enforce UTF-8 without BOM: $(-not $AllowBom)"
Write-Host "Auto-fix mode: $([bool]$Fix)"
Write-Host ""

$extensions = @('.cs', '.csx', '.json', '.xml', '.xaml', '.axaml', '.md', '.props', '.targets', '.csproj', '.sln', '.proto', '.ps1')
$excludePatterns = @('\bin\', '\obj\', '\.git\', '\.vs\', '\.idea\', '\node_modules\', '\artifacts\')

$allFiles = Get-ChildItem -Path $targetPath -Recurse -File | Where-Object {
    $file = $_
    $ext = $file.Extension.ToLowerInvariant()
    if ($extensions -notcontains $ext) { return $false }
    foreach ($pattern in $excludePatterns) {
        if ($file.FullName -match [regex]::Escape($pattern)) { return $false }
    }
    return $true
}

$utf8Throwing = New-Object System.Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$totalChecked = 0
$violationCount = 0
$fixedCount = 0

foreach ($file in $allFiles) {
    $totalChecked++
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -eq 0) { continue }

    $hasUtf8Bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $hasUtf16LeBom = ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE)
    $hasUtf16BeBom = ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF)

    $errors = @()

    if ($hasUtf16LeBom -or $hasUtf16BeBom) {
        $errors += "Non-UTF-8 encoding: UTF-16 BOM detected"
    }

    if ($hasUtf8Bom -and -not $AllowBom) {
        $errors += "UTF-8 BOM detected (BOM prohibited by standard)"
    }

    # Verify UTF-8 byte validity
    $text = $null
    try {
        $startIndex = if ($hasUtf8Bom) { 3 } else { 0 }
        $text = $utf8Throwing.GetString($bytes, $startIndex, $bytes.Length - $startIndex)
    }
    catch {
        $errors += "Invalid UTF-8 byte sequence: $($_.Exception.Message)"
    }

    if ($text -ne $null) {
        # Check for mixed line endings:
        $orphanLf = [regex]::IsMatch($text, '(?<!\r)\n')
        $orphanCr = [regex]::IsMatch($text, '\r(?!\n)')
        $hasCrlf = $text.Contains("`r`n")

        if ($hasCrlf -and ($orphanLf -or $orphanCr)) {
            $errors += "Mixed line endings in file: contains both CRLF and orphan LF/CR"
        }
        elseif ($LineEnding -eq 'crlf' -and $orphanLf) {
            $errors += "Line ending mismatch: expected CRLF but found LF"
        }
        elseif ($LineEnding -eq 'lf' -and $hasCrlf) {
            $errors += "Line ending mismatch: expected LF but found CRLF"
        }
    }

    if ($errors.Count -gt 0) {
        $violationCount++
        $relPath = $file.FullName.Substring($targetPath.Length).TrimStart('\', '/')
        Write-Host "[FAIL] $relPath" -ForegroundColor Red
        foreach ($err in $errors) {
            Write-Host "       -> $err" -ForegroundColor Yellow
        }

        if ($Fix -and $text -ne $null) {
            $targetNewline = if ($LineEnding -eq 'crlf') { "`r`n" } else { "`n" }
            $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
            if ($targetNewline -eq "`r`n") {
                $normalized = $normalized.Replace("`n", "`r`n")
            }
            [System.IO.File]::WriteAllText($file.FullName, $normalized, $utf8NoBom)
            $fixedCount++
            Write-Host "       [FIXED] Rewritten as UTF-8 (no BOM) with $LineEnding line endings." -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host "=== Scan Complete ===" -ForegroundColor Cyan
Write-Host "Files Scanned: $totalChecked"
Write-Host "Violations:    $violationCount"
if ($Fix) {
    Write-Host "Files Fixed:   $fixedCount"
}

if ($violationCount -gt 0 -and -not $Fix) {
    Write-Host ""
    Write-Host "Run with -Fix switch to automatically normalize all files:" -ForegroundColor Yellow
    Write-Host "  pwsh scripts/verify-code-integrity.ps1 -Fix" -ForegroundColor White
    exit 1
}

exit 0
