param([switch]$ShortcutOnly,[switch]$Launch)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$programDirectory = Join-Path $PSScriptRoot 'bin'
$programPath = Join-Path $programDirectory 'HongguoRemote.exe'
function Update-DesktopShortcut([string]$target) {
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    if (!$desktop -or !(Test-Path -LiteralPath $desktop -PathType Container)) { throw 'Desktop directory is unavailable.' }
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcutPath = Join-Path $desktop '红果遥控端.lnk'
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $target
        $shortcut.Arguments = ''
        $shortcut.WorkingDirectory = Split-Path $target -Parent
        $shortcut.IconLocation = $target + ',0'
        $shortcut.Description = '红果遥控端'
        $shortcut.Save()
        Write-Host "Updated desktop shortcut: $shortcutPath"
    } finally {
        if ($null -ne $shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}
if ($ShortcutOnly -or $Launch) {
    if (!(Test-Path -LiteralPath $programPath -PathType Leaf)) {
        Write-Host "Program not found: $programPath. Run build.ps1 first." -ForegroundColor Red
        exit 1
    }
    try { Update-DesktopShortcut $programPath } catch {
        Write-Warning ('Could not create desktop shortcut: ' + $_.Exception.Message)
        if (!$Launch) { exit 1 }
    }
    if ($Launch) {
        try { Start-Process -FilePath $programPath -WorkingDirectory $programDirectory }
        catch { Write-Host ('Could not start program: ' + $_.Exception.Message) -ForegroundColor Red; exit 1 }
    }
    exit 0
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path $compiler)) { throw 'Windows .NET Framework 4.x C# compiler is required.' }
New-Item -ItemType Directory -Force -Path $programDirectory | Out-Null
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','WPF\WindowsFormsIntegration.dll')
$argsList = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:' + $programPath),'/win32manifest:app.manifest','/win32icon:assets\hongguo.ico','/resource:src\MainWindow.xaml,MainWindow.xaml')
$argsList += $refs | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$argsList += Get-ChildItem src -Filter '*.cs' | ForEach-Object FullName
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $programPath"
$uninstallerPath = Join-Path $PSScriptRoot 'uninstall.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ('/out:' + $uninstallerPath) /reference:System.Windows.Forms.dll /win32icon:assets\hongguo.ico Uninstall.cs
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller build failed.' }
Write-Host "Built $uninstallerPath"
try { Update-DesktopShortcut $programPath } catch { Write-Warning ('Build succeeded, but could not create desktop shortcut: ' + $_.Exception.Message) }
