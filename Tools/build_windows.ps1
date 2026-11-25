# Compila sia l'APK del telefono (Builds\CardboardXR.apk) tramite Unity in modalità batch (senza interfaccia grafica)
# sia l'APK del Pixel Watch (WearApp\app\build\outputs\apk\debug\app-debug.apk) tramite Gradle,
# firmandoli con la stessa chiave (~\.android\debug.keystore) così la Wearable Data Layer API comunica subito.
param(
    [string]$UnityPath = $env:UNITY
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

if (-not $UnityPath) {
    $candidates = @(
        "C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Unity.exe",
        "C:\Program Files\Unity\Hub\Editor\*\Editor\Unity.exe"
    )
    foreach ($pattern in $candidates) {
        $found = Get-Item $pattern -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) {
            $UnityPath = $found.FullName
            break
        }
    }
}

Write-Host "== 1/2 Build APK Pixel Watch (WearApp)" -ForegroundColor Cyan
Push-Location "$RepoRoot\WearApp"
try {
    if (-not $env:JAVA_HOME) {
        $jdkCandidates = @(
            "C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK",
            "$env:USERPROFILE\.jdks\*",
            "C:\Program Files\Android\Android Studio\jbr",
            "C:\Program Files\Android\Android Studio1\jbr"
        )
        foreach ($pattern in $jdkCandidates) {
            $jdk = Get-Item $pattern -ErrorAction SilentlyContinue | Where-Object { Test-Path "$($_.FullName)\bin\java.exe" } | Select-Object -First 1
            if ($jdk) {
                $env:JAVA_HOME = $jdk.FullName
                break
            }
        }
    }
    .\gradlew.bat assembleDebug
    if ($LASTEXITCODE -ne 0) { throw "Build Gradle WearApp fallita" }
} finally {
    Pop-Location
}
$WatchApk = "$RepoRoot\WearApp\app\build\outputs\apk\debug\app-debug.apk"
Write-Host "APK Pixel Watch pronto: $WatchApk" -ForegroundColor Green

Write-Host "`n== 2/2 Build APK Telefono (CardboardXR)" -ForegroundColor Cyan
if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
    Write-Host "ERRORE: Unity 6000.6.8f1 (Unity.exe) non trovato sul PC." -ForegroundColor Red
    Write-Host "1. Installa Unity Hub da https://unity.com/download e accedi col tuo account Unity (per attivare la licenza gratuita Personal)." -ForegroundColor Yellow
    Write-Host "2. Installa Unity 6000.6.8f1 selezionando il modulo 'Android Build Support' (link diretto: unityhub://6000.6.8f1/237b6593792b )." -ForegroundColor Yellow
    Write-Host "3. Una volta installato, rilancia semplicemente: .\Tools\build_windows.ps1" -ForegroundColor Yellow
    exit 1
}

# Usa lo stesso debug.keystore di Android Studio/Gradle per firmare l'APK Unity
$DebugKeystore = Join-Path $env:USERPROFILE ".android\debug.keystore"
if (Test-Path $DebugKeystore) {
    $env:CARDBOARDXR_KEYSTORE = $DebugKeystore
    $env:CARDBOARDXR_KEYSTORE_PASS = "android"
    $env:CARDBOARDXR_KEY_ALIAS = "androiddebugkey"
}
if (-not $env:CARDBOARDXR_VERSION) {
    $env:CARDBOARDXR_VERSION = "2.0.30"
}

$BuildLog = Join-Path $RepoRoot "Builds\build_android.log"
New-Item -ItemType Directory -Force -Path (Join-Path $RepoRoot "Builds") | Out-Null
Write-Host "Avvio Unity in batchmode ($UnityPath)..."
$proc = Start-Process -FilePath $UnityPath -ArgumentList @(
    "-batchmode",
    "-nographics",
    "-projectPath", "`"$RepoRoot`"",
    "-buildTarget", "Android",
    "-executeMethod", "ProjectBootstrap.StereoBuilder.BuildAndroid",
    "-logFile", "`"$BuildLog`""
) -Wait -PassThru -NoNewWindow

if ($proc.ExitCode -ne 0 -or -not (Test-Path "$RepoRoot\Builds\CardboardXR.apk")) {
    Write-Host "Build Unity fallita. Ultimi errori dal log ($BuildLog):" -ForegroundColor Red
    if (Test-Path $BuildLog) {
        Select-String -Path $BuildLog -Pattern "\[Build\]|error CS|BuildFailedException" | Select-Object -Last 25
    }
    exit 1
}

Write-Host "APK Telefono pronto: $RepoRoot\Builds\CardboardXR.apk" -ForegroundColor Green
