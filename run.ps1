# Launches this copy of the game, waits until BepInEx\LogOutput.log matches -WaitFor (or the
# timeout / game exit), then closes the game unless -KeepOpen. Prints the plugin-relevant log lines.
#
#   .\mod-src\run.ps1 -WaitFor 'Chainloader startup complete'
param(
    [string]$WaitFor = 'Chainloader startup complete',
    [int]$TimeoutSec = 120,
    [int]$SettleSec = 0,
    [switch]$KeepOpen,
    [string]$Show = 'Error|Warning|Fatal|Probe|LiveLure'
)
$root = Split-Path $PSScriptRoot -Parent
$log = Join-Path $root 'BepInEx\LogOutput.log'
if (Get-Process castnchill -ErrorAction SilentlyContinue) { Write-Host 'Game already running - close it first.'; exit 1 }
if (Test-Path $log) { Remove-Item $log -Force }

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CncWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    // The game's own window: the Unity window that belongs to this process, never the BepInEx console and never anyone else's.
    public static IntPtr GameWindow(uint pid) {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "UnityWndClass", null)) != IntPtr.Zero) {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid) return h;
        }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
}
'@

# Photographs the game window when the mod logs "SHOT <name>". PrintWindow asks the window to draw itself, so this
# gets the game and only the game even when another window is in front of it.
function Save-GameShot([System.Diagnostics.Process]$proc, [string]$name) {
    $proc.Refresh()
    # The process also owns the BepInEx console, which Windows sometimes reports as its main window.
    $h = [CncWin]::GameWindow([uint32]$proc.Id)
    $r = New-Object CncWin+RECT
    if ($h -eq [IntPtr]::Zero -or [CncWin]::IsIconic($h) -or -not [CncWin]::GetWindowRect($h, [ref]$r)) { Write-Host "skipped shot $name (no window)"; return }
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    if ($w -le 0 -or $ht -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [CncWin]::PrintWindow($h, $hdc, 2)   # 2 = PW_RENDERFULLCONTENT, needed for a Direct3D window
    $g.ReleaseHdc($hdc); $g.Dispose()
    $dir = Join-Path $root 'BepInEx\probe\shots'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $bmp.Save((Join-Path $dir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "captured $name.png (${w}x${ht}) ok=$ok"
}
# Real relative mouse movement: six small rod flicks (up, pause, back down), ending where it started.
function Send-Flicks {
    for ($i = 0; $i -lt 6; $i++) {
        for ($s = 0; $s -lt 8; $s++) { [CncWin]::mouse_event(1, 0, -25, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 15 }
        Start-Sleep -Milliseconds 300
        for ($s = 0; $s -lt 8; $s++) { [CncWin]::mouse_event(1, 0, 25, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 15 }
        Start-Sleep -Milliseconds 800
    }
}
$flicksSent = $false

$p = Start-Process -FilePath (Join-Path $root 'castnchill.exe') -WorkingDirectory $root -PassThru
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$matched = $false
$shotsTaken = 0
while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    Start-Sleep -Milliseconds 150
    if (Test-Path $log) {
        $text = Get-Content $log -Raw -ErrorAction SilentlyContinue
        if (-not $text) { continue }   # the game was mid-write, or has not logged anything yet
        $shots = [regex]::Matches($text, 'SHOT (\S+)')
        while ($shotsTaken -lt $shots.Count) {
            Save-GameShot $p ("{0:00}-{1}" -f $shotsTaken, $shots[$shotsTaken].Groups[1].Value)
            $shotsTaken++
        }
        if (-not $flicksSent -and $text -match 'MOUSE-TEST BEGIN') { $flicksSent = $true; Write-Host 'injecting mouse flicks'; Send-Flicks }
        if ($text -match $WaitFor) { $matched = $true; break }
    }
}
if ($matched -and $SettleSec -gt 0) { Start-Sleep -Seconds $SettleSec }
Write-Host ("matched={0} exited={1}" -f $matched, $p.HasExited)

if (-not $KeepOpen -and -not $p.HasExited) {
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(15000)) { $p.Kill(); Write-Host 'forced close' }
}
if (Test-Path $log) { Get-Content $log | Where-Object { $_ -match $Show } | Select-Object -First 80 }
