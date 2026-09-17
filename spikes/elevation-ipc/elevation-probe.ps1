param([switch]$Elevated)
$ErrorActionPreference = 'Continue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$outDir = $PSScriptRoot
Add-Type -Path "$PSScriptRoot\vol.cs"
# 用字符码构造 \.\C: ，避免任何反斜杠转义问题
$devPath = [string]::new([char[]]@(92,92,46,92,67,58))

if ($Elevated) {
  $l = @()
  $l += "ELEVATED_INSTANCE"
  $l += "IsAdmin = $isAdmin"
  $l += "DevPath = $devPath"
  $l += "CWD = " + (Get-Location).Path
  $swIn = [Diagnostics.Stopwatch]::StartNew()
  $h = [Win32Vol]::CreateFileW($devPath, [uint32]2147483648, [uint32]7, [IntPtr]::Zero, [uint32]3, [uint32]0, [IntPtr]::Zero)
  if ($h -eq [IntPtr](-1)) { $l += "RawVolumeOpen = FAILED err=" + [Runtime.InteropServices.Marshal]::GetLastWin32Error() }
  else { $l += "RawVolumeOpen = SUCCESS"; [void][Win32Vol]::CloseHandle($h) }
  $l += "WorkMs = " + $swIn.ElapsedMilliseconds
  $l | Set-Content -Path (Join-Path $outDir 'elev2-inner.txt') -Encoding UTF8
  exit 0
}

$log = @()
$log += "IsAdmin(parent) = $isAdmin"
$log += "DevPath = $devPath"
$log += ""
$log += "--- 非提权下对卷的可用访问权限 ---"
$cases = @(
  @{n='GENERIC_READ';         a=[uint32]2147483648},
  @{n='GENERIC_WRITE';        a=[uint32]1073741824},
  @{n='FILE_READ_DATA';       a=[uint32]1},
  @{n='FILE_READ_ATTRIBUTES'; a=[uint32]128},
  @{n='0 (query only)';       a=[uint32]0}
)
foreach ($c in $cases) {
  $h = [Win32Vol]::CreateFileW($devPath, $c.a, [uint32]7, [IntPtr]::Zero, [uint32]3, [uint32]0, [IntPtr]::Zero)
  if ($h -eq [IntPtr](-1)) {
    $log += "  " + $c.n.PadRight(22) + " -> FAILED err=" + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
  } else {
    $log += "  " + $c.n.PadRight(22) + " -> SUCCESS"
    [void][Win32Vol]::CloseHandle($h)
  }
}
$log += ""
$log += "--- 按需提权往返 ---"
Remove-Item (Join-Path $outDir 'elev2-inner.txt') -ErrorAction SilentlyContinue
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
  $p = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-Elevated') -Verb RunAs -Wait -PassThru
  $sw.Stop()
  $log += "  OK: exit=" + $p.ExitCode + ", roundTrip=" + $sw.ElapsedMilliseconds + " ms"
} catch {
  $sw.Stop()
  $log += "  FAILED: " + $_.Exception.Message
}
$log += ""
$log += "--- 提权子进程回传 ---"
$inner = Join-Path $outDir 'elev2-inner.txt'
if (Test-Path $inner) { Get-Content $inner | ForEach-Object { $log += "  | " + $_ } }
else { $log += "  (none)" }
$log -join "`n"
