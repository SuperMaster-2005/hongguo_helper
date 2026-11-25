using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class Uninstall
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            ValidateDirectory(root);
            foreach (Process process in Process.GetProcessesByName("HongguoRemote"))
            {
                using (process)
                {
                    // 无法核实进程路径时也停止卸载，避免误删正在使用的文件。
                    if (String.Equals(process.MainModule.FileName, Path.Combine(root, "bin", "HongguoRemote.exe"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("请先退出红果遥控端，再运行卸载程序。");
                }
            }
            if (MessageBox.Show("将永久删除以下整个文件夹及其中所有文件：\r\n\r\n" + root +
                "\r\n\r\n包括源码、依赖、安卓本地签名及你放入的其他文件，请先备份。\r\n手机上的应用不会卸载。\r\n\r\n确定卸载？",
                "卸载红果助手", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            // 从目录外运行系统 PowerShell，等本程序退出后清理自身；路径按字面量传入。
            string script = @"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$root = '" + root.Replace("'", "''") + @"'
function Assert-SafeTree([string]$path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '目录包含链接或联接，请先移走它们再卸载。' }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $path -Force) { Assert-SafeTree $child.FullName }
    }
}
try {
    Wait-Process -Id " + Process.GetCurrentProcess().Id + @" -ErrorAction SilentlyContinue
    if (!(Test-Path -LiteralPath (Join-Path $root 'bin\HongguoRemote.exe') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $root 'build.ps1') -PathType Leaf)) { throw '目录校验失败，已停止卸载。' }
    Assert-SafeTree $root
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $link = Join-Path $desktop '红果遥控端.lnk'
    if (Test-Path -LiteralPath $link) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($link)
        $matches = [String]::Equals($shortcut.TargetPath, (Join-Path $root 'bin\HongguoRemote.exe'), [StringComparison]::OrdinalIgnoreCase)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        if ($matches) { Remove-Item -LiteralPath $link -Force }
    }
    Remove-Item -LiteralPath $root -Recurse -Force
    [void][Windows.Forms.MessageBox]::Show('红果助手已卸载。', '卸载完成')
} catch {
    [void][Windows.Forms.MessageBox]::Show(('卸载未完成：' + $_.Exception.Message + ""`r`n请检查目录中的剩余文件："" + $root), '卸载失败')
    exit 1
}
";
            var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WorkingDirectory = Path.GetTempPath();
            Process.Start(info).Dispose();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "无法卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void ValidateDirectory(string root)
    {
        string[] protectedPaths = { Path.GetPathRoot(root), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath() };
        foreach (string path in protectedPaths)
            if (!String.IsNullOrEmpty(path) && String.Equals(root, Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("卸载程序必须放在独立的软件文件夹中。");
        for (DirectoryInfo directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("软件路径包含链接或联接，请从真实目录卸载。");
        if (!File.Exists(Path.Combine(root, "bin", "HongguoRemote.exe")) || !File.Exists(Path.Combine(root, "build.ps1")))
            throw new InvalidOperationException("未找到红果助手程序，卸载程序必须放在项目根目录。");
    }
}
