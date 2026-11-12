@echo off
chcp 65001 >nul
set "ROOT=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = Get-Content -LiteralPath '%~f0' -Raw; $i = $s.IndexOf('#PS' + 'START'); Invoke-Expression $s.Substring($i)"
echo.
pause
exit /b

#PSSTART
$root = $env:ROOT.TrimEnd('\')
$out  = Join-Path $root 'AllCode.txt'

if (Test-Path -LiteralPath $out) {
    Remove-Item -LiteralPath $out -Force
    Write-Host 'Regi AllCode.txt torolve.'
}

$files = Get-ChildItem -Path $root -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.vs)\\' } |
    Sort-Object FullName

$sb = New-Object System.Text.StringBuilder
foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1)
    $content = [string](Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8)
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("===== $rel =====")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine($content)
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($out, $sb.ToString(), $utf8)

$kb = [math]::Round((Get-Item -LiteralPath $out).Length / 1KB, 1)
Write-Host ("Kesz: {0} fajl -> AllCode.txt ({1} KB)" -f $files.Count, $kb)