<#
.SYNOPSIS
    Validates an architectural implementation plan against the 5-lens refinement protocol.
.DESCRIPTION
    Verifies that the target implementation plan:
    1. Contains a valid 'Refinement Status Gate' header.
    2. Does not unlock the 'Implementation Gate' prematurely while lenses remain pending.
    3. Contains no banned architectural anti-patterns (implicit/explicit cast operators, brittle mock invocations).
    4. Enforces vertical feature slice naming conventions.
.PARAMETER Path
    The absolute or relative path to the implementation plan markdown file.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -Path $Path)) {
    Write-Error "Target plan file not found: $Path"
    exit 1
}

$content = Get-Content -Path $Path -Raw
$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

Write-Host "Validating implementation plan: $Path" -ForegroundColor Cyan

# 1. Check for Refinement Status Gate
if ($content -notmatch "##\s+Refinement Status Gate") {
    $errors.Add("Missing required '## Refinement Status Gate' header.")
}

# 2. Check for each of the 5 Lenses
$lenses = @(
    "Lens 1: Scope & Feature Slice Framing",
    "Lens 2: Onion Architecture & Primitive Obsession",
    "Lens 3: Threat Modeling & Security Posture",
    "Lens 4: High-Value, Low-Brittle Unit Test Design",
    "Lens 5: Cross-Layer Infrastructure Alignment"
)

$pendingLenses = 0
foreach ($lens in $lenses) {
    if ($content -match "\[ \]\s+$([regex]::Escape($lens))") {
        $pendingLenses++
        Write-Host "  [-] ${lens}: PENDING" -ForegroundColor Yellow
    } elseif ($content -match "\[x\]\s+$([regex]::Escape($lens))") {
        Write-Host "  [+] ${lens}: APPROVED" -ForegroundColor Green
    } else {
        $errors.Add("Missing status line for '$lens'.")
    }
}

# 3. Check Implementation Gate status
if ($content -match "Implementation Gate:\s*UNLOCKED") {
    if ($pendingLenses -gt 0) {
        $errors.Add("Implementation Gate is marked UNLOCKED, but $pendingLenses lens(es) are still pending!")
    } else {
        Write-Host "  [+] Implementation Gate: UNLOCKED (All lenses approved)" -ForegroundColor Green
    }
} elseif ($content -match "Implementation Gate:\s*LOCKED") {
    Write-Host "  [i] Implementation Gate: LOCKED (Refinement in progress)" -ForegroundColor Cyan
} else {
    $errors.Add("Missing 'Implementation Gate: LOCKED' or 'UNLOCKED' directive.")
}

# 4. Anti-Pattern Scan: Cast Operators
if ($content -match "(public\s+static\s+)?(implicit|explicit)\s+operator") {
    $errors.Add("Banned anti-pattern detected: Implicit or explicit cast operators found in plan. Use explicit factory methods.")
}

# 5. Anti-Pattern Scan: Brittle Mock Invocations
if ($content -match "\.Verify\s*\(|\bTimes\.(Once|Exactly|AtLeastOnce)\b") {
    $errors.Add("Banned anti-pattern detected: Mock invocation verification (.Verify / Times.Once) found in test plan. Assert observable state transitions instead.")
}

# 6. Anti-Pattern Scan: Horizontal Layer Folders
if ($content -match "(?m)^[\s-]*`?(Services|Models|Helpers|Utilities|DTOs)/`?") {
    $warnings.Add("Detected horizontal layering (`Services/`, `Models/`, etc.). Plans should organize by vertical feature slices (`Features/<FeatureName>/`).")
}

# Summary
Write-Host ""
if ($warnings.Count -gt 0) {
    Write-Host "Warnings ($($warnings.Count)):" -ForegroundColor Yellow
    foreach ($w in $warnings) {
        Write-Host "  - $w" -ForegroundColor Yellow
    }
}

if ($errors.Count -gt 0) {
    Write-Host "Validation FAILED with $($errors.Count) error(s):" -ForegroundColor Red
    foreach ($e in $errors) {
        Write-Host "  - $e" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Plan validation PASSED! The plan conforms to the refinement protocol." -ForegroundColor Green
exit 0
