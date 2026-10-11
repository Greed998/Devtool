using System.Text;

namespace DevToolbox;

public enum FieldKind { Text, Number, Choice, Bool }

public sealed record BuilderField(
    string Key,
    string Label,
    string Default,
    string Hint = "",
    FieldKind Kind = FieldKind.Text,
    string[]? Options = null);

public sealed record BuilderTemplate(
    string Name,
    string Category,
    string Description,
    ScriptKind Kind,
    bool RunAsAdmin,
    BuilderField[] Fields,
    string Body,
    string Warning = "");

/// <summary>
/// Parameterezett szkriptsablonok. A behelyettesites {{Kulcs}} jelolokkel tortenik,
/// igy a sablon szovegeben a PowerShell $valtozok erintetlenul maradnak.
///
/// FONTOS ELV: jelszo SOHA nem kerul a generalt fajlba - a szkript futaskor kerdezi be.
/// Igy a konyvtarban tarolt szkript kiszivargasa nem ad hozzaferest semmihez.
/// </summary>
public static class ScriptBuilder
{
    public static string Fill(string body, IDictionary<string, string> values)
    {
        var sb = new StringBuilder(body);
        foreach (var (k, v) in values) sb.Replace("{{" + k + "}}", v);
        return sb.ToString();
    }

