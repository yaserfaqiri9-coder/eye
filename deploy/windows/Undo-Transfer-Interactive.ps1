try {
    & (Join-Path $PSScriptRoot 'undo-transfer.ps1')
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Read-Host 'Press Enter to close'
}
