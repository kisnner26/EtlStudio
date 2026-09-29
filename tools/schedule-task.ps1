<#
Programa una carga diaria de EtlStudio en el Programador de tareas de Windows.
  .\tools\schedule-task.ps1 -Spec C:\etl\spec.json -Package C:\etl\salida.dtsx -Hora 02:00 [-Notify https://...]
Corre con tu usuario (necesita tu DSN con el token). El resultado queda en %APPDATA%\EtlStudio\logs y, si pasas -Notify, en el webhook.
#>
param(
    [Parameter(Mandatory)][string]$Spec,
    [Parameter(Mandatory)][string]$Package,
    [string]$Hora = '02:00',
    [string]$Nombre = 'EtlStudio-Carga',
    [string]$Notify = '',
    [string]$Exe = "$env:LOCALAPPDATA\Programs\EtlStudio\EtlStudio.exe"
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "No encontre $Exe. Pasa la ruta con -Exe." }
$args_ = "--all `"$Spec`" `"$Package`""
if ($Notify) { $args_ += " --notify `"$Notify`"" }
$action  = New-ScheduledTaskAction -Execute $Exe -Argument $args_
$trigger = New-ScheduledTaskTrigger -Daily -At $Hora
$set     = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 4)
Register-ScheduledTask -TaskName $Nombre -Action $action -Trigger $trigger -Settings $set -Force | Out-Null
"Tarea '$Nombre' programada todos los dias a las $Hora."
