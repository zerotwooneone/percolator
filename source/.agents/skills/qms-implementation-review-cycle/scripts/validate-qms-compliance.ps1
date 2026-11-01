<#
.SYNOPSIS
    Automated QMS Compliance & Traceability Validator.
.DESCRIPTION
    Validates adherence to strict medical-device QMS guidelines:
    - Zero code symbols / interfaces in SystemRequirements.md (IEC 62304 Clause 5.2 / FDA 820.30(c))
    - Valid verification and control tags on SYS-* and DEP-* items
    - Globally unique semantic identifiers (<PREFIX>-<SLUG>)
    - Detection of concrete implementing class leaks in Requirements.md (IEC 62304 Clause 5.3)
#>

[CmdletBinding()]
param(
    [string]$RootPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RootPath)) {
    # Attempt to locate source root by walking upward for SystemRequirements.md or Percolator.sln
    $current = $PSScriptRoot
    while ($current -and -not (Test-Path (Join-Path $current "SystemRequirements.md")) -and -not (Test-Path (Join-Path $current "Percolator.sln"))) {
        $parent = Split-Path -Path $current -Parent
        if ($parent -eq $current) { break }
        $current = $parent
    }
    if (Test-Path (Join-Path $current "SystemRequirements.md")) {
        $ResolvedRoot = Resolve-Path $current
    } else {
        $ResolvedRoot = Resolve-Path (Join-Path $PSScriptRoot "../../../..")
    }
} else {
    $ResolvedRoot = Resolve-Path $RootPath
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   QMS COMPLIANCE & TRACEABILITY AUDIT SCRIPT    " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Scanning repository root: $($ResolvedRoot.Path)`n"

$hasErrors = $false

# 1. Validate SystemRequirements.md
$sysReqPath = Join-Path $ResolvedRoot.Path "SystemRequirements.md"
if (Test-Path $sysReqPath) {
    Write-Host "[1/3] Auditing SystemRequirements.md..." -ForegroundColor Yellow
    $sysLines = Get-Content $sysReqPath
    
    # Code-like syntax patterns forbidden in black-box System Requirements (IEC 62304 Clause 5.2)
    $forbiddenPatterns = @(
        '\binterface\s+I[A-Z]\w+',
        '\bclass\s+[A-Z]\w+',
        '\b(public|private|protected|internal)\s+(class|interface|struct|record|void|int|bool|string)\b',
        '\bnamespace\s+[A-Z]\w+'
    )
    
    for ($i = 0; $i -lt $sysLines.Count; $i++) {
        $line = $sysLines[$i]
        $lineNum = $i + 1

        if ($line -match '`SYS-[A-Z0-9-]+`') {
            # Check for verification tag
            if ($line -notmatch '\[Verify:\s*(Integration|Simulation|Unit)\]') {
                Write-Host "  [FAIL] Line ${lineNum}: Missing valid [Verify: Integration|Simulation|Unit] tag: $line" -ForegroundColor Red
                $hasErrors = $true
            }
            # Check control tag if mitigating
            if ($line -match 'mitigates|HAZ-' -and $line -notmatch '\[Control:\s*(Inherent|Protective|Information)\]') {
                Write-Host "  [WARN] Line ${lineNum}: Requirement mitigates a hazard but lacks [Control: Inherent|Protective|Information] tag." -ForegroundColor Yellow
            }
            # Check for forbidden code declaration patterns
            foreach ($pattern in $forbiddenPatterns) {
                if ($line -match $pattern) {
                    Write-Host "  [FAIL] Line ${lineNum}: Leaked code declaration '$($matches[0])' in system requirement (violates Clause 5.2): $line" -ForegroundColor Red
                    $hasErrors = $true
                }
            }
        }
    }
} else {
    Write-Host "  [SKIP] SystemRequirements.md not found at $($sysReqPath)." -ForegroundColor DarkGray
}

# 2. Validate Deployable Requirements.md files
Write-Host "`n[2/3] Auditing Deployable Requirements.md files..." -ForegroundColor Yellow
$reqFiles = Get-ChildItem -Path $ResolvedRoot.Path -Filter "Requirements.md" -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git|\.agents|\.gemini)[\\/]' }

foreach ($file in $reqFiles) {
    $relPath = $file.FullName.Substring($ResolvedRoot.Path.Length + 1)
    $lines = Get-Content $file.FullName
    $fileErrors = 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $lineNum = $i + 1

        if ($line -match '`DEP-[A-Z0-9-]+`') {
            # Check verification tag
            if ($line -notmatch '\[Verify:\s*(Unit|Integration|StaticAnalysis)\]') {
                Write-Host "  [FAIL] ${relPath}:${lineNum}: Missing valid [Verify: Unit|Integration|StaticAnalysis] tag." -ForegroundColor Red
                $fileErrors++
                $hasErrors = $true
            }
            # Check for concrete class declaration or leaks
            if ($line -match '\bclass\s+[A-Z]\w+' -or $line -match '\b(public|private|internal)\s+class\b') {
                Write-Host "  [FAIL] ${relPath}:${lineNum}: Concrete class definition leaked in Software Item Requirements (violates Clause 5.3.2): $line" -ForegroundColor Red
                $fileErrors++
                $hasErrors = $true
            }
        }
    }

    if ($fileErrors -eq 0) {
        Write-Host "  [PASS] $relPath is compliant." -ForegroundColor Green
    }
}

# 3. Global Semantic Identifier Uniqueness
Write-Host "`n[3/3] Checking Semantic Identifier Uniqueness..." -ForegroundColor Yellow
$allMdFiles = Get-ChildItem -Path $ResolvedRoot.Path -Filter "*.md" -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git|\.agents|\.gemini)[\\/]' }
$idDefinitions = @{}

foreach ($file in $allMdFiles) {
    $relPath = $file.FullName.Substring($ResolvedRoot.Path.Length + 1)
    $lines = Get-Content $file.FullName

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        # Match definition patterns like - `SYS-SLUG`: or ### `DEP-SLUG`
        if ($line -match '(?<!\[)`([A-Z]{2,6}-[A-Z0-9-]+)`(?=:|\s|$)') {
            $id = $matches[1]
            if ($id -match '^(SYS|DEP|HAZ|UN|IU|USR|ENV|CONTRA|FACT|DEC|INV|SOUP)-') {
                if ($idDefinitions.ContainsKey($id)) {
                    Write-Host "  [FAIL] Duplicate semantic ID definition found for '$id' in $relPath (previously in $($idDefinitions[$id]))" -ForegroundColor Red
                    $hasErrors = $true
                } else {
                    $idDefinitions[$id] = $relPath
                }
            }
        }
    }
}

Write-Host "`nChecked $($idDefinitions.Count) unique semantic items." -ForegroundColor DarkGray

Write-Host "`n==================================================" -ForegroundColor Cyan
if ($hasErrors) {
    Write-Host "   AUDIT FAILED: QMS Violations Detected!       " -ForegroundColor Red
    Write-Host "==================================================" -ForegroundColor Cyan
    exit 1
} else {
    Write-Host "   AUDIT PASSED: All QMS checks compliant!       " -ForegroundColor Green
    Write-Host "==================================================" -ForegroundColor Cyan
    exit 0
}
