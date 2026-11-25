$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $PSScriptRoot
$buildTools = Join-Path $root 'tools\android-sdk\android-15'
$platform = '..\tools\android-sdk\android-35\android.jar'
if (!(Test-Path $platform)) { throw 'Missing tools/android-sdk/android-35/android.jar' }
if (!(Test-Path "$buildTools\aapt2.exe")) { throw 'Missing Android Build Tools 35 (android-15 directory).' }
New-Item -ItemType Directory -Force -Path build,build/gen,build/classes,build/dex,../bin | Out-Null
function Check-Exit { if ($LASTEXITCODE -ne 0) { throw 'Android build command failed.' } }
& "$buildTools\aapt2.exe" compile --dir res -o build/resources.zip
Check-Exit
& "$buildTools\aapt2.exe" link -o build/resources.apk -I $platform --manifest AndroidManifest.xml --java build/gen --min-sdk-version 29 --target-sdk-version 35 build/resources.zip
Check-Exit
$sources = @(Get-ChildItem src,build/gen -Recurse -Filter '*.java' | ForEach-Object FullName)
& javac -encoding UTF-8 -source 8 -target 8 -classpath $platform -d build/classes @sources
Check-Exit
& jar cf build/classes.jar -C build/classes .
Check-Exit
& java -cp "$buildTools\lib\d8.jar" com.android.tools.r8.D8 --lib $platform --min-api 29 --output build/dex build/classes.jar
Check-Exit
Copy-Item build/resources.apk build/unsigned.apk -Force
& jar uf build/unsigned.apk -C build/dex classes.dex
Check-Exit
& "$buildTools\zipalign.exe" -f 4 build/unsigned.apk build/aligned.apk
Check-Exit
$key = Join-Path $root 'tools\companion-local.p12'
if (!(Test-Path $key)) {
 & keytool -genkeypair -keystore $key -storetype PKCS12 -storepass android -keypass android -alias companion -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=Hongguo Local Companion'
 Check-Exit
}
$output = Join-Path $root 'bin\HongguoCompanion.apk'
& java -jar "$buildTools\lib\apksigner.jar" sign --ks $key --ks-pass pass:android --key-pass pass:android --out $output build/aligned.apk
Check-Exit
& java -jar "$buildTools\lib\apksigner.jar" verify --verbose $output
Check-Exit
Write-Host "Built $output"
