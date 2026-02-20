Import-Module PSWriteColor
Clear-Host

$Year=(Get-Date).Year - 2000
$DoY=(((Get-Date).DayOfYear) * 2.7).ToString("000")
$Tm=((Get-Date).Hour * 41) + (Get-Date).Minute
$Ver="0.$Year.$DoY.$Tm"
Write-Color "Publishing version:  ", "$Ver" -Color White, Yellow

# dotnet publish PatchInstaller.csproj -c Release -r win-x64 --self-contained true /p:WarningLevel=0 /p:PublishSingleFile=true /p:Version="$Ver"

$process = Start-Process -FilePath "C:\Program Files\dotnet\dotnet.exe" `
    -ArgumentList "publish", "PatchInstaller.csproj", "-c Release -r win-x64 --self-contained true /p:WarningLevel=0 /p:PublishSingleFile=true /p:Version=`"$Ver`"" `
    -NoNewWindow `
    -Wait -PassThru
$exitCode = $process.ExitCode

#Write-Color "Exit code: " , $exitCode -Color White, Red
if ( !$exitCode )
{
    $Ver  | Out-File -FilePath version.txt
    Write-Color "NEW Published version is:  ", $Ver -Color White, Yellow
}
$exeVer = (Get-Item -Path "C:\Users\SyncthingServiceAcct\Syncthing\CapTG_OneDrive\Documents\VSCode Solutions Folder\PatchInstaller\bin\Release\net10.0\win-x64\publish\PatchInstaller.exe").VersionInfo.ProductVersion
write-Color "Executable version:        ", $exeVer -Color White, Yellow

Get-ChildItem "C:\Users\SyncthingServiceAcct\Syncthing\CapTG_OneDrive\Documents\VSCode Solutions Folder\PatchInstaller\bin\Release\net10.0\win-x64\publish\PatchInstaller.exe" | Select-Object Name, LastWriteTime, Length
