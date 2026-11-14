try {
    & (Join-Path $PSScriptRoot 'restore-transfer.ps1')
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Read-Host 'Press Enter to close'
}