    public static readonly BuilderTemplate[] Templates =
    {
        // ================================================================ AD – új erdő
        new("Active Directory – új erdő", "Kiszolgáló",
            "Az első tartományvezérlő felhúzása egy új erdőben. Ellenőrzi az előfeltételeket " +
            "(fix IP, gépnév, munkacsoport), telepíti a szerepkört, majd újraindít.",
            ScriptKind.PowerShell, true,
            new BuilderField[]
            {
                new("DomainName", "Tartomány (FQDN)", "ceg.local",
                    "Belső tartománynév. Ne .local legyen, ha nyilvános tanúsítvány is kell rá."),
                new("NetBios", "NetBIOS név", "CEG", "Legfeljebb 15 karakter, nagybetűkkel."),
                new("ForestMode", "Erdő- és tartományszint", "WinThreshold",
                    "WinThreshold = 2016-os szint, a legtöbb környezetben ez a jó.",
                    FieldKind.Choice, new[] { "Win2012R2", "WinThreshold", "2025" }),
                new("DbPath", "Adatbázis útvonala", @"C:\Windows\NTDS"),
                new("LogPath", "Naplók útvonala", @"C:\Windows\NTDS"),
                new("SysvolPath", "SYSVOL útvonala", @"C:\Windows\SYSVOL"),
            },
            """
            #Requires -RunAsAdministrator
            <#
                Active Directory erdő telepítése
                Tartomány : {{DomainName}}   ({{NetBios}})
                Szint     : {{ForestMode}}

                A gép a végén ÚJRAINDUL. A DSRM jelszót a szkript futáskor kéri be –
                szándékosan nincs a fájlba írva.
            #>

            $ErrorActionPreference = 'Stop'

            $DomainName  = '{{DomainName}}'
            $NetBiosName = '{{NetBios}}'
            $ForestMode  = '{{ForestMode}}'

            Write-Host '=== Előfeltételek ellenőrzése ===' -ForegroundColor Cyan

            # --- 1. Már tartományvezérlő?
            $cs = Get-CimInstance Win32_ComputerSystem
            if ($cs.DomainRole -ge 4) { throw 'Ez a gép már tartományvezérlő.' }
            if ($cs.PartOfDomain)      { throw 'A gép már tartomány tagja. Új erdőhöz munkacsoportban kell lennie.' }

            # --- 2. Fix IP-cím
            $cfg = Get-NetIPConfiguration | Where-Object { $_.IPv4Address } | Select-Object -First 1
            if (-not $cfg) { throw 'Nincs IPv4 cím a gépen.' }

            $dhcp = (Get-NetIPInterface -InterfaceIndex $cfg.InterfaceIndex -AddressFamily IPv4).Dhcp
            Write-Host ("  IP-cím:  {0}" -f $cfg.IPv4Address.IPAddress)
            if ($dhcp -eq 'Enabled') {
                Write-Host '  FIGYELEM: a kártya DHCP-ről kap címet.' -ForegroundColor Yellow
                Write-Host '  Tartományvezérlőnek FIX IP kell, különben a DNS elromlik.' -ForegroundColor Yellow
                if ((Read-Host '  Mégis folytatod? (i/n)') -notmatch '^[iIyY]') { return }
            }

            # --- 3. Gépnév
            Write-Host ("  Gépnév:  {0}" -f $env:COMPUTERNAME)
            if ($env:COMPUTERNAME -match '^(WIN-|DESKTOP-)') {
                Write-Host '  A gépnév még a gyári véletlen név. Telepítés UTÁN már nem nevezhető át' -ForegroundColor Yellow
                Write-Host '  egyszerűen a tartományvezérlő. Érdemes most átnevezni.' -ForegroundColor Yellow
                if ((Read-Host '  Mégis folytatod? (i/n)') -notmatch '^[iIyY]') { return }
            }

            # --- 4. Szabad hely
            $free = (Get-PSDrive C).Free / 1GB
            Write-Host ("  Szabad hely a C:-n: {0:N1} GB" -f $free)
            if ($free -lt 10) { throw 'Kevesebb mint 10 GB szabad hely. Előbb szabadíts fel.' }

            Write-Host ''
            Write-Host '=== Szerepkör telepítése ===' -ForegroundColor Cyan
            Install-WindowsFeature -Name AD-Domain-Services -IncludeManagementTools

            Import-Module ADDSDeployment

            Write-Host ''
            Write-Host 'Add meg a DSRM (címtár-helyreállítási) jelszót.' -ForegroundColor Cyan
            Write-Host 'Ezt címtár-helyreállításkor fogod használni – jegyezd fel biztonságos helyre.' -ForegroundColor DarkGray
            $dsrm = Read-Host 'DSRM jelszó' -AsSecureString

            Write-Host ''
            Write-Host '=== Erdő létrehozása – a gép ezután újraindul ===' -ForegroundColor Cyan

            Install-ADDSForest `
                -DomainName $DomainName `
                -DomainNetbiosName $NetBiosName `
                -ForestMode $ForestMode `
                -DomainMode $ForestMode `
                -InstallDns:$true `
                -DatabasePath '{{DbPath}}' `
                -LogPath '{{LogPath}}' `
                -SysvolPath '{{SysvolPath}}' `
                -SafeModeAdministratorPassword $dsrm `
                -NoRebootOnCompletion:$false `
                -Force:$true

            # --- Újraindítás UTÁN ezekkel ellenőrizd:
            #     Get-ADDomain
            #     Get-ADForest
            #     dcdiag /v
            #     Get-DnsServerZone
            """,
            "A gép a szkript végén automatikusan újraindul. Éles szerveren csak karbantartási " +
            "ablakban futtasd."),

        // ================================================================ AD – további DC
        new("Active Directory – további tartományvezérlő", "Kiszolgáló",
            "Meglévő tartományhoz ad hozzá egy újabb tartományvezérlőt (redundancia).",
            ScriptKind.PowerShell, true,
            new BuilderField[]
            {
                new("DomainName", "Meglévő tartomány (FQDN)", "ceg.local"),
                new("SiteName", "AD hely (site)", "Default-First-Site-Name"),
                new("InstallDns", "DNS is települjön", "igen", "", FieldKind.Choice, new[] { "igen", "nem" }),
                new("GlobalCatalog", "Globális katalógus legyen", "igen", "", FieldKind.Choice, new[] { "igen", "nem" }),
            },
            """
            #Requires -RunAsAdministrator
            <#
                További tartományvezérlő hozzáadása
                Tartomány: {{DomainName}}    Hely: {{SiteName}}
                A gép a végén ÚJRAINDUL.
            #>

            $ErrorActionPreference = 'Stop'
            $DomainName = '{{DomainName}}'

            Write-Host '=== Előfeltételek ===' -ForegroundColor Cyan

            $cs = Get-CimInstance Win32_ComputerSystem
            if ($cs.DomainRole -ge 4) { throw 'Ez a gép már tartományvezérlő.' }

            # A DNS-nek a meglévő tartományvezérlőre kell mutatnia, különben nem talál rá.
            $dns = (Get-DnsClientServerAddress -AddressFamily IPv4 |
                    Where-Object ServerAddresses).ServerAddresses
            Write-Host ("  Beállított DNS: {0}" -f ($dns -join ', '))

            try {
                $dc = Resolve-DnsName -Name "_ldap._tcp.dc._msdcs.$DomainName" -Type SRV -ErrorAction Stop
                Write-Host ("  Megtalált tartományvezérlő: {0}" -f ($dc.NameTarget -join ', ')) -ForegroundColor Green
            } catch {
                throw "A(z) $DomainName tartomány nem található a DNS-ben. Állítsd át a DNS-t a meglévő DC-re."
            }

            Write-Host ''
            Write-Host '=== Szerepkör telepítése ===' -ForegroundColor Cyan
            Install-WindowsFeature -Name AD-Domain-Services -IncludeManagementTools
            Import-Module ADDSDeployment

            Write-Host ''
            Write-Host 'Tartományi rendszergazda hitelesítő adatai:' -ForegroundColor Cyan
            $cred = Get-Credential -Message "Tartományi rendszergazda ($DomainName)"

            Write-Host 'DSRM (címtár-helyreállítási) jelszó ehhez a géphez:' -ForegroundColor Cyan
            $dsrm = Read-Host 'DSRM jelszó' -AsSecureString

            Install-ADDSDomainController `
                -DomainName $DomainName `
                -SiteName '{{SiteName}}' `
                -InstallDns:${{InstallDnsBool}} `
                -NoGlobalCatalog:${{NoGcBool}} `
                -Credential $cred `
                -SafeModeAdministratorPassword $dsrm `
                -NoRebootOnCompletion:$false `
                -Force:$true

            # --- Újraindítás után:  repadmin /replsummary   és   dcdiag /v
            """,
            "A gép a szkript végén újraindul."),

        // ================================================================ DHCP
        new("DHCP kiszolgáló és hatókör", "Kiszolgáló",
            "Telepíti a DHCP szerepkört, engedélyezi az AD-ben, létrehoz egy hatókört " +
            "átjáróval, DNS-sel és kizárási tartománnyal.",
            ScriptKind.PowerShell, true,
            new BuilderField[]
            {
                new("ScopeName", "Hatókör neve", "Iroda"),
                new("Network", "Hálózat azonosítója", "192.168.1.0",
                    "A hálózat első címe, nem a szerveré."),
                new("Mask", "Alhálózati maszk", "255.255.255.0"),
                new("RangeStart", "Kiosztás kezdete", "192.168.1.100"),
                new("RangeEnd", "Kiosztás vége", "192.168.1.200"),
                new("Router", "Átjáró (router)", "192.168.1.1"),
                new("DnsServers", "DNS kiszolgáló(k)", "192.168.1.10",
                    "Több cím vesszővel elválasztva."),
                new("DnsDomain", "DNS tartomány", "ceg.local"),
                new("LeaseHours", "Bérlet hossza (óra)", "8", "", FieldKind.Number),
                new("ExclStart", "Kizárás kezdete", "192.168.1.100",
                    "Hagyd üresen, ha nincs kizárás."),
                new("ExclEnd", "Kizárás vége", "192.168.1.110"),
            },
            """
            #Requires -RunAsAdministrator
            <#
                DHCP kiszolgáló telepítése és hatókör létrehozása
                Hatókör: {{ScopeName}}  –  {{RangeStart}} … {{RangeEnd}}  ({{Mask}})
            #>

            $ErrorActionPreference = 'Stop'

            $ScopeName  = '{{ScopeName}}'
            $Network    = '{{Network}}'
            $Mask       = '{{Mask}}'
            $RangeStart = '{{RangeStart}}'
            $RangeEnd   = '{{RangeEnd}}'
            $Router     = '{{Router}}'
            $DnsServers = '{{DnsServers}}' -split ',' | ForEach-Object { $_.Trim() }
            $DnsDomain  = '{{DnsDomain}}'
            $LeaseHours = {{LeaseHours}}

            Write-Host '=== Szerepkör telepítése ===' -ForegroundColor Cyan
            $f = Get-WindowsFeature -Name DHCP
            if (-not $f.Installed) {
                Install-WindowsFeature -Name DHCP -IncludeManagementTools
            } else {
                Write-Host '  Már telepítve.' -ForegroundColor DarkGray
            }

            # A telepítés után létre kell hozni a DHCP biztonsági csoportokat.
            netsh dhcp add securitygroups | Out-Null
            Restart-Service dhcpserver

            Write-Host ''
            Write-Host '=== Engedélyezés az Active Directoryban ===' -ForegroundColor Cyan
            $cs = Get-CimInstance Win32_ComputerSystem
            if ($cs.PartOfDomain) {
                # Tartományban a DHCP csak engedélyezés (authorize) után oszt címet.
                $fqdn = "$env:COMPUTERNAME.$($cs.Domain)"
                $ip = (Get-NetIPConfiguration | Where-Object { $_.IPv4Address } |
                       Select-Object -First 1).IPv4Address.IPAddress
                try {
                    Add-DhcpServerInDC -DnsName $fqdn -IPAddress $ip
                    Write-Host "  Engedélyezve: $fqdn ($ip)" -ForegroundColor Green
                } catch {
                    Write-Host "  Már engedélyezve, vagy nincs jogosultság: $_" -ForegroundColor Yellow
                }
            } else {
                Write-Host '  A gép nem tartománytag – AD-engedélyezés nem szükséges.' -ForegroundColor DarkGray
            }

            # A Server Manager „konfiguráció befejezése” figyelmeztetésének eltüntetése.
            $rolePath = 'HKLM:\SOFTWARE\Microsoft\ServerManager\Roles\12'
            if (Test-Path $rolePath) { Set-ItemProperty -Path $rolePath -Name ConfigurationState -Value 2 }

            Write-Host ''
            Write-Host '=== Hatókör létrehozása ===' -ForegroundColor Cyan
            if (Get-DhcpServerv4Scope -ScopeId $Network -ErrorAction SilentlyContinue) {
                Write-Host "  A(z) $Network hatókör már létezik – kihagyva." -ForegroundColor Yellow
            } else {
                Add-DhcpServerv4Scope -Name $ScopeName `
                    -StartRange $RangeStart -EndRange $RangeEnd `
                    -SubnetMask $Mask -State Active `
                    -LeaseDuration (New-TimeSpan -Hours $LeaseHours)
                Write-Host "  Létrehozva: $ScopeName" -ForegroundColor Green
            }

            Set-DhcpServerv4OptionValue -ScopeId $Network `
                -Router $Router -DnsServer $DnsServers -DnsDomain $DnsDomain

            {{ExclusionBlock}}

            # A kiosztott gépek DNS-rekordjának karbantartása
            Set-DhcpServerv4DnsSetting -ComputerName $env:COMPUTERNAME `
                -DynamicUpdates Always -DeleteDnsRROnLeaseExpiry $true

            Write-Host ''
            Write-Host '=== Eredmény ===' -ForegroundColor Cyan
            Get-DhcpServerv4Scope | Format-Table ScopeId, Name, StartRange, EndRange, State, LeaseDuration -AutoSize
            Get-DhcpServerv4OptionValue -ScopeId $Network | Format-Table OptionId, Name, Value -AutoSize
            """,
            "Egy hálózaton csak EGY DHCP kiszolgáló osszon címet. Ha van router-beli DHCP, " +
            "azt előbb kapcsold ki, különben ütköző címkiosztás lesz."),

        // ================================================================ DNS zóna
        new("DNS – belső zóna és rekordok", "Kiszolgáló",
            "Belső DNS zóna létrehozása, továbbítók beállítása és reverse zóna.",
            ScriptKind.PowerShell, true,
            new BuilderField[]
            {
                new("ZoneName", "Zóna neve", "ceg.local"),
                new("Network", "Hálózat a reverse zónához", "192.168.1.0/24"),
                new("Forwarder1", "Továbbító 1", "1.1.1.1"),
                new("Forwarder2", "Továbbító 2", "8.8.8.8"),
            },
            """
            #Requires -RunAsAdministrator
            <# Belső DNS zóna: {{ZoneName}} #>

            $ErrorActionPreference = 'Stop'
            Import-Module DnsServer

            Write-Host '=== Forward zóna ===' -ForegroundColor Cyan
            if (Get-DnsServerZone -Name '{{ZoneName}}' -ErrorAction SilentlyContinue) {
                Write-Host '  Már létezik.' -ForegroundColor DarkGray
            } else {
                Add-DnsServerPrimaryZone -Name '{{ZoneName}}' -ReplicationScope Domain -DynamicUpdate Secure
                Write-Host '  Létrehozva.' -ForegroundColor Green
            }

            Write-Host '=== Reverse zóna ===' -ForegroundColor Cyan
            try {
                Add-DnsServerPrimaryZone -NetworkId '{{Network}}' -ReplicationScope Domain -DynamicUpdate Secure
                Write-Host '  Létrehozva.' -ForegroundColor Green
            } catch {
                Write-Host "  Kihagyva: $_" -ForegroundColor Yellow
            }

            Write-Host '=== Továbbítók ===' -ForegroundColor Cyan
            Set-DnsServerForwarder -IPAddress '{{Forwarder1}}', '{{Forwarder2}}' -UseRootHint $false
            Get-DnsServerForwarder | Format-List

            Write-Host '=== Ellenőrzés ===' -ForegroundColor Cyan
            Get-DnsServerZone | Format-Table ZoneName, ZoneType, IsDsIntegrated, DynamicUpdate -AutoSize
            """),

        // ================================================================ fájlmegosztás
        new("Fájlmegosztás jogosultságokkal", "Kiszolgáló",
            "Mappa és SMB megosztás létrehozása, NTFS jogosultságokkal és öröklés-megszakítással.",
            ScriptKind.PowerShell, true,
            new BuilderField[]
            {
                new("Path", "Mappa útvonala", @"D:\Megosztas\Penzugy"),
                new("ShareName", "Megosztás neve", "Penzugy"),
                new("FullGroup", "Teljes jogú csoport", "CEG\\Penzugy-Admin"),
                new("ChangeGroup", "Írási jogú csoport", "CEG\\Penzugy-Felhasznalo"),
                new("ReadGroup", "Olvasási jogú csoport", "CEG\\Penzugy-Olvaso",
                    "Hagyd üresen, ha nem kell."),
            },
            """
            #Requires -RunAsAdministrator
            <# Megosztás: {{ShareName}}  ->  {{Path}} #>

            $ErrorActionPreference = 'Stop'
            $Path = '{{Path}}'
            $Share = '{{ShareName}}'

            if (-not (Test-Path $Path)) {
                New-Item -ItemType Directory -Path $Path -Force | Out-Null
                Write-Host "Mappa létrehozva: $Path" -ForegroundColor Green
            }

            # --- SMB megosztás. A megosztási jog Everyone/Full, a tényleges korlátozás NTFS-en
            #     történik. Ez a bevált gyakorlat: két helyen karbantartani a jogokat hibaforrás.
            if (Get-SmbShare -Name $Share -ErrorAction SilentlyContinue) {
                Write-Host 'A megosztás már létezik.' -ForegroundColor Yellow
            } else {
                New-SmbShare -Name $Share -Path $Path -FullAccess 'Everyone' -FolderEnumerationMode AccessBased | Out-Null
                Write-Host "Megosztás létrehozva: \\\\$env:COMPUTERNAME\\$Share" -ForegroundColor Green
            }

            # --- NTFS jogosultságok
            $acl = Get-Acl $Path
            $acl.SetAccessRuleProtection($true, $false)      # öröklés megszakítása
            $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

            function Add-Rule($who, $rights) {
                $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                    $who, $rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                $acl.AddAccessRule($rule)
            }

            Add-Rule 'SYSTEM' 'FullControl'
            Add-Rule 'BUILTIN\Administrators' 'FullControl'
            Add-Rule '{{FullGroup}}' 'FullControl'
            Add-Rule '{{ChangeGroup}}' 'Modify'
            {{ReadRule}}

            Set-Acl -Path $Path -AclObject $acl

            Write-Host ''
            Get-Acl $Path | Select-Object -ExpandProperty Access |
              Select-Object IdentityReference, FileSystemRights, AccessControlType |
              Format-Table -AutoSize
            """,
            "A szkript megszakítja az öröklést és TÖRLI a meglévő jogosultságokat a mappán. " +
            "Meglévő, éles mappán előbb mentsd ki: Get-Acl.'"),

        // ================================================================ AD felhasználók CSV-ből
        new("AD felhasználók CSV-ből", "Kiszolgáló",
            "Tömeges felhasználó-létrehozás CSV alapján. A szkript mintafájlt is generál.",
            ScriptKind.PowerShell, true,
            new BuilderField[]
            {
                new("CsvPath", "CSV fájl", @"C:\Temp\felhasznalok.csv"),
                new("OuPath", "Cél OU", "OU=Felhasznalok,DC=ceg,DC=local"),
                new("Upn", "UPN utótag", "ceg.local"),
                new("MustChange", "Első belépéskor jelszócsere", "igen", "",
                    FieldKind.Choice, new[] { "igen", "nem" }),
            },
            """
            #Requires -RunAsAdministrator
            <#
                AD felhasználók tömeges létrehozása
                CSV oszlopok:  Vezeteknev;Keresztnev;Felhasznalonev;Beosztas;Osztaly
                Elválasztó: pontosvessző (az Excel magyar beállítással így menti)
            #>

            $ErrorActionPreference = 'Stop'
            Import-Module ActiveDirectory

            $CsvPath = '{{CsvPath}}'
            $OU      = '{{OuPath}}'
            $UpnSuffix = '{{Upn}}'

            # --- Mintafájl, ha még nincs
            if (-not (Test-Path $CsvPath)) {
                $sample = @'
            Vezeteknev;Keresztnev;Felhasznalonev;Beosztas;Osztaly
            Kovacs;Janos;kovacs.janos;Konyvelo;Penzugy
            Nagy;Eva;nagy.eva;Ertekesito;Kereskedelem
            '@
                New-Item -ItemType Directory -Force -Path (Split-Path $CsvPath) | Out-Null
                $sample | Set-Content -Path $CsvPath -Encoding UTF8
                Write-Host "Mintafájl létrehozva: $CsvPath" -ForegroundColor Yellow
                Write-Host 'Töltsd ki, majd futtasd újra a szkriptet.' -ForegroundColor Yellow
                return
            }

            Write-Host 'Kezdő jelszó az új fiókokhoz:' -ForegroundColor Cyan
            $pw = Read-Host 'Jelszó' -AsSecureString

            $rows = Import-Csv $CsvPath -Delimiter ';' -Encoding UTF8
            Write-Host ("{0} sor beolvasva." -f @($rows).Count)

            $created = 0; $skipped = 0
            foreach ($r in $rows) {
                $sam = $r.Felhasznalonev.Trim()
                if (-not $sam) { continue }

                if (Get-ADUser -Filter "SamAccountName -eq '$sam'" -ErrorAction SilentlyContinue) {
                    Write-Host "  kihagyva (létezik): $sam" -ForegroundColor DarkGray
                    $skipped++
                    continue
                }

                New-ADUser `
                    -Name "$($r.Vezeteknev) $($r.Keresztnev)" `
                    -GivenName $r.Keresztnev `
                    -Surname $r.Vezeteknev `
                    -SamAccountName $sam `
                    -UserPrincipalName "$sam@$UpnSuffix" `
                    -Title $r.Beosztas `
                    -Department $r.Osztaly `
                    -Path $OU `
                    -AccountPassword $pw `
                    -ChangePasswordAtLogon ${{MustChangeBool}} `
                    -Enabled $true

                Write-Host "  létrehozva: $sam" -ForegroundColor Green
                $created++
            }

            Write-Host ''
            Write-Host ("Kész: {0} létrehozva, {1} kihagyva." -f $created, $skipped) -ForegroundColor Cyan
            """),

        // ================================================================ Linux
        new("Linux szerver alapbeállítás", "Linux",
            "Friss Debian/Ubuntu szerver felkészítése: frissítés, felhasználó, SSH szigorítás, " +
            "tűzfal, automatikus biztonsági frissítések.",
            ScriptKind.Bash, false,
            new BuilderField[]
            {
                new("User", "Létrehozandó felhasználó", "admin"),
                new("SshPort", "SSH port", "22", "", FieldKind.Number),
                new("Hostname", "Gépnév", "srv01"),
                new("Timezone", "Időzóna", "Europe/Budapest"),
                new("AllowFrom", "SSH engedélyezve innen", "192.168.1.0/24",
                    "CIDR tartomány, vagy 'any' ha bárhonnan."),
            },
            """
            #!/usr/bin/env bash
            #
            # Debian/Ubuntu szerver alapbeállítás
            # Gépnév: {{Hostname}}   Felhasználó: {{User}}   SSH port: {{SshPort}}
            #
            # Futtatás:  sudo bash alapbeallitas.sh
            #
            set -euo pipefail

            USER_NAME="{{User}}"
            SSH_PORT="{{SshPort}}"
            HOSTNAME="{{Hostname}}"
            TIMEZONE="{{Timezone}}"
            ALLOW_FROM="{{AllowFrom}}"

            if [[ $EUID -ne 0 ]]; then
                echo "Ezt root-ként (sudo) kell futtatni." >&2
                exit 1
            fi

            echo "=== Gépnév és időzóna ==="
            hostnamectl set-hostname "$HOSTNAME"
            timedatectl set-timezone "$TIMEZONE"

            echo "=== Csomagok frissítése ==="
            export DEBIAN_FRONTEND=noninteractive
            apt-get update -qq
            apt-get upgrade -y -qq
            apt-get install -y -qq ufw fail2ban unattended-upgrades sudo curl htop

            echo "=== Felhasználó: $USER_NAME ==="
            if id "$USER_NAME" &>/dev/null; then
                echo "  már létezik"
            else
                adduser --disabled-password --gecos "" "$USER_NAME"
                usermod -aG sudo "$USER_NAME"
                echo "  létrehozva, sudo joggal"
                echo "  Állíts be jelszót:  passwd $USER_NAME"
                echo "  Vagy tölts fel kulcsot:  ssh-copy-id -p $SSH_PORT $USER_NAME@$HOSTNAME"
            fi

            echo "=== SSH szigorítás ==="
            SSHD=/etc/ssh/sshd_config.d/99-hardening.conf
            mkdir -p /etc/ssh/sshd_config.d
            cat > "$SSHD" <<EOF
            Port ${SSH_PORT}
            PermitRootLogin no
            PasswordAuthentication yes
            X11Forwarding no
            MaxAuthTries 3
            ClientAliveInterval 300
            EOF

            # FIGYELEM: a jelszavas belépés egyelőre BE van kapcsolva, hogy ki ne zárd magad.
            # Miután a kulcsos belépés működik, állítsd át:  PasswordAuthentication no

            echo "=== Tűzfal ==="
            ufw --force reset >/dev/null
            ufw default deny incoming
            ufw default allow outgoing
            if [[ "$ALLOW_FROM" == "any" ]]; then
                ufw allow "${SSH_PORT}/tcp"
            else
                ufw allow from "$ALLOW_FROM" to any port "$SSH_PORT" proto tcp
            fi
            ufw --force enable

            echo "=== Automatikus biztonsági frissítések ==="
            dpkg-reconfigure -f noninteractive unattended-upgrades

            systemctl restart ssh || systemctl restart sshd

            echo
            echo "=== Kész ==="
            echo "MIELŐTT KILÉPSZ: nyiss egy MÁSIK terminált és próbáld ki a belépést a"
            echo "$SSH_PORT porton. Ha nem megy, ebből a munkamenetből még tudsz javítani."
            ufw status verbose
            """,
            "SSH portot és tűzfalat állít. Távoli gépen mindig tartsd nyitva a jelenlegi " +
            "munkamenetet, amíg az új belépést ki nem próbáltad."),
    };

