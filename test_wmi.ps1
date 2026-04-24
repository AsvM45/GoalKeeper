$action = {
    $proc = $Event.SourceEventArgs.NewEvent.TargetInstance
    Write-Host "ProcessCreated: $($proc.Name) | $($proc.ExecutablePath)"
}

Register-WmiEvent -Query "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'" -SourceIdentifier "ProcTrace" -Action $action

Write-Host "Monitoring..."
Start-Process Notepad.exe
Start-Sleep -Seconds 3

Unregister-Event -SourceIdentifier "ProcTrace"
Write-Host "Done"
