param([switch]$Client)
$ErrorActionPreference = 'Continue'
$pipeName = 'CleanerC-Pipe-Test2'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$nl = [string][char]10

if ($Client) {
  $o = @(); $o += 'CLIENT IsAdmin=' + $isAdmin
  try {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    $c.Connect(15000)
    $o += 'CLIENT connected at ' + (Get-Date).ToString('HH:mm:ss.fff')
    $w = New-Object System.IO.StreamWriter($c)
    $w.WriteLine('hello-from-elevated-client')
    $w.WriteLine('clientIsAdmin=' + $isAdmin)
    $w.Flush()
    $w.Dispose(); $c.Dispose()
    $o += 'CLIENT sent OK'
  } catch { $o += 'CLIENT FAILED: ' + $_.Exception.Message }
  [string]::Join($nl, $o) | Set-Content -Path (Join-Path $PSScriptRoot 'pipe-client-report.txt') -Encoding UTF8
  exit 0
}

$log = @()
$log += 'SERVER IsAdmin=' + $isAdmin
$srv = New-Object System.IO.Pipes.NamedPipeServerStream($pipeName, [System.IO.Pipes.PipeDirection]::In, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
$async = $srv.BeginWaitForConnection($null, $null)
$log += 'SERVER pipe created, waiting (async)'
$proc = $null
try {
  $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath, '-Client') -Verb RunAs -PassThru
  $log += 'SERVER elevation accepted'
} catch { $log += 'SERVER elevation FAILED: ' + $_.Exception.Message }

if ($async.AsyncWaitHandle.WaitOne(20000)) {
  $srv.EndWaitForConnection($async)
  $log += 'SERVER client connected'
  $r = New-Object System.IO.StreamReader($srv)
  $line = $r.ReadLine()
  while ($null -ne $line) { $log += '  RECV: ' + $line; $line = $r.ReadLine() }
  $log += 'SERVER read finished'
} else { $log += 'SERVER TIMEOUT: no connection in 20s' }

if ($proc) { $ok = $proc.WaitForExit(8000); $log += 'SERVER client exited=' + $ok + ' code=' + $proc.ExitCode }
$log += 'SERVER client-side report:'
$cr = Join-Path $PSScriptRoot 'pipe-client-report.txt'
if (Test-Path $cr) { Get-Content $cr | ForEach-Object { $log += '  | ' + $_ } } else { $log += '  (none)' }
[string]::Join($nl, $log)
