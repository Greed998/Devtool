# wireguard-nt a Fleetben

- Forrás: https://download.wireguard.com/wireguard-nt/wireguard-nt-1.1.zip (letöltve 2026-10-10, a zip SHA-256-ja:
  `DCEB30A9BC4BE48CCE0F74160FC88A585A2C2627366E8F846FC6658F9038DACE`)
- `amd64/wireguard.dll` – VÁLTOZATLANUL (WireGuard LLC aláírásával), SHA-256:
  `B1B85E072C45D81358BE29D94C599DC76652F912BE8C0F0A41E2D5D89A6461D3`
- `wireguard.h` – a hivatalos API-fejléc (GPL-2.0 OR MIT), `LICENSE.txt` – a bináris licence (Prebuilt Binaries License).

A licenc szerint a DLL csak változatlanul, a mellé tett programmal együtt adható tovább, ha az kizárólag a `wireguard.h`
felületén használja (Permitted API). A Fleet így tesz: a Fleet.exe beágyazva hozza, induláskor a lenyomat ellenőrzése után a
saját mappájába írja (`wireguard.dll` + `wireguard-LICENSE.txt`), és csak a `wireguard.h` függvényeit hívja. Frissítéskor a
DLL-t és a lenyomatot (`WgTunnel.DllSha256`) együtt kell cserélni.
