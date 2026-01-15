# RealTimeTranslator Build and Test Script
# This script automates the build and test process for the RealTimeTranslator project

param(
    [switch]$SkipTests,
    [switch]$SkipIntegrationTests,
    [switch]$Verbose,
    [string]$Configuration = "Debug",
    [string]$TestResultsPath = "TestResults"
)

# Set error action preference
$ErrorActionPreference = "Stop"

# Function to write colored output
function Write-ColoredOutput {
    param(
        [string]$Message,
        [string]$Color = "White"
    )

    $originalColor = $Host.UI.RawUI.ForegroundColor
    $Host.UI.RawUI.ForegroundColor = $Color
    Write-Host $Message
    $Host.UI.RawUI.ForegroundColor = $originalColor
}

# Function to execute command with error handling
function Invoke-CommandWithErrorHandling {
    param(
        [string]$Command,
        [string]$Description
    )

    if ($Verbose) {
        Write-ColoredOutput "Executing: $Command" "Cyan"
    }

    try {
        $result = Invoke-Expression $Command
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code $LASTEXITCODE"
        }
        return $result
    }
    catch {
        Write-ColoredOutput "ERROR: $Description failed - $($_.Exception.Message)" "Red"
        throw
    }
}

# Main script execution
try {
    Write-ColoredOutput "=== RealTimeTranslator Build and Test Script ===" "Green"
    Write-ColoredOutput "Configuration: $Configuration" "Yellow"
    Write-ColoredOutput "Skip Tests: $SkipTests" "Yellow"
    Write-ColoredOutput "Skip Integration Tests: $SkipIntegrationTests" "Yellow"
    Write-ColoredOutput ""

    # Change to project root directory
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    Set-Location $scriptDir

    Write-ColoredOutput "Working directory: $(Get-Location)" "Cyan"

    # Step 1: Restore packages
    Write-ColoredOutput "Step 1: Restoring NuGet packages..." "Yellow"
    Invoke-CommandWithErrorHandling "dotnet restore" "Package restoration"

    # Step 2: Build the solution
    Write-ColoredOutput "Step 2: Building solution..." "Yellow"
    $buildCommand = "dotnet build --configuration $Configuration"
    if ($Verbose) {
        $buildCommand += " --verbosity detailed"
    }
    Invoke-CommandWithErrorHandling $buildCommand "Solution build"

    # Step 3: Run tests (if not skipped)
    if (-not $SkipTests) {
        Write-ColoredOutput "Step 3: Running tests..." "Yellow"

        # Create test results directory
        if (-not (Test-Path $TestResultsPath)) {
            New-Item -ItemType Directory -Path $TestResultsPath | Out-Null
        }

        # Build test filter
        $testFilter = ""
        if ($SkipIntegrationTests) {
            $testFilter = "--filter TestCategory!=Integration"
        }

        $testCommand = "dotnet test --configuration $Configuration --logger `"trx;LogFileName=test-results.trx`" --results-directory $TestResultsPath $testFilter"

        if ($Verbose) {
            $testCommand += " --verbosity detailed"
        }

        Invoke-CommandWithErrorHandling $testCommand "Test execution"

        # Parse test results
        $trxFile = Get-ChildItem "$TestResultsPath/*.trx" | Select-Object -Last 1
        if ($trxFile) {
            Write-ColoredOutput "Test results saved to: $($trxFile.FullName)" "Cyan"

            # Simple test result summary (basic parsing)
            $content = Get-Content $trxFile.FullName -Raw
            $passed = [regex]::Match($content, 'passed="(\d+)"').Groups[1].Value
            $failed = [regex]::Match($content, 'failed="(\d+)"').Groups[1].Value
            $total = [regex]::Match($content, 'total="(\d+)"').Groups[1].Value

            Write-ColoredOutput "Test Results Summary:" "Green"
            Write-ColoredOutput "  Total: $total" "White"
            Write-ColoredOutput "  Passed: $passed" "Green"
            Write-ColoredOutput "  Failed: $failed" "Red"
        }
    } else {
        Write-ColoredOutput "Step 3: Tests skipped as requested" "Yellow"
    }

    # Step 4: Verify build artifacts
    Write-ColoredOutput "Step 4: Verifying build artifacts..." "Yellow"

    $coreDll = "src/RealTimeTranslator.Core/bin/$Configuration/net10.0-windows8.0/RealTimeTranslator.Core.dll"
    $translationDll = "src/RealTimeTranslator.Translation/bin/$Configuration/net10.0-windows8.0/RealTimeTranslator.Translation.dll"
    $uiExe = "src/RealTimeTranslator.UI/bin/$Configuration/net10.0-windows8.0/RealTimeTranslator.UI.exe"

    $artifacts = @($coreDll, $translationDll, $uiExe)
    $missingArtifacts = @()

    foreach ($artifact in $artifacts) {
        if (-not (Test-Path $artifact)) {
            $missingArtifacts += $artifact
        }
    }

    if ($missingArtifacts.Count -gt 0) {
        Write-ColoredOutput "WARNING: Missing build artifacts:" "Yellow"
        foreach ($artifact in $missingArtifacts) {
            Write-ColoredOutput "  $artifact" "Red"
        }
    } else {
        Write-ColoredOutput "All build artifacts verified successfully" "Green"
    }

    Write-ColoredOutput "" "White"
    Write-ColoredOutput "=== Build and Test completed successfully! ===" "Green"

} catch {
    Write-ColoredOutput "" "White"
    Write-ColoredOutput "=== Build and Test FAILED! ===" "Red"
    Write-ColoredOutput "Error: $($_.Exception.Message)" "Red"
    exit 1
}