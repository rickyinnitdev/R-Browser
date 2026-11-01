param([string]$Url, [string]$Out = "D:\R1\shot.png", [int]$Wait = 12)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Shot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Get-Process RBrowser -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process "D:\R1\bin\Debug\net9.0-windows\RBrowser.exe" -ArgumentList $Url -PassThru
Start-Sleep -Milliseconds 1200
$p.Refresh()
# Put the window at the bottom of the z-order and on-screen: DWM keeps painting it,
# no focus is stolen from whatever the user is doing.
[Shot]::SetWindowPos($p.MainWindowHandle, [IntPtr]1, 0, 0, 1280, 840, 0x0010 -bor 0x0004) | Out-Null
Start-Sleep -Seconds $Wait
$r = New-Object Shot+RECT
[Shot]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h2 = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h2
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Shot]::PrintWindow($p.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"printwindow=$ok ${w}x${h2} -> $Out"
Stop-Process $p -Force
