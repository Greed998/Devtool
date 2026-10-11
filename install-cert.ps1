<#
    install-cert.ps1 — a kiadói tanúsítvány telepítése egy célgépre

    Ezt a NYILVÁNOS részt (.cer) kell telepíteni azokra a gépekre, ahol az
    általad aláírt programot futtatni fogod. A privát kulcsot (.pfx) SOHA
    ne vidd ki a saját gépedről.

    Rendszergazdai jog szükséges.
#>

[CmdletBinding()]
param(
    [string]$CerPath = "$PSScriptRoot\cert\kodalairas.cer",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "  Ehhez rendszergazdai jog kell." -ForegroundColor Red
    Write-Host "  Indítsd újra a parancssort rendszergazdaként." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $CerPath)) {
    Write-Host "  Nem található a tanúsítvány: $CerPath" -ForegroundColor Red
    Write-Host "  Készítsd el előbb a make-cert.cmd futtatásával." -ForegroundColor Red
    exit 1
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $CerPath
Write-Host ""
Write-Host "  Tanúsítvány : $($cert.Subject)"
Write-Host "  Ujjlenyomat : $($cert.Thumbprint)"
Write-Host "  Érvényes    : $($cert.NotBefore.ToString('yyyy.MM.dd')) – $($cert.NotAfter.ToString('yyyy.MM.dd'))"
Write-Host ""

$stores = @("Root", "TrustedPublisher")

if ($Remove) {
    foreach ($s in $stores) {
        Get-ChildItem "Cert:\LocalMachine\$s" |
            Where-Object Thumbprint -eq $cert.Thumbprint |
            Remove-Item -Force
        Write-Host "  Eltávolítva innen: LocalMachine\$s" -ForegroundColor Green
    }
    Write-Host ""
    exit 0
}

foreach ($s in $stores) {
    Import-Certificate -FilePath $CerPath -CertStoreLocation "Cert:\LocalMachine\$s" | Out-Null
    Write-Host "  Telepítve ide: LocalMachine\$s" -ForegroundColor Green
}

Write-Host ""
Write-Host "  Kész. Az ezzel a tanúsítvánnyal aláírt programok mostantól" -ForegroundColor Green
Write-Host "  megbízhatóként futnak ezen a gépen." -ForegroundColor Green
Write-Host ""
Write-Host "  Eltávolítás:  install-cert.cmd -Remove" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Sok gépre: tartományban csoportházirenddel" -ForegroundColor DarkGray
Write-Host "    Számítógép konfigurációja > Házirendek > Windows-beállítások >" -ForegroundColor DarkGray
Write-Host "    Biztonsági beállítások > Nyilvános kulcsokra vonatkozó házirendek" -ForegroundColor DarkGray
Write-Host "  Intune-nal: Eszközök > Konfigurációs profilok > Megbízható tanúsítvány" -ForegroundColor DarkGray
Write-Host ""