    /// <summary>
    /// A generalashoz szukseges szarmaztatott ertekek (PowerShell logikai valtozok,
    /// felteteles blokkok) eloallitasa a nyers mezoertekekbol.
    /// </summary>
    public static Dictionary<string, string> Derive(BuilderTemplate t, Dictionary<string, string> v)
    {
        var d = new Dictionary<string, string>(v);

        bool Yes(string key) =>
            d.TryGetValue(key, out var s) && s.Equals("igen", StringComparison.OrdinalIgnoreCase);

        d["InstallDnsBool"] = Yes("InstallDns") ? "true" : "false";
        d["NoGcBool"] = Yes("GlobalCatalog") ? "false" : "true";
        d["MustChangeBool"] = Yes("MustChange") ? "true" : "false";

        // DHCP kizárási tartomány csak akkor, ha ki van töltve
        var e1 = d.GetValueOrDefault("ExclStart", "").Trim();
        var e2 = d.GetValueOrDefault("ExclEnd", "").Trim();
        d["ExclusionBlock"] = e1.Length > 0 && e2.Length > 0
            ? $"Add-DhcpServerv4ExclusionRange -ScopeId $Network -StartRange '{e1}' -EndRange '{e2}'"
            : "# (nincs kizárási tartomány megadva)";

        // Fájlmegosztás olvasó csoportja
        var read = d.GetValueOrDefault("ReadGroup", "").Trim();
        d["ReadRule"] = read.Length > 0
            ? $"Add-Rule '{read}' 'ReadAndExecute'"
            : "# (nincs olvasási jogú csoport megadva)";

        return d;
    }

    public static string Generate(BuilderTemplate t, Dictionary<string, string> values) =>
        Fill(t.Body, Derive(t, values));
}
