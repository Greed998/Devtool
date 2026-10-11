<#
    make-cert.ps1 — önaláírt kódaláíró tanúsítvány készítése

    MIRE JÓ ÉS MIRE NEM
    -------------------
    JÓ:   olyan gépekre, ahol a tanúsítványodat megbízhatóként telepíted
          (saját gépek, ügyfélgépek, tartomány GPO-val vagy Intune-nal).
          Ilyenkor eltűnik a SmartScreen figyelmeztetés, és látszik a neved.
          Jó arra is, hogy kipróbáld az aláírási folyamatot, mielőtt fizetős
          tanúsítványt veszel.

    NEM JÓ: nyilvános letöltéshez. Egy ismeretlen gépen az önaláírt tanúsítvány
          NEM segít – sőt, erősebb blokkot vált ki, mint az aláírás nélküli fájl,
          mert a Windows nem ismeri a kiállítót. Nyilvános terjesztéshez CA-tól
          vásárolt tanúsítvány kell.
#>

[CmdletBinding()]
param(
    [string]$Publisher = "Poltoli Informatika",
    [string]$Organization = "Pusztai Csaba e.v.",
    [string]$Country = "HU",
    [int]$Years = 5,
    [string]$OutDir = "$PSScriptRoot\cert"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "  Önaláírt kódaláíró tanúsítvány készítése" -ForegroundColor Cyan
Write-Host "  ----------------------------------------" -ForegroundColor DarkGray
Write-Host ""

# A subject nevének SZÓ SZERINT egyeznie kell a DevToolbox.csproj <Company> értékével,
# különben a UAC ablak és a fájl tulajdonságai eltérő kiadót mutatnak.
$subject = "CN=$Publisher, O=$Organization, C=$Country"
Write-Host "  Kiadó neve : $Publisher"
Write-Host "  Subject    : $subject"
Write-Host "  Érvényes   : $Years évig"
Write-Host ""

# --- 1. tanúsítvány létrehozása -------------------------------------------
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $subject `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears($Years) `
    -FriendlyName "$Publisher – kódaláírás"

Write-Host "  Létrehozva. Ujjlenyomat:" -ForegroundColor Green
Write-Host "    $($cert.Thumbprint)" -ForegroundColor White
Write-Host ""

# --- 2. exportálás ---------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$pfxPath = Join-Path $OutDir "kodalairas.pfx"
$cerPath = Join-Path $OutDir "kodalairas.cer"

Write-Host "  Adj meg egy jelszót a .pfx fájlhoz (a beírás nem látszik):"
$pw = Read-Host -AsSecureString
if ($pw.Length -eq 0) { throw "Üres jelszóval nem exportálható a privát kulcs." }

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pw -Force | Out-Null
Export-Certificate  -Cert $cert -FilePath $cerPath -Force | Out-Null

# a sign.cmd innen olvassa ki az ujjlenyomatot
$cert.Thumbprint | Set-Content -Path (Join-Path $OutDir "thumbprint.txt") -Encoding ascii

Write-Host ""
Write-Host "  Elkészült:" -ForegroundColor Green
Write-Host "    $pfxPath" -ForegroundColor White
Write-Host "      A PRIVÁT KULCS. Ezt tartsd biztonságban, ne töltsd fel sehova," -ForegroundColor DarkYellow
Write-Host "      és ne tedd verziókövetésbe." -ForegroundColor DarkYellow
Write-Host "    $cerPath" -ForegroundColor White
Write-Host "      A nyilvános rész. EZT kell telepíteni azokra a gépekre," -ForegroundColor DarkGray
Write-Host "      ahol futtatni fogod a programot (lásd install-cert.cmd)." -ForegroundColor DarkGray
Write-Host ""

# --- 3. helyi telepítés (opcionális) --------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$answer = Read-Host "  Telepítsem most megbízhatóként ERRE a gépre? (i/n)"
if ($answer -match '^(i|I|y|Y)') {
    if (-not $isAdmin) {
        Write-Host "  Ehhez rendszergazdai jog kell. Indítsd újra a szkriptet emelt joggal," -ForegroundColor Yellow
        Write-Host "  vagy futtasd külön az install-cert.cmd fájlt." -ForegroundColor Yellow
    }
    else {
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null
        Write-Host "  Telepítve a Megbízható legfelső szintű hitelesítésszolgáltatók és a" -ForegroundColor Green
        Write-Host "  Megbízható kiadók tárolóba." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "  Következő lépés:" -ForegroundColor Cyan
Write-Host "    1. sign.cmd -> MODE=SELFSIGNED, majd futtasd"
Write-Host "    2. Ügyfélgépeken: install-cert.cmd (rendszergazdaként)"
Write-Host ""
Write-Host "  Ne feledd: nyilvános letöltéshez ez NEM elég – ott CA-tól vásárolt" -ForegroundColor DarkYellow
Write-Host "  tanúsítvány kell. Ez a megoldás a kezelt gépekre való." -ForegroundColor DarkYellow
Write-Host ""
