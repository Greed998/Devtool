namespace DevToolbox.Fleet.Server;

/// <summary>
/// A szuperadmin webfelülete: egyetlen önálló HTML-oldal, beágyazva a szerver exébe.
/// Nincs külső keretrendszer, npm vagy CDN – a teljes felület itt van, offline is működik.
/// A JSON API-t hívja (/web/api/*), amit a session-süti véd.
/// </summary>
public static class WebUi
{
    public static string LoginPage(string? error) => $$"""
<!DOCTYPE html>
<html lang="hu">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Fleet Server – bejelentkezés</title>
<style>{{Css}}</style>
</head>
<body class="login-body">
  <form class="login-card" method="post" action="/web/login">
    <div class="brand"><div class="brand-mark">◆</div><div class="brand-name">Fleet <span>Server</span></div></div>
    <h1>Bejelentkezés</h1>
    <p class="muted">Szuperadmin hozzáférés a flottakezeléshez.</p>
    {{(error is null ? "" : $"<div class=\"alert\">{error}</div>")}}
    <label>Felhasználónév</label>
    <input name="user" autocomplete="username" autofocus required>
    <label>Jelszó</label>
    <input name="password" type="password" autocomplete="current-password" required>
    <button type="submit">Belépés</button>
  </form>
</body>
</html>
""";

    public static string SetupPage(string? error, int webPort = DefaultWebPort, int agentPort = DefaultAgentPort) => $$"""
<!DOCTYPE html>
<html lang="hu">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Fleet Server – első beállítás</title>
<style>{{Css}}</style>
</head>
<body class="login-body">
  <form class="login-card wide" method="post" action="/web/setup">
    <div class="brand"><div class="brand-mark">◆</div><div class="brand-name">Fleet <span>Server</span></div></div>
    <h1>Első beállítás</h1>
    <p class="muted">Hozd létre a szuperadmin fiókot. Ez az egyetlen felhasználó, aki a
       webfelületről vezérli a flottát.</p>
    {{(error is null ? "" : $"<div class=\"alert\">{error}</div>")}}

    <label>Szuperadmin felhasználónév</label>
    <input name="user" value="admin" required>

    <label>Jelszó</label>
    <input name="password" type="password" required>
    <div class="hint">Legalább 12 karakter, háromféle karaktertípussal.</div>

    <label>Jelszó megerősítése</label>
    <input name="password2" type="password" required>

    <div class="row">
      <div class="col">
        <label>Webfelület portja</label>
        <input name="webPort" type="number" value="{{webPort}}" required>
        <div class="hint">Ezen a porton éred el ezt a felületet.</div>
      </div>
      <div class="col">
        <label>Agent-kommunikáció portja</label>
        <input name="agentPort" type="number" value="{{agentPort}}" required>
        <div class="hint">Ide jelentkeznek az agentek. Külön a webtől.</div>
      </div>
    </div>

    <button type="submit">Beállítás mentése és belépés</button>
  </form>
</body>
</html>
""";

    public const int DefaultWebPort = 8080;
    public const int DefaultAgentPort = 8443;

    public static string Dashboard(string user, Role role) => $$"""
<!DOCTYPE html>
<html lang="hu">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Fleet Server</title>
<style>{{Css}}</style>
</head>
<body>
<header class="topbar">
  <div class="brand"><div class="brand-mark">◆</div><div class="brand-name">Fleet <span>Server</span></div>
    <button class="navtog" onclick="toggleNav()" title="Menü összecsukása / kinyitása">☰</button></div>
  <div class="top-actions">
    <button class="ghost" onclick="pageClose();refresh()" title="Gépek"><span class="i">▣</span><span class="t">Gépek</span></button>
    {{(role >= Role.Kezelo ? Nav("showTokenDialog()", "＋", "Új agent") : "")}}
    {{Nav("showGroups()", "▦", "Csoportok")}}
    {{Nav("showCatalog()", "▤", "Programok")}}
    {{Nav("showRequests()", "✋", "Programkérések")}}
    {{Nav("showPolicies()", "◷", "Szabályok")}}
    {{Nav("showRules()", "◎", "Monitorozás")}}
    {{Nav("showSnmp()", "≋", "SNMP")}}
    {{Nav("showAlerts()", "⚠", "Riasztások")}}
    {{Nav("showReports()", "⇩", "Jelentések")}}
    {{Nav("showSettings()", "⚙", "Beállítások")}}
    {{(role >= Role.Admin ? Nav("showUsers()", "☺", "Felhasználók") + Nav("showSecurity()", "⚿", "Biztonság") : "")}}
    <span class="user">{{user}} · {{RoleLabel(role)}}</span>
    <a class="ghost" href="/web/logout" title="Kilépés"><span class="i">⏻</span><span class="t">Kilépés</span></a>
  </div>
</header>

<div class="stats" id="stats"></div>

<main class="layout">
  <section class="panel">
    <div class="panel-head"><h2>Felügyelt gépek</h2><div style="display:flex;gap:8px;align-items:center"><span id="mgBox"></span>
      <input id="search" class="search" placeholder="Keresés…" oninput="renderAgents()"></div></div>
    <div id="agentList" class="agent-list"></div>
  </section>

  <section class="panel">
    <div class="panel-head"><h2 id="detailTitle">Válassz gépet</h2>
      <div id="detailActions"></div>
    </div>
    <div id="detail" class="detail"></div>
  </section>
</main>

<div id="toast" class="toast"></div>

<div id="page" class="page"></div>

<div id="modal" class="modal-back" onclick="if(event.target===this)closeModal()">
  <div class="modal"><div id="modalBody"></div></div>
</div>

<script>window.ROLE={{(int)role}};</script>
<script>{{DialogJs}}</script>
<script>{{Js}}</script>
<script>{{MonitorJs}}</script>
<script>{{KeysJs}}</script>
<script>{{CatalogJs}}</script>
<script>{{ReportsJs}}</script>
<script>{{RulesJs}}</script>
<script>{{GroupsJs}}</script>
<script>{{StorageJs}}</script>
<script>{{SnmpJs}}</script>
<script>{{CveJs}}</script>
<script>{{RequestsJs}}</script>
</body>
</html>
""";

    // ================================================================ CSS

    private static string RoleLabel(Role r) => r switch { Role.Admin => "Adminisztrátor", Role.Kezelo => "Kezelő", _ => "Megfigyelő" };

    /// <summary>Oldalsó menüpont: ikon + felirat (összecsukott menüben csak az ikon, a felirat a title-ben). A Központ is használja.</summary>
    internal static string Nav(string onclick, string icon, string text) =>
        $"<button class=\"ghost\" onclick=\"{onclick}\" title=\"{text}\"><span class=\"i\">{icon}</span><span class=\"t\">{text}</span></button>";

    internal const string Css = """
:root{
  --bg:#0f1116; --panel:#171a21; --panel2:#1d212a; --line:#272c37;
  --ink:#e6e9ef; --muted:#8a93a3; --accent:#5b8cff; --accent2:#7c5cff;
  --ok:#31c07a; --warn:#e0a52b; --err:#f0554e; --violet:#b46bff;
  --radius:14px;
}
*{box-sizing:border-box}
/* sötét natív elemek mindenhol: a görgetősáv és a listák se legyenek világosak (a felhasználó képernyőképe) */
:root{color-scheme:dark}
html,body{margin:0;height:100%}
body{background:var(--bg);color:var(--ink);
  font-family:"Segoe UI",system-ui,-apple-system,sans-serif;font-size:14px}
h1,h2{margin:0;font-weight:600}
.muted{color:var(--muted)}

/* --- brand */
.brand{display:flex;align-items:center;gap:10px}
.brand-mark{width:32px;height:32px;border-radius:9px;display:grid;place-items:center;
  background:linear-gradient(135deg,var(--accent),var(--accent2));color:#fff;font-size:17px;
  box-shadow:0 4px 14px rgba(91,140,255,.35)}
.brand-name{font-size:16px;font-weight:600;letter-spacing:.2px}
.brand-name span{color:var(--muted);font-weight:500}

/* --- login / setup */
.login-body{display:grid;place-items:center;min-height:100vh;
  background:radial-gradient(1200px 600px at 30% -10%,rgba(124,92,255,.18),transparent),
             radial-gradient(1000px 500px at 90% 10%,rgba(91,140,255,.14),transparent),var(--bg)}
.login-card{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);
  padding:34px 32px;width:380px;box-shadow:0 24px 60px rgba(0,0,0,.5)}
.login-card.wide{width:520px}
.login-card h1{font-size:22px;margin:18px 0 6px}
.login-card label{display:block;margin:16px 0 6px;font-size:13px;color:var(--muted)}
.login-card input{width:100%;padding:11px 13px;background:var(--panel2);
  border:1px solid var(--line);border-radius:10px;color:var(--ink);font-size:14px;outline:none}
.login-card input:focus{border-color:var(--accent)}
.login-card button{margin-top:22px;width:100%;padding:12px;border:0;border-radius:10px;
  background:linear-gradient(135deg,var(--accent),var(--accent2));color:#fff;font-size:15px;
  font-weight:600;cursor:pointer}
.login-card button:hover{filter:brightness(1.08)}
.hint{font-size:12px;color:var(--muted);margin-top:5px}
.alert{background:rgba(240,85,78,.12);border:1px solid rgba(240,85,78,.4);color:#ff9a95;
  padding:10px 12px;border-radius:9px;margin-top:14px;font-size:13px}
.row{display:flex;gap:16px}.col{flex:1}

/* --- topbar */
.topbar{display:flex;align-items:center;justify-content:space-between;
  padding:14px 22px;border-bottom:1px solid var(--line);background:var(--panel)}
.top-actions{display:flex;align-items:center;gap:10px}
.user{color:var(--muted);font-size:13px;padding:0 6px}
.ghost{background:var(--panel2);border:1px solid var(--line);color:var(--ink);
  padding:8px 14px;border-radius:9px;cursor:pointer;font-size:13px;text-decoration:none;display:inline-block}
.ghost:hover{border-color:var(--accent);color:#fff}

/* --- stats */
.stats{display:flex;gap:14px;padding:18px 22px;flex-wrap:wrap}
.stat{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);
  padding:16px 20px;min-width:150px;flex:1}
.stat .n{font-size:26px;font-weight:700}
.stat .l{color:var(--muted);font-size:12px;margin-top:2px;text-transform:uppercase;letter-spacing:.4px}
.stat.err .n{color:var(--err)} .stat.warn .n{color:var(--warn)} .stat.ok .n{color:var(--ok)}

/* --- layout */
.layout{display:grid;grid-template-columns:minmax(360px,1fr) minmax(400px,1.2fr);
  gap:16px;padding:4px 22px 22px;align-items:start}
@media(max-width:900px){.layout{grid-template-columns:1fr}}
/* --- oldalsáv: széles képernyőn a felső sáv bal oldali menüvé válik (keskenyen felül marad) */
@media(min-width:1000px){
body:not(.login-body){padding-left:236px}
.topbar{position:fixed;left:0;top:0;bottom:0;width:236px;box-sizing:border-box;flex-direction:column;
  align-items:stretch;justify-content:flex-start;gap:16px;padding:22px 14px;border-bottom:none;
  border-right:1px solid var(--line);z-index:20;overflow-y:auto}
.topbar .brand{padding:0 6px 16px;border-bottom:1px solid var(--line)}
.top-actions{flex-direction:column;align-items:stretch;gap:3px;flex:1}
.top-actions .ghost{display:block;width:100%;box-sizing:border-box;text-align:left;padding:10px 12px;
  border:1px solid transparent;background:transparent;border-radius:8px;text-decoration:none;font-size:14px}
.top-actions .ghost:hover{background:var(--panel2);border-color:var(--line);color:#fff}
.top-actions .user{margin-top:auto;padding:12px 12px 6px;border-top:1px solid var(--line);font-size:12px}
.modal-back,.toast{z-index:50}
}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);overflow:hidden}
.panel-head{display:flex;align-items:center;justify-content:space-between;
  padding:16px 18px;border-bottom:1px solid var(--line);gap:12px}
.panel-head h2{font-size:15px}
.search{background:var(--panel2);border:1px solid var(--line);border-radius:8px;
  padding:7px 11px;color:var(--ink);font-size:13px;outline:none;width:180px}
.search:focus{border-color:var(--accent)}

/* --- agent list */
.agent-list{max-height:60vh;overflow:auto}
.agent{display:flex;align-items:center;gap:12px;padding:13px 18px;
  border-bottom:1px solid var(--line);cursor:pointer;transition:background .12s}
.agent:hover{background:var(--panel2)}
.agent.sel{background:linear-gradient(90deg,rgba(91,140,255,.14),transparent);
  box-shadow:inset 3px 0 0 var(--accent)}
.dot{width:9px;height:9px;border-radius:50%;flex:none}
.dot.online{background:var(--ok);box-shadow:0 0 8px rgba(49,192,122,.6)}
.dot.offline{background:var(--warn)} .dot.revoked{background:var(--violet)}
.agent .name{flex:1;font-weight:600}
.agent .meta{color:var(--muted);font-size:12px}
.badge{font-size:11px;padding:3px 9px;border-radius:20px;font-weight:600}
.badge.upd{background:rgba(224,165,43,.16);color:var(--warn)}
.badge.ok{background:rgba(49,192,122,.16);color:var(--ok)}

/* --- detail */
.detail{padding:18px}
.detail .subtitle{color:var(--muted);font-size:13px;margin-bottom:16px;line-height:1.6}
.upd-table{width:100%;border-collapse:collapse}
.upd-table th{text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:.4px;
  color:var(--muted);padding:8px 10px;border-bottom:1px solid var(--line)}
.upd-table td{padding:9px 10px;border-bottom:1px solid var(--line);font-size:13px}
.upd-table tr:last-child td{border:0}
.ver-old{color:var(--muted)} .ver-new{color:var(--ok);font-weight:600}
.chk{width:16px;height:16px;accent-color:var(--accent)}
.mono{font-family:Consolas,monospace;font-size:12px;color:var(--muted)}

/* --- buttons */
.btn{border:0;border-radius:9px;padding:8px 15px;font-size:13px;font-weight:600;cursor:pointer}
.btn.primary{background:linear-gradient(135deg,var(--accent),var(--accent2));color:#fff}
.btn.primary:hover{filter:brightness(1.08)}
.btn.danger{background:rgba(240,85,78,.14);color:var(--err);border:1px solid rgba(240,85,78,.35)}
.btn.small{padding:6px 12px;font-size:12px}
.btn:disabled{opacity:.5;cursor:default}
.detail-actions{display:flex;gap:8px}

/* --- toast + modal */
.toast{position:fixed;bottom:24px;left:50%;transform:translateX(-50%) translateY(80px);
  background:var(--panel2);border:1px solid var(--line);padding:12px 20px;border-radius:10px;
  box-shadow:0 12px 30px rgba(0,0,0,.5);opacity:0;transition:.25s;pointer-events:none;z-index:50}
.toast.show{opacity:1;transform:translateX(-50%) translateY(0)}
.toast.ok{border-color:rgba(49,192,122,.5)} .toast.err{border-color:rgba(240,85,78,.5)}
.modal-back{position:fixed;inset:0;background:rgba(0,0,0,.6);display:none;
  place-items:center;z-index:60;padding:16px}
.modal-back.show{display:grid}
/* Az ablak soha nem nagyobb a képernyőnél: a tartalom benne görget (eddig a hosszú ablak kilógott). */
.modal{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);
  padding:26px;width:560px;max-width:92vw;max-height:calc(100vh - 32px);overflow:auto;box-shadow:0 30px 70px rgba(0,0,0,.6)}

/* --- teljes oldalas nézet (monitorozás, programtár): a menüsáv mellett, bal oldalon kategória-menüvel */
.page{position:fixed;inset:0;background:var(--bg);z-index:40;display:none;flex-direction:column}
.page.show{display:flex}
@media(min-width:1000px){.page{left:236px}}
.page-head{display:flex;align-items:center;gap:10px;padding:14px 22px;border-bottom:1px solid var(--line);background:var(--panel);flex-wrap:wrap}
.page-head h2{font-size:17px}
.page-head .sub{color:var(--muted);font-size:13px;margin-right:auto}
.page-body{flex:1;display:flex;min-height:0}
.page-menu{width:220px;flex:none;border-right:1px solid var(--line);overflow:auto;padding:10px 8px;background:var(--panel)}
.page-menu button{display:flex;justify-content:space-between;align-items:center;gap:8px;width:100%;text-align:left;background:transparent;
  border:1px solid transparent;color:var(--ink);padding:9px 12px;border-radius:8px;cursor:pointer;font-size:14px;margin-bottom:2px}
.page-menu button:hover{background:var(--panel2)}
.page-menu button.sel{background:linear-gradient(90deg,rgba(91,140,255,.18),transparent);border-color:var(--line)}
.page-menu .cnt{color:var(--muted);font-size:12px}
.page-main{flex:1;overflow:auto;padding:18px 22px}
@media(max-width:760px){.page-body{flex-direction:column}.page-menu{width:auto;display:flex;overflow-x:auto;border-right:0;border-bottom:1px solid var(--line)}
  .page-menu button{width:auto;white-space:nowrap}}
.tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(160px,1fr));gap:10px;margin-bottom:18px}
.tile{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
.tile .v{font-size:20px;font-weight:700}.tile .l{font-size:12px;color:var(--muted);margin-top:2px}
.graphs{display:grid;grid-template-columns:repeat(auto-fill,minmax(400px,1fr));gap:14px}
@media(max-width:760px){.graphs{grid-template-columns:1fr}}
.gcard{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px;min-width:0}
.gcard .gh{display:flex;justify-content:space-between;align-items:center;gap:8px;font-size:13px}
.gcard .gv{font-family:Consolas,monospace;font-size:13px;white-space:nowrap}
.gbox{height:130px;position:relative;margin-top:8px;background:var(--panel2);border-radius:8px;border:1px solid var(--line)}
.gstat{font-size:11px;color:var(--muted);margin-top:6px;display:flex;gap:14px;flex-wrap:wrap}
.gtip{position:absolute;top:4px;pointer-events:none;background:var(--panel);border:1px solid var(--line);border-radius:6px;
  padding:2px 7px;font-size:11px;white-space:nowrap;display:none;z-index:2}
.gax{position:absolute;left:6px;font-size:10px;color:var(--muted);pointer-events:none}
/* menüpont teljes oldalon (openView): olvasható szélesség, a korábbi ablak-tartalom stílusaival */
.view{max-width:1000px}
.view h3{margin:18px 0 10px;font-size:16px}
.page-main label:has(>input[type=checkbox]),.page-main label:has(>input[type=radio]){color:var(--ink)}
.page-main input[type=checkbox],.page-main input[type=radio]{width:auto;accent-color:var(--accent)}
.page-main select{background:var(--panel2);color:var(--ink)}
/* a lista-választó (pl. a szabály metrika-listája) is sötét: a Chromium a natív, világos sémával rajzolná */
.modal select,.page-main select{color-scheme:dark}
.modal select option,.modal select optgroup{background:var(--panel2);color:var(--ink)}
/* összecsukható oldalsó menü: ikon + felirat; összecsukva csak az ikon (a felirat a buttonon title-ként) */
.top-actions .ghost .i{display:inline-block;width:22px;text-align:center;margin-right:8px;opacity:.85}
.navtog{display:none}
@media(min-width:1000px){
.navtog{display:block;margin-left:auto;background:transparent;border:1px solid var(--line);color:var(--muted);border-radius:7px;
  width:28px;height:28px;cursor:pointer;font-size:14px;line-height:1}
.navtog:hover{color:#fff;border-color:var(--accent)}
body.nav-min:not(.login-body){padding-left:64px}
body.nav-min .topbar{width:64px;padding:22px 8px}
body.nav-min .topbar .brand{flex-direction:column;gap:10px;padding:0 0 14px}
body.nav-min .brand-name,body.nav-min .top-actions .t,body.nav-min .top-actions .user{display:none}
body.nav-min .top-actions .ghost{text-align:center;padding:10px 0}
body.nav-min .top-actions .ghost .i{margin-right:0}
body.nav-min .navtog{margin-left:0}
body.nav-min .page{left:64px}
}
/* a teljes oldalon ugyanaz a mező- és tokenstílus, mint az ablakokban */
.page-main label{display:block;color:var(--muted);font-size:13px;margin:14px 0 6px}
.page-main input:not([type=checkbox]):not([type=radio]):not(.search){width:100%;padding:9px 12px;background:var(--panel2);
  border:1px solid var(--line);border-radius:9px;color:var(--ink);outline:none}
.page-main input:focus{border-color:var(--accent)}
.page .token{font-family:Consolas,monospace;font-size:15px;font-weight:700;color:var(--accent);
  background:var(--panel2);padding:12px 14px;border-radius:9px;word-break:break-all;margin:10px 0}
.page details summary{color:var(--accent)}
.sbadge{font-size:11px;padding:2px 8px;border-radius:20px;font-weight:600;white-space:nowrap}
.sbadge.repo{background:rgba(49,192,122,.16);color:var(--ok)}.sbadge.pending{background:rgba(224,165,43,.16);color:var(--warn)}
.sbadge.rejected{background:rgba(240,85,78,.14);color:var(--err)}
.sbadge.auto{background:rgba(91,140,255,.16);color:var(--accent)}
/* széles ablak (csomag-szerkesztő): a programlista kényelmesen elfér */
.modal.wide{width:880px}
/* kategória-választó címkék (programtár) */
.chips{display:flex;flex-wrap:wrap;gap:6px;margin:0 0 10px}
.chip{background:var(--panel2);border:1px solid var(--line);color:var(--ink);border-radius:20px;padding:4px 11px;font-size:12px;cursor:pointer}
.chip:hover{border-color:var(--accent)}
.chip.sel{border-color:var(--accent);background:rgba(91,140,255,.18)}
.chip .n{color:var(--muted);margin-left:5px}
.publink{color:var(--accent);cursor:pointer;font-size:12px}
.publink:hover{text-decoration:underline}
.catcell{font-size:12px;color:var(--muted);white-space:nowrap}
/* programválasztó lista kategória-fejlécekkel (csomag-szerkesztő, telepítés) */
.plist{max-height:min(56vh,540px);overflow:auto;border:1px solid var(--line);border-radius:9px;padding:0 12px;background:var(--panel)}
.pgh{position:sticky;top:0;z-index:1;background:var(--panel);display:flex;justify-content:space-between;font-size:11px;font-weight:600;
  text-transform:uppercase;letter-spacing:.5px;color:var(--accent);padding:10px 0 5px;border-bottom:1px solid var(--line)}
.pgh span{color:var(--muted);font-weight:500}
.modal label.prow,.prow{display:flex;gap:10px;align-items:center;margin:0;padding:6px 2px;border-bottom:1px solid rgba(255,255,255,.04);
  color:var(--ink);font-size:13px;cursor:pointer}
.prow:hover{background:var(--panel2)}
.prow .pn{flex:1;min-width:0}
.prow .pp{color:var(--muted);font-size:12px;white-space:nowrap}
.prow .pid{font-size:11px;white-space:nowrap;max-width:230px;overflow:hidden;text-overflow:ellipsis}
.ptools{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-bottom:8px}
.ptools .cnt{margin-left:auto;font-size:12px;color:var(--muted)}
/* gépek csoportosítva (főoldali lista, Központ: telephely gépei) */
.agroup{display:flex;justify-content:space-between;align-items:center;gap:8px;padding:9px 18px;background:var(--panel2);border-bottom:1px solid var(--line);
  font-size:12px;font-weight:600;text-transform:uppercase;letter-spacing:.4px;color:var(--accent);cursor:pointer;user-select:none}
.agroup span{color:var(--muted);font-weight:500;text-transform:none;letter-spacing:0}
.agroup.closed{opacity:.75}
td.agroup{display:table-cell;padding:9px 10px}
td.agroup span{float:right}
.grpsel{background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:6px 8px;color:var(--ink);font-size:13px}
.page-main textarea,.modal textarea{width:100%;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink);padding:9px 12px;
  font-family:Consolas,monospace;font-size:13px;outline:none;resize:vertical}
.page-main textarea:focus,.modal textarea:focus{border-color:var(--accent)}
/* haladásjelző (feltöltés, hosszabb műveletek); .ind = ismeretlen hosszúságú, mozgó sáv */
.pbar{height:8px;background:var(--panel2);border:1px solid var(--line);border-radius:5px;overflow:hidden;margin-top:6px}
.pbar>div{height:100%;width:0;background:linear-gradient(90deg,var(--accent),var(--accent2));transition:width .2s}
.pbar.ind>div{width:35%;animation:pind 1.1s ease-in-out infinite}
@keyframes pind{0%{margin-left:-35%}100%{margin-left:100%}}
.modal h3{margin:0 0 12px;font-size:18px}
.modal .token{font-family:Consolas,monospace;font-size:15px;font-weight:700;color:var(--accent);
  background:var(--panel2);padding:12px 14px;border-radius:9px;word-break:break-all;margin:10px 0}
.modal label{display:block;color:var(--muted);font-size:13px;margin:14px 0 6px}
.modal input{width:100%;padding:10px 12px;background:var(--panel2);border:1px solid var(--line);
  border-radius:9px;color:var(--ink);outline:none}
.modal input[type=checkbox],.modal input[type=radio]{width:auto;padding:0;margin:0;flex:none;accent-color:var(--accent)}
.modal label:has(>input[type=checkbox]),.modal label:has(>input[type=radio]){color:var(--ink)}
.empty{color:var(--muted);text-align:center;padding:40px 20px}
""";

    // ================================================================ JS

    private const string Js = """
let agents=[], selectedId=null;

async function api(path,opts){
  const r=await fetch('/web/api/'+path,{...opts,headers:{'Content-Type':'application/json'}});
  if(r.status===401){location.href='/web';return null}
  return r.json();
}

async function refresh(){
  const data=await api('agents');
  if(!data)return;
  agents=data.agents; window._pkg={v:data.agentPackageVersion||'',au:data.agentAutoUpdate!==false};
  renderStats(data);
  renderAgents();
  if(selectedId)renderDetail();
}

function renderStats(d){
  const online=agents.filter(a=>a.online&&!a.revoked).length;
  const totalUpd=agents.reduce((s,a)=>s+(a.updatesAvailable||0),0);
  const withUpd=agents.filter(a=>(a.updatesAvailable||0)>0&&!a.revoked).length;
  document.getElementById('stats').innerHTML=`
    <div class="stat"><div class="n">${agents.length}</div><div class="l">Gép összesen</div></div>
    <div class="stat ok"><div class="n">${online}</div><div class="l">Online</div></div>
    <div class="stat ${withUpd?'warn':''}"><div class="n">${withUpd}</div><div class="l">Frissítendő gép</div></div>
    <div class="stat ${totalUpd?'warn':''}"><div class="n">${totalUpd}</div><div class="l">Elavult alkalmazás</div></div>`+
    // 1.5.5: internetkapcsolat (ha a szerver figyeli) - kattintásra a Riasztások oldal
    (d.internet?`<div class="stat ${d.internet.online===false?'err':d.internet.online?'ok':''}" style="cursor:pointer" onclick="showAlerts()" title="${d.internet.online?esc('Válaszolt: '+d.internet.target+' ('+d.internet.rtt+' ms)'):''}">
      <div class="n" style="font-size:20px">${d.internet.online===false?'NINCS':d.internet.online?'rendben':'…'}</div>
      <div class="l">Internet${d.internet.online===false&&d.internet.since?' – '+esc(d.internet.since)+' óta':''}</div></div>`:'');
}

function renderAgents(){
  const q=(document.getElementById('search').value||'').toLowerCase();
  const list=document.getElementById('agentList');
  // Csoportosítás (Szerver / kliens, Windows-verzió, Virtuális / fizikai, Csoport) - a választás a böngészőben marad.
  const mb=document.getElementById('mgBox');if(mb&&!document.getElementById('mgBy'))mb.innerHTML=mgSel('mgBy','mgSet(this.value);renderAgents()');
  const shown=agents.filter(a=>a.machineName.toLowerCase().includes(q)||(a.groups||[]).concat(a.classes||[]).some(g=>g.toLowerCase().includes(q)));
  if(!shown.length){list.innerHTML='<div class="empty">Nincs megjeleníthető gép.</div>';return}
  const row=a=>{
    const st=a.revoked?'revoked':(a.online?'online':'offline');
    const upd=a.updatesAvailable||0;
    const badge=a.revoked?'':(upd>0?`<span class="badge upd">${upd} frissítés</span>`:`<span class="badge ok">naprakész</span>`);
    const sc=a.score&&!a.revoked?`<span class="badge" title="Eszköz-összegzés: biztonság ${a.score.security}, állapot ${a.score.health}" style="background:${scoreCol(a.score.score)}22;color:${scoreCol(a.score.score)}">${a.score.score}</span>`:'';
    return `<div class="agent ${a.agentId===selectedId?'sel':''}" onclick="select('${a.agentId}')">
      <span class="dot ${st}"></span>
      <span class="name">${esc(a.machineName)}</span>
      ${sc}${badge}
      <span class="meta">${a.lastSeen||'–'}</span>
    </div>`;
  };
  if(!_mgBy){list.innerHTML=shown.map(row).join('');return;}
  list.innerHTML=machGroups(shown,_mgBy).map(([k,l])=>{const key=_mgBy+':'+k,h=mgHead(key,k,l);
    return `<div class="agroup ${h.closed?'closed':''}" onclick="mgToggle(${esc(JSON.stringify(key))});renderAgents()">${h.html}</div>`+(h.closed?'':l.map(row).join(''));}).join('');
}

function select(id){selectedId=id;renderAgents();renderDetail()}

function renderDetail(){
  const a=agents.find(x=>x.agentId===selectedId);
  const title=document.getElementById('detailTitle');
  const box=document.getElementById('detail');
  const actions=document.getElementById('detailActions');
  if(!a){title.textContent='Válassz gépet';box.innerHTML='';actions.innerHTML='';return}

  title.textContent=a.machineName;
  actions.innerHTML=a.revoked?'':`
    <div class="detail-actions">
      ${window.ROLE>=1?'<button class="btn small primary" onclick="updateSelected()">Kijelöltek frissítése</button><button class="btn small" onclick="updateAll()">Összes</button>':'<span style="color:var(--muted);font-size:12px">Csak megtekintés – frissítéshez kezelői jog kell.</span>'}
      <button class="btn small" onclick="showMonitor('${a.agentId}','${esc(a.machineName)}')">Monitorozás</button>
      <button class="btn small" onclick="showCve('${a.agentId}')">Sérülékenységek${a.cve&&a.cve.total?` <span class="badge upd" style="${a.cve.exploited||a.cve.critical?'color:var(--err)':''}">${a.cve.total}</span>`:''}</button>
      ${window.ROLE>=1?`<button class="btn small" onclick="showUsb('${a.agentId}')">USB-napló</button>`:''}
      ${window.ROLE>=1?`<button class="btn small" onclick="showServices('${a.agentId}','${esc(a.machineName)}')">Szolgáltatások…</button>`:''}
      ${window.ROLE>=2?`<button class="btn small ${a.terminal?'primary':''}" onclick="showTerminal('${a.agentId}','${esc(a.machineName)}')">Terminál${a.terminal?'':' (ki)'}</button>`:''}
      <button class="btn small danger" onclick="revoke('${a.agentId}')">Visszavonás</button>
    </div>`;

  const meta=[a.operatingSystem,a.isVirtual?'virtuális':'fizikai','agent '+a.agentVersion].filter(Boolean).join(' · ');
  let html=scoreBox(a.score)+`<div class="subtitle">${esc(meta)}<br>Ujjlenyomat: <span class="mono">${a.fingerprint||''}</span><br>
    Utoljára látva: ${a.lastSeen||'–'} · regisztrálva: ${a.enrolled||'–'}</div>`;

  const upd=a.updates||[];
  if(upd.length){
    html+=`<table class="upd-table"><tr><th style="width:20px"></th><th>Alkalmazás</th>
      <th>Telepített</th><th>Elérhető</th></tr>`+
      upd.map((u,i)=>`<tr><td><input type="checkbox" class="chk" data-id="${esc(u.id)}" checked></td>
        <td>${esc(u.name)}</td><td class="ver-old">${esc(u.installed)}</td>
        <td class="ver-new">${esc(u.available)}</td></tr>`).join('')+`</table>`;
  } else {
    html+=`<div class="empty">✓ Minden alkalmazás naprakész.</div>`;
  }
  html+=detailExtra(a);
  box.innerHTML=html;
}

async function updateSelected(){
  const ids=[...document.querySelectorAll('#detail .chk:checked')].map(c=>c.dataset.id);
  if(!ids.length){toast('Jelölj ki legalább egy alkalmazást','err');return}
  const r=await api('queue',{method:'POST',body:JSON.stringify({agentId:selectedId,ids})});
  if(r&&r.ok)toast(`${ids.length} frissítési feladat betéve`,'ok');
}
async function updateAll(){
  if(!await uiConfirm('A gép ÖSSZES frissíthető alkalmazása frissüljön?'))return;
  const r=await api('queue',{method:'POST',body:JSON.stringify({agentId:selectedId,all:true})});
  if(r&&r.ok)toast('Feladat betéve: összes frissítése','ok');
}
async function revoke(id){
  if(!await uiConfirm('Visszavonod ezt az agentet? Utána nem tud jelentkezni.'))return;
  const r=await api('revoke',{method:'POST',body:JSON.stringify({agentId:id})});
  if(r&&r.ok){toast('Agent visszavonva','ok');refresh()}
}

async function showTokenDialog(){
  openView(`<h3>Új agent hozzáadása</h3>
    <label>Címke (melyik telephely / ügyfél)</label>
    <input id="tokenLabel" placeholder="pl. iroda-2024">
    <p class="muted" style="font-size:12px;margin-top:10px">A token a beállításokban megadott
      ideig és annyi gépre érvényes. Egy gépre elég egy csomag.</p>
    <button class="btn primary" style="margin-top:14px" onclick="createToken()">Telepítőcsomag készítése</button>`);
}
let lastInstaller=null, lastZipUrl=null;
async function createToken(){
  const label=document.getElementById('tokenLabel').value.trim()||'nevtelen';
  const r=await api('token',{method:'POST',body:JSON.stringify({label})});
  if(!r||!r.token){toast('Nem sikerült','err');return}
  lastInstaller=r.installerPs; lastZipUrl=r.zipUrl;

  const life = r.lifetimeMinutes>0 ? `${r.lifetimeMinutes} percig` : 'lejárat nélkül';
  const uses = r.maxUses>0 ? `${r.maxUses} gépre` : 'korlátlan gépre';

  const zipBlock = r.zipUrl
    ? `<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:14px;margin:14px 0">
         <div style="font-weight:600;margin-bottom:8px">Töltsd le a telepítőcsomagot</div>
         <a class="btn primary" href="${r.zipUrl}" download>DevToolboxAgent-telepito.zip letöltése</a>
         <div style="font-size:12px;color:var(--muted);margin-top:8px">
           Benne az agent és a TELEPIT.cmd (a tokennel). Csomagold ki, majd a
           <b>TELEPIT.cmd</b>-t futtasd rendszergazdaként. Több semmi.</div>
       </div>`
    : `<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:14px;margin:14px 0">
         <div style="color:var(--warn)">Ebben a szerverben nincs beépített agent – a ZIP nem készíthető.
           Telepítsd a teljes FleetServer.exe-t, vagy másold a DevToolboxAgent.exe-t a szerver adatmappájába.</div>
       </div>`;

  openView(`<h3>Kész telepítőcsomag</h3>
    <p class="muted">A token <b>${life}</b>, <b>${uses}</b> érvényes.</p>
    ${zipBlock}
    <div style="border-top:1px solid var(--line);margin-top:8px;padding-top:14px">
      <div style="font-size:12px;color:var(--muted);margin-bottom:6px">Alternatíva: PowerShell telepítő</div>
      <button class="btn small" onclick="downloadInstaller()">telepit-agent.ps1</button>
    </div>
    <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
      <div style="font-size:12px;color:var(--muted);margin-bottom:6px">Ha inkább kézzel: token és ujjlenyomat</div>
      <div class="token" style="font-size:13px">${r.token}</div>
      <div class="token" style="font-size:13px;color:var(--muted)">${r.fingerprint}</div>
    </div>`);
}

function downloadInstaller(){
  if(!lastInstaller){toast('Nincs mit letölteni','err');return}
  const blob=new Blob([lastInstaller],{type:'text/plain;charset=utf-8'});
  const a=document.createElement('a');
  a.href=URL.createObjectURL(blob); a.download='telepit-agent.ps1';
  document.body.appendChild(a); a.click(); a.remove();
  toast('Telepítő letöltve','ok');
}

async function showSettings(){
  const info=await api('server-info',{method:'POST',body:'{}'});
  if(!info){toast('Nem érhető el','err');return}
  const upd = info.pendingUpdate
    ? '<div style="color:var(--warn);font-size:12px;margin-top:6px">Feltöltött frissítés vár – a szerver újraindításakor lép életbe.</div>' : '';
  openView(`<h3>Beállítások</h3>

    <div style="font-size:13px;color:var(--muted);margin-bottom:6px">Szerver verzió: <b style="color:var(--ink)">${info.version}</b>
      · web-port ${info.webPort} · agent-port ${info.agentPort}</div>

    <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
      <div style="font-weight:600;margin-bottom:10px">Beléptető token alapértékei</div>
      <div class="row">
        <div class="col"><label style="margin-top:0">Élettartam (perc, 0=nincs)</label>
          <input id="setLife" type="number" value="${info.tokenLifetimeMinutes}"></div>
        <div class="col"><label style="margin-top:0">Max. gép (0=korlátlan)</label>
          <input id="setUses" type="number" value="${info.tokenMaxUses}"></div>
      </div>
      <label style="display:flex;align-items:center;gap:8px;margin-top:12px"><input type="checkbox" id="setAU" ${info.agentAutoUpdate?'checked':''}>
        Agentek automatikus frissítése (a szerverbe épített agentre${info.agentPackageVersion?' – most: '+esc(info.agentPackageVersion):''})</label>
      <button class="btn primary small" style="margin-top:12px" onclick="saveTokenSettings()">Mentés</button>
    </div>

    ${window.ROLE>=2?'<div id="portBox" style="border-top:1px solid var(--line);margin-top:16px;padding-top:14px"></div>':''}
    ${window.ROLE>=2?'<div id="storageBox" style="border-top:1px solid var(--line);margin-top:16px;padding-top:14px"></div>':''}

    <div style="border-top:1px solid var(--line);margin-top:16px;padding-top:14px">
      <div style="font-weight:600;margin-bottom:8px">Központ kapcsolat</div>
      <div style="font-size:12px;color:var(--muted);margin-bottom:8px">${hubStatus(info)}</div>
      <label style="margin-top:0">Központ címe (https://)</label>
      <input id="hubUrl" placeholder="https://fleet.poltoli.hu" value="${esc(info.hubUrl||'')}">
      <div class="row">
        <div class="col"><label>Telephely neve</label><input id="hubName" value="${esc(info.hubSiteName||'')}"></div>
        <div class="col"><label>Központ ujjlenyomat</label><input id="hubFp" value="${esc(info.hubFingerprint||'')}"></div>
      </div>
      <label>Telephely-token (a Központ adja, csak regisztrációhoz)</label>
      <input id="hubTok" placeholder="${info.hubEnrolled?'már regisztrálva – csak új regisztrációhoz töltsd ki':''}">
      <div style="display:flex;gap:8px;margin-top:12px;flex-wrap:wrap"><button class="btn primary small" onclick="saveHub()">Mentés és kapcsolódás</button>
        <button class="btn small" onclick="hubTest()">Kapcsolat tesztelése</button>
        ${info.hubUrl?'<button class="btn small" onclick="hubDial()">Kapcsolódás most</button>':''}</div>
      <div id="hubDialRes" style="font-size:12px;margin-top:8px"></div>
      ${info.hubUrl?'<div id="wgBox" style="margin-top:14px"></div>':''}
    </div>

    <div style="border-top:1px solid var(--line);margin-top:16px;padding-top:14px">
      <div style="font-weight:600;margin-bottom:8px">Fleet Server frissítése</div>
      <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Töltsd fel az új FleetServer.exe-t.
        A csere a szerver újraindításakor történik (a szolgáltatás magától újraindul).</div>
      <input id="updFile" type="file" accept=".exe" style="font-size:12px">
      <button class="btn primary small" style="margin-top:12px" onclick="uploadServer()">Feltöltés</button>
      <div id="updProg" style="font-size:12px;color:var(--muted);margin-top:8px"></div>
      ${upd}
    </div>`);
  if(window.ROLE>=2){loadPorts();loadStorage();}
  if(info.hubUrl)loadWg();
}
// ---- WireGuard-alagút a Központ felé (1.5.9): állapot és helyi javítás (végpont, kulcscsere, újrakapcsolás).
//      Szigorú mód: ha egyszer az alagúton ment a betárcsázás, csak azon megy – ha nem áll fel, itt javítható.
async function loadWg(){const box=document.getElementById('wgBox');if(!box)return;
  const d=await api('wg-status',{method:'POST',body:'{}'});if(!d||!d.ok){box.innerHTML='';return;}
  if(!d.offered){box.innerHTML=`<div style="border-top:1px solid var(--line);padding-top:12px">
    <div style="font-weight:600;margin-bottom:4px">WireGuard-alagút</div>
    <div style="font-size:12px;color:var(--muted)">A Központ még nem ajánlott alagutat ehhez a telephelyhez. Kapcsolódj a Központhoz, és a Központ Beállításaiban kapcsold be a WireGuardot.</div></div>`;return;}
  const a=window.ROLE>=2,sz=b=>b>=1048576?(b/1048576).toFixed(1)+' MB':b>=1024?Math.round(b/1024)+' KB':b+' B';
  const st=d.recent?'<span class="badge ok">alagút él</span>':d.running?'<span class="badge upd" style="color:var(--warn)">fut, de nincs friss kézfogás</span>':'<span class="badge upd" style="color:var(--err)">az alagút nem fut</span>';
  box.innerHTML=`<div style="border-top:1px solid var(--line);padding-top:12px">
    <div style="font-weight:600;margin-bottom:8px">WireGuard-alagút a Központ felé ${st}${d.required?' <span class="badge" style="color:var(--accent)">kötelező</span>':''}</div>
    <div style="font-size:12px;color:var(--muted);line-height:1.7">
      Saját cím: <b class="mono">${esc(d.address||'–')}</b> · Központ: <b class="mono">${esc(d.hubAddress||'–')}</b><br>
      Végpont (a Központ nyilvános címe): <b class="mono">${esc(d.endpoint||'–')}</b>${d.endpointOverride?' <span style="color:var(--warn)">(helyi felülírás)</span>':''}<br>
      Kézfogás: ${d.handshake?esc(d.handshake):'<span style="color:var(--err)">még nem volt</span>'} · le ${sz(d.rx)} · fel ${sz(d.tx)}
      ${d.lastError?`<br><span style="color:var(--err)">${esc(d.lastError)}</span>`:''}</div>
    ${a?`<div style="margin-top:10px"><label style="margin-top:0;font-size:12px">Végpont felülírása (gépnév:port; üresen a Központé: ${esc(d.hubEndpoint||'')})</label>
      <input id="wgEp" placeholder="${esc(d.hubEndpoint||'fleet.poltoli.hu:51820')}" value="${esc(d.endpointOverride||'')}">
      <div style="display:flex;gap:8px;margin-top:10px;flex-wrap:wrap">
        <button class="btn small primary" onclick="wgSave()">Végpont mentése</button>
        <button class="btn small" onclick="wgRepair(false)">Újrakapcsolás</button>
        <button class="btn small danger" onclick="wgRepair(true)">Új kulcs (kulcscsere)</button></div>
      <div id="wgMsg" style="font-size:12px;color:var(--muted);margin-top:6px"></div></div>`:''}</div>`;}
async function wgSave(){const m=document.getElementById('wgMsg');m.textContent='Mentés…';
  const r=await api('wg-save',{method:'POST',body:JSON.stringify({endpoint:document.getElementById('wgEp').value.trim()})});
  if(r&&r.ok){toast('Végpont mentve','ok');loadWg();}else{m.textContent='';toast((r&&r.error)||'Nem sikerült','err');}}
async function wgRepair(newKey){const m=document.getElementById('wgMsg');
  if(newKey&&!await uiConfirm('Új WireGuard-kulcsot készítesz. A Központtal az aláírt csatornán közöljük, utána felépül az alagút. Folytatod?'))return;
  m.textContent=newKey?'Kulcscsere és újrakapcsolás…':'Újrakapcsolás…';
  const r=await api('wg-repair',{method:'POST',body:JSON.stringify({newKey})});
  if(r&&r.ok){toast('Kész – az alagút hamarosan felépül','ok');setTimeout(loadWg,1500);}else{m.textContent='';toast((r&&r.error)||'Nem sikerült','err');}}
// ---- Portok kézi állítása (admin). Agent-port: a régi port az agentek átállásáig figyel (az aláírt válaszban kapják
//      az újat, és az új porton ellenőrzik a szerver kulcsát); web-port: azonnal vált, a böngészőt átirányítjuk.
async function loadPorts(){const box=document.getElementById('portBox');if(!box)return;const p=await api('ports',{method:'POST',body:'{}'});if(!p||p.error)return;
  const names=l=>l.length?esc(l.slice(0,15).join(', '))+(l.length>15?' …':''):'–';
  box.innerHTML=`<div style="font-weight:600;margin-bottom:8px">Portok</div>
    <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Az <b>agent-port</b> váltásakor a régi port még figyel: a gépek a következő jelentkezéskor aláírt válaszban
      kapják az újat, az új porton ellenőrzik a szerver kulcsát, és átállnak. A régi port bezárul, ha minden aktív gép átállt (vagy letelt a türelmi idő).
      A <b>web-port</b> azonnal vált – a böngésző átirányul. Az új portra tűzfalszabály és URL-foglalás készül.</div>
    ${p.oldAgentPort>0?`<div style="background:rgba(224,165,43,.1);border:1px solid rgba(224,165,43,.35);border-radius:9px;padding:10px 12px;margin-bottom:10px;font-size:13px">
      Portváltás folyamatban: a régi <b>${p.oldAgentPort}</b> is figyel (${esc(p.since||'')} óta, legkésőbb ${esc(p.deadline||'')}-ig).<br>
      Átállt: <b>${p.onNew.length}</b> gép · még a régin: <b>${p.onOld.length}</b> (${names(p.onOld)}) · a szerver indulása óta nem jelentkezett: <b>${p.unknown.length}</b> (${names(p.unknown)})
      <div style="margin-top:8px"><button class="btn small danger" onclick="portCloseOld()">Régi port lezárása most</button></div></div>`:''}
    <div class="row"><div class="col"><label style="margin-top:0">Agent-port</label><input id="pAgent" type="number" value="${p.agentPort}"></div>
      <div class="col"><label style="margin-top:0">Web-port</label><input id="pWeb" type="number" value="${p.webPort}"></div>
      <div class="col"><label style="margin-top:0">Régi port legfeljebb (nap)</label><input id="pDays" type="number" value="${p.days}"></div></div>
    <button class="btn primary small" style="margin-top:12px" onclick="portSave()">Portok mentése</button>`;}
async function portSave(){const v=id=>parseInt(document.getElementById(id).value)||0;
  const body={agentPort:v('pAgent'),webPort:v('pWeb'),days:v('pDays')||30};
  if(!await uiConfirm(`Agent-port: ${body.agentPort}, web-port: ${body.webPort}. Mehet?`))return;
  const r=await api('port-save',{method:'POST',body:JSON.stringify(body)});
  if(!r||!r.ok){toast((r&&r.error)||'Nem sikerült','err');return;}
  if(r.webChanged&&r.webUrl){toast('A web-port megváltozott – átirányítás…','ok');setTimeout(()=>location.href=r.webUrl,2500);return;}
  toast('Portok mentve','ok');loadPorts();}
async function portCloseOld(){if(!await uiConfirm('Lezárod a régi agent-portot? Az át nem állt gépek ezután nem érik el a szervert (újra kell telepíteni / címet javítani).'))return;
  const r=await api('port-close-old',{method:'POST',body:'{}'});if(r&&r.ok){toast('A régi port lezárva','ok');loadPorts();}else toast((r&&r.error)||'Hiba','err');}

function hubStatus(i){
  if(!i.hubUrl) return 'Nincs beállítva – a telephely nem tárcsáz be.';
  if(i.hubError) return '<span style="color:var(--err)">'+esc(i.hubError)+'</span>';
  if(i.hubLastContact) return '<span style="color:var(--ok)">Kapcsolódva</span> · utoljára '+i.hubLastContact+' · '+Math.round(i.hubInterval/60*10)/10+' percenként';
  return 'Beállítva, első kapcsolódásra vár…';
}
async function saveHub(){
  const body={hubUrl:document.getElementById('hubUrl').value,hubSiteName:document.getElementById('hubName').value,
    hubFingerprint:document.getElementById('hubFp').value,hubToken:document.getElementById('hubTok').value};
  const r=await api('hub-settings',{method:'POST',body:JSON.stringify(body)});
  // Mentés után azonnal betárcsázunk (regisztráció, ha új token jött) - nem kell a következő körre várni.
  if(r&&r.ok){toast('Központ beállítás mentve – kapcsolódás…','ok');if(body.hubUrl)hubDial();} else toast((r&&r.error)||'Nem sikerült','err');
}
// Kapcsolat-teszt a beírt (még nem mentett) címmel és ujjlenyomattal: elérés (TLS, proxy) és a Központ kulcsa.
async function hubTest(){const box=document.getElementById('hubDialRes');uiBusy(box,'Teszt…');
  const r=await api('hub-test',{method:'POST',body:JSON.stringify({hubUrl:document.getElementById('hubUrl').value,hubFingerprint:document.getElementById('hubFp').value})});
  box.innerHTML=((r&&r.steps)||['✗ Nem sikerült']).map(s=>`<div style="color:${s.startsWith('✓')?'var(--ok)':s.startsWith('✗')?'var(--err)':'var(--muted)'}">${esc(s)}</div>`).join('')+
    (r&&r.ok?'<div style="color:var(--muted);margin-top:4px">A „Mentés és kapcsolódás” után a telephely azonnal regisztrál és betárcsázik.</div>':'');}
// Azonnali betárcsázás a Központba (a Központ kérései - frissítés, feladat, szabály - így nem várnak a következő körre).
async function hubDial(){const box=document.getElementById('hubDialRes');uiBusy(box,'Kapcsolódás…');
  const r=await api('hub-dial',{method:'POST',body:'{}'});
  box.innerHTML=r&&r.ok?`<span style="color:var(--ok)">Kapcsolódva (${esc(r.lastContact||'')}) – a Központ kérései átjöttek.</span>`:`<span style="color:var(--err)">${esc((r&&r.error)||'Nem sikerült')}</span>`;}
async function saveTokenSettings(){
  const life=parseInt(document.getElementById('setLife').value)||0;
  const uses=parseInt(document.getElementById('setUses').value)||0;
  const au=document.getElementById('setAU'); const r=await api('settings',{method:'POST',body:JSON.stringify({tokenLifetimeMinutes:life,tokenMaxUses:uses,agentAutoUpdate:au?au.checked:null})});
  if(r&&r.ok)toast('Beállítások mentve','ok'); else toast('Nem sikerült','err');
}

async function uploadServer(){
  const f=document.getElementById('updFile').files[0];
  if(!f){toast('Válassz exe fájlt','err');return}
  const prog=document.getElementById('updProg');
  const j=await uiUpload('/web/api/upload-server',f,prog);
  if(j.ok){prog.innerHTML=uiBar(100)+'<span style="color:var(--ok)">'+esc(j.message||'Feltöltve')+'</span>';toast('Feltöltve','ok');}
  else prog.innerHTML='<span style="color:var(--err)">'+esc(j.error||'hiba')+'</span>';
}

function openModal(html,wide){document.getElementById('modalBody').innerHTML=html;
  const m=document.querySelector('#modal .modal');if(m)m.classList.toggle('wide',!!wide);
  document.getElementById('modal').classList.add('show')}
function closeModal(){document.getElementById('modal').classList.remove('show')}
function copyText(t){navigator.clipboard.writeText(t).then(()=>toast('Vágólapra másolva','ok'))}

let toastTimer;
function toast(msg,kind){const t=document.getElementById('toast');
  t.textContent=msg;t.className='toast show '+(kind||'');
  clearTimeout(toastTimer);toastTimer=setTimeout(()=>t.className='toast',2600)}

async function showUsers(){
  const d=await api('users',{method:'POST',body:'{}'});
  if(!d||!d.users){toast('Nincs jogosultság','err');return}
  const rows=d.users.map(u=>`<tr><td>${esc(u.username)}${u.disabled?' <span style="color:var(--warn)">(tiltva)</span>':''}</td>
    <td>${esc(u.roleName)}</td><td style="text-align:right">
    <button class="btn small" onclick="editUser('${esc(u.username)}',${u.role},${u.disabled})">Módosít</button></td></tr>`).join('');
  openView(`<h3>Felhasználók</h3>
    <table class="upd-table"><tr><th>Név</th><th>Szerepkör</th><th></th></tr>${rows}</table>
    <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
      <div style="font-weight:600;margin-bottom:8px">Új felhasználó</div>
      <input id="nu" placeholder="felhasználónév">
      <input id="np" type="password" placeholder="jelszó (min. 12 karakter, 3 típus)" style="margin-top:8px">
      <select id="nr" style="margin-top:8px;width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">
        <option value="0">Megfigyelő – csak néz</option>
        <option value="1">Kezelő – frissíthet, gyakoriságot állít</option>
        <option value="2">Adminisztrátor – mindent, felhasználókat is</option></select>
      <button class="btn primary small" style="margin-top:12px" onclick="createUser()">Létrehozás</button>
    </div>`);
}
async function createUser(){
  const r=await api('user-create',{method:'POST',body:JSON.stringify({
    username:document.getElementById('nu').value,password:document.getElementById('np').value,role:parseInt(document.getElementById('nr').value)})});
  if(r&&r.ok){toast('Létrehozva','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');
}
function editUser(name,role,disabled){
  openModal(`<h3>${esc(name)}</h3>
    <label>Szerepkör</label>
    <select id="er" style="width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">
      <option value="0"${role==0?' selected':''}>Megfigyelő</option>
      <option value="1"${role==1?' selected':''}>Kezelő</option>
      <option value="2"${role==2?' selected':''}>Adminisztrátor</option></select>
    <button class="btn small" style="margin-top:10px" onclick="setRole('${esc(name)}')">Szerepkör mentése</button>
    <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
      <label>Új jelszó</label><input id="ep" type="password" placeholder="min. 12 karakter">
      <button class="btn small" style="margin-top:10px" onclick="setPw('${esc(name)}')">Jelszó mentése</button>
    </div>
    <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px;display:flex;gap:8px">
      <button class="btn small" onclick="disableUser('${esc(name)}',${!disabled})">${disabled?'Engedélyezés':'Letiltás'}</button>
      <button class="btn small danger" onclick="delUser('${esc(name)}')">Törlés</button>
    </div>`);
}
async function setRole(n){const r=await api('user-role',{method:'POST',body:JSON.stringify({username:n,role:parseInt(document.getElementById('er').value)})});
  if(r&&r.ok){toast('Mentve','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
async function setPw(n){const r=await api('user-password',{method:'POST',body:JSON.stringify({username:n,password:document.getElementById('ep').value})});
  if(r&&r.ok){toast('Jelszó módosítva','ok');}else toast((r&&r.error)||'Hiba','err');}
async function disableUser(n,d){const r=await api('user-disable',{method:'POST',body:JSON.stringify({username:n,disabled:d})});
  if(r&&r.ok){toast('Kész','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
async function delUser(n){if(!await uiConfirm(n+' törlése?'))return;const r=await api('user-delete',{method:'POST',body:JSON.stringify({username:n})});
  if(r&&r.ok){toast('Törölve','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
function esc(s){return String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]))}

// ---------------- monitorozás: teljes oldalon, kategóriánként (WebUi.MonitorJs) ----------------
let monAgent=null;
// A bővített mérések (SMART-részletek, hardver, ping, biztonság…) az 1.5.0-s agenttől jönnek - régebbinél ezt jelezzük.
function monAgentWarn(v){return verNewer('1.5.0',v)?`Az agent ${v||'?'} – a bővített mérések (SMART-részletek, hardver-érzékelők, ping, biztonság…) az agent 1.5.0-ra frissülése után jelennek meg`:'';}
function showMonitor(id,name){
  monAgent=id;const a=agents.find(x=>x.agentId===id)||{};
  openMonitorPage({title:'Monitorozás – '+name,req:{agentId:id},warn:monAgentWarn(a.agentVersion),
    extra:window.ROLE>=1?'<button class="btn small" onclick="showMonSettings()">Mit mérjen?</button>':''});
}
// A „+ küszöb” a szabály-szerkesztőt nyitja (WebUi.RulesJs), előre kitöltve ezzel a metrikával és géppel.
function monAlert(key,label){ruEdit(null,{key,label,agentId:monAgent});}
// „Mit mérjen?”: a Monitorozás – szabályok oldal mérés-lapja (WebUi.RulesJs).
function showMonSettings(){showRules('measure');}

// ---------------- riasztások ----------------
async function showAlerts(){
  const d=await api('alerts',{method:'POST',body:'{}'});
  const lg=(await api('alert-log',{method:'POST',body:'{}'}))||{log:''};
  const rules=(d&&d.rules)||[], hubRules=(d&&d.hubRules)||[];
  const act=(d&&d.active)||[]; window._offGroups=(d&&d.groups)||[];
  const og=(d&&d.offlineGroups)||[], om=d?d.offlineMinutes:15;
  openView(`<h3>Riasztások</h3>
    ${act.length?`<div style="font-weight:600;margin:6px 0">Aktív most</div><table class="upd-table">${act.map(a=>`<tr><td>${esc(a.machine)}</td><td>${esc(a.name)}${a.metricKey?` <span class="mono" style="font-size:11px">${esc(a.metricKey)}</span>`:''}</td>
      <td class="mono">${a.offline?Math.round(a.lastValue)+' perc':a.ruleId==='internet'?'nincs internet':a.ruleId==='szolgaltatas'?'leállt':(+a.lastValue).toFixed(1)}</td><td>${esc(a.since||'')}</td></tr>`).join('')}</table>`:'<div class="empty" style="padding:16px">Most nincs aktív riasztás.</div>'}
    <div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin:12px 0">
      <div style="font-weight:600;margin-bottom:6px">Kiesés-riasztás</div>
      <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Riaszt, ha egy gép ennyi perce nem jelentkezett (az agent 5 percenként jelentkezik; 0 = kikapcsolva, legalább 10).
        Az esténként kikapcsolt gépek kihagyhatók: jelölj ki csoportokat, és csak azok gépei riasztanak.</div>
      <div style="display:flex;gap:8px;align-items:center"><input id="offMin" type="number" value="${om}" style="width:90px" ${window.ROLE>=1?'':'disabled'}> perc</div>
      ${window._offGroups.length?`<div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:8px">${window._offGroups.map((g,i)=>`<label style="display:flex;gap:4px;align-items:center;margin:0"><input type="checkbox" class="offGrp" value="${i}" ${og.includes(g)?'checked':''} ${window.ROLE>=1?'':'disabled'}>${esc(g)}</label>`).join('')}</div>
        <div style="font-size:11px;color:var(--muted);margin-top:4px">Ha egyik sincs bejelölve: minden gép.</div>`:''}
      ${window.ROLE>=1?'<button class="btn small primary" style="margin-top:10px" onclick="saveOffline()">Mentés</button>':''}
    </div>
    <div id="netBox" style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin:12px 0"></div>
    ${window.ROLE>=1?`<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin:12px 0;display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap">
      <div><div style="font-weight:600">Másolás külső eszközre</div><div style="font-size:12px;color:var(--muted)">Az USB- / cserélhető meghajtókra írt fájlok naplója minden gépről (a Mit mérjen? oldalon kapcsolható be).</div></div>
      <button class="btn small" onclick="showUsb('')">USB-napló →</button></div>`:''}
    <div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin:12px 0;display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap">
      <div><div style="font-weight:600">Küszöb-szabályok</div><div style="font-size:12px;color:var(--muted)">${rules.length} helyi${hubRules.length?` · ${hubRules.length} a Központtól`:''} · ${rules.filter(r=>r.activeCount>0).length} riaszt most.
        A szabályok a Monitorozás menüpontban: lista, új szabály metrika-választóval, gép / csoport / minden gép hatókörrel.</div></div>
      <button class="btn small primary" onclick="showRules('rules')">Szabályok kezelése →</button></div>
    <div style="border-top:1px solid var(--line);margin-top:12px;padding-top:12px"><div style="font-weight:600;margin-bottom:6px">Riasztásnapló</div>
    <pre style="background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:10px;font-size:11px;max-height:180px;overflow:auto;white-space:pre-wrap">${esc(lg.log||'(még nincs riasztás)')}</pre></div>`);
  loadNet();
}
// ---- Internetkapcsolat figyelése (1.5.5): a megadott címek SORBAN, az első válasz = van internet; ha egyik sem
//      válaszol (30 mp múlva még egyszer próbálva), kiesés-riasztás - a Központ a visszatérés után e-mailben is jelzi.
async function loadNet(){const box=document.getElementById('netBox');if(!box)return;const n=await api('netwatch',{method:'POST',body:'{}'});if(!n)return;
  const e=window.ROLE>=1,st=!n.enabled?'<span class="badge">kikapcsolva</span>':n.online===false?`<span class="badge upd" style="color:var(--err)">NINCS INTERNET ${esc(n.since||'')} óta</span>`
    :n.online?`<span class="badge ok">rendben</span>`:'<span class="badge">még nincs ellenőrzés</span>';
  box.innerHTML=`<div style="font-weight:600;margin-bottom:6px">Internetkapcsolat figyelése ${st}</div>
    <div style="font-size:12px;color:var(--muted);margin-bottom:8px">A szerver a megadott címeket sorban pingeli: ha az első válaszol, van internet (a többit nem kérdezi); ha nem, a következőt próbálja.
      Ha egyik sem válaszol, fél perc múlva még egyszer végigpróbálja, és csak utána riaszt. Kiesés alatt percenként ellenőriz, hogy a visszatérés ideje pontos legyen.
      IP-címet adj meg (pl. 1.1.1.1, 8.8.8.8, 9.9.9.9, vagy a szolgáltató átjárója) – a gépnévhez DNS kellene, ami kiesésnél maga sem működik.</div>
    ${n.enabled&&n.lastCheck?`<div style="font-size:12px;margin-bottom:8px">Utolsó ellenőrzés: ${esc(n.lastCheck)}${n.online?` · válaszolt: <b>${esc(n.target)}</b> (${n.rtt} ms)`:''}${(n.noReply||[]).length?` · nem válaszolt: ${esc(n.noReply.join(', '))}`:''}</div>`:''}
    <label style="display:flex;gap:6px;align-items:center;margin:0 0 8px"><input type="checkbox" id="nwOn" ${n.enabled?'checked':''} ${e?'':'disabled'}> Figyelés bekapcsolva</label>
    <label style="margin:0 0 4px">Pingelendő címek, sorrendben (soronként vagy vesszővel)</label>
    <textarea id="nwT" rows="3" style="width:100%;background:var(--panel);border:1px solid var(--line);border-radius:8px;color:var(--ink);padding:8px;font-family:Consolas,monospace" ${e?'':'disabled'} placeholder="1.1.1.1&#10;8.8.8.8&#10;9.9.9.9">${esc((n.targets||[]).join('\n'))}</textarea>
    <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-top:8px">Ellenőrzés
      <select id="nwM" class="grpsel" ${e?'':'disabled'}>${[1,2,5,10,15,30,60].map(m=>`<option value="${m}" ${m===n.minutes?'selected':''}>${m} percenként</option>`).join('')}</select>
      ${e?'<button class="btn small primary" onclick="saveNet()">Mentés</button><button class="btn small" onclick="testNet()">Próba most</button>':''}<span id="nwRes" style="font-size:12px"></span></div>
    ${(n.outages||[]).length?`<div style="font-weight:600;margin:12px 0 6px;font-size:13px">Legutóbbi kiesések</div><table class="upd-table"><tr><th>Kezdete</th><th>Vége</th><th>Időtartam</th></tr>
      ${n.outages.map(o=>`<tr><td>${esc(o.start)}</td><td>${o.end?esc(o.end):'<span style="color:var(--err)">még tart</span>'}</td><td>${o.minutes} perc</td></tr>`).join('')}</table>`:''}`;}
// ---- Sérülékenységek (1.5.6): a Központtól kapott CVE-találatok a gépre (WebUi.CveJs).
async function showCve(agentId){const a=agents.find(x=>x.agentId===agentId);
  const d=await api('cve',{method:'POST',body:JSON.stringify({agentId})});if(!d)return;
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Sérülékenységek${a?' – '+esc(a.machineName):''}</h2>
    <span class="sub">programok, driverek, BIOS – ${d.checkedAt?'utolsó ellenőrzés a Központban: '+esc(d.checkedAt):'még nincs ellenőrzés'}</span></div>
    <div class="page-body"><div class="page-main"><div class="view" style="max-width:1300px">
      ${d.hub?'':'<div class="empty" style="margin-bottom:12px">A sérülékenység-keresést a Központ (Fleet HUB) végzi: ehhez a szervert a Beállításokban a Központhoz kell kapcsolni.</div>'}
      ${cveSum(d)}${cveTable(d.findings)}
      <div style="font-size:12px;color:var(--muted);margin-top:10px">${esc(CVE_NOTE)}</div></div></div></div>`);}
// ---- Webes admin terminál (1.5.9): PowerShell a gépen, LocalSystemként; csak admin, csak engedélyezett gépen. A parancs és
//      a kimenet az aláírt feladat-/eredmény-csatornán megy; a gép a nyitott terminál alatt 2 mp-enként tárcsáz be.
let _termA='',_termHist=[],_termBusy=false,_termTimer=null;
async function showTerminal(agentId,machine){_termA=agentId;_termHist=[];const a=agents.find(x=>x.agentId===agentId);const on=a&&a.terminal;
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Terminál – ${esc(machine)}</h2>
    <span class="sub">PowerShell a gépen, rendszergazdai (LocalSystem) joggal · minden parancs a műveleti naplóba kerül</span>
    <label style="display:flex;align-items:center;gap:6px;font-size:13px"><input type="checkbox" id="termOn" ${on?'checked':''} onchange="termEnable()"> Engedélyezve ezen a gépen</label></div>
    <div class="page-body"><div class="page-main"><div class="view" style="max-width:1300px;display:flex;flex-direction:column;height:calc(100vh - 150px)">
      <div style="background:rgba(224,165,43,.1);border:1px solid rgba(224,165,43,.35);border-radius:9px;padding:8px 12px;margin-bottom:10px;font-size:12px">
        Figyelem: a parancsok teljes rendszergazdai joggal futnak a gépen. A válasz a következő betárcsázáskor jön (nyitott terminálnál kb. 2 mp).</div>
      <div id="termOut" class="mono" style="flex:1;overflow:auto;background:#0a0c10;border:1px solid var(--line);border-radius:9px;padding:12px;font-size:12.5px;white-space:pre-wrap;word-break:break-all"></div>
      <form onsubmit="termSend();return false" style="display:flex;gap:8px;margin-top:10px">
        <span class="mono" style="color:var(--accent);align-self:center">PS&gt;</span>
        <input id="termIn" class="mono" autocomplete="off" placeholder="PowerShell-parancs… (pl. Get-Service | ? Status -eq Running)" style="flex:1;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink);padding:9px 12px;font-size:12.5px" ${on?'':'disabled'}>
        <button class="btn small primary" id="termBtn" ${on?'':'disabled'}>Futtat</button></form>
      <div id="termMsg" style="font-size:12px;color:var(--muted);margin-top:6px">${on?'':'A terminál ezen a gépen ki van kapcsolva – kapcsold be a fenti jelölővel.'}</div>
    </div></div></div>`);
  termAppend('sys','Terminál – '+machine+'. '+(on?'Írd be a parancsot, Enter a futtatás.':'Kapcsold be a használathoz.'));
  setTimeout(()=>{const i=document.getElementById('termIn');if(i&&on)i.focus();},50);}
function termAppend(kind,text){const o=document.getElementById('termOut');if(!o)return;const col=kind==='cmd'?'var(--accent)':kind==='err'?'var(--err)':kind==='sys'?'var(--muted)':'var(--ink)';
  const d=document.createElement('div');d.style.color=col;if(kind==='cmd')d.style.fontWeight='600';d.textContent=(kind==='cmd'?'PS> ':'')+text;o.appendChild(d);o.scrollTop=o.scrollHeight;}
async function termEnable(){const on=document.getElementById('termOn').checked;
  const r=await api('terminal-enable',{method:'POST',body:JSON.stringify({agentId:_termA,on})});
  if(r&&r.ok){const a=agents.find(x=>x.agentId===_termA);if(a)a.terminal=on;
    document.getElementById('termIn').disabled=!on;document.getElementById('termBtn').disabled=!on;
    document.getElementById('termMsg').textContent=on?'Bekapcsolva. A gép a következő betárcsázáskor veszi át.':'Kikapcsolva.';
    termAppend('sys',on?'A terminál engedélyezve ezen a gépen.':'A terminál letiltva.');toast(on?'Engedélyezve':'Letiltva','ok');if(on)document.getElementById('termIn').focus();}
  else{document.getElementById('termOn').checked=!on;toast((r&&r.error)||'Nem sikerült','err');}}
async function termSend(){if(_termBusy)return;const i=document.getElementById('termIn');const cmd=i.value.trim();if(!cmd)return;
  termAppend('cmd',cmd);i.value='';_termBusy=true;document.getElementById('termBtn').disabled=true;
  const msg=document.getElementById('termMsg');msg.textContent='Fut…';
  const r=await api('terminal',{method:'POST',body:JSON.stringify({agentId:_termA,command:cmd})});
  if(!r||!r.ok){termAppend('err',(r&&r.error)||'Nem sikerült kiadni.');_termBusy=false;document.getElementById('termBtn').disabled=false;msg.textContent='';return;}
  if(!r.online)msg.textContent='A gép épp nem jelentkezik – a parancs vár, amíg betárcsáz…';
  termPoll(r.taskId,0);}
async function termPoll(taskId,tries){if(_termA===''){_termBusy=false;return;}
  if(tries>150){termAppend('err','(időtúllépés – a gép nem válaszolt)');_termDone();return;}
  const r=await api('terminal-result',{method:'POST',body:JSON.stringify({agentId:_termA,taskId})});
  if(r&&r.ok&&r.done){termAppend(r.success?'out':'err',(r.output||'(nincs kimenet)').replace(/\s+$/,''));if(!r.success)termAppend('sys','(kilépési kód: '+r.exit+')');_termDone();return;}
  _termTimer=setTimeout(()=>termPoll(taskId,tries+1),2000);}
function _termDone(){_termBusy=false;const b=document.getElementById('termBtn');if(b)b.disabled=false;const m=document.getElementById('termMsg');if(m)m.textContent='';const i=document.getElementById('termIn');if(i)i.focus();}
// ---- Másolás külső eszközre (1.5.6): az agentek USB-naplója - gépenként vagy összesen (kezelő / admin; személyes adat).
let _usbA='',_usbQ='',_usbP=0,_usbT=null;
const USB_KIND={connect:'csatlakoztatva',disconnect:'leválasztva',write:'fájl írása',rename:'átnevezés',delete:'törlés',overflow:'túl sok esemény'};
async function showUsb(agentId){_usbA=agentId||'';_usbQ='';_usbP=0;const a=agents.find(x=>x.agentId===_usbA);
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Másolás külső eszközre${a?' – '+esc(a.machineName):''}</h2>
    <span class="sub">USB / cserélhető meghajtók: csatlakoztatás, írt / átnevezett / törölt fájlok, a bejelentkezett felhasználóval</span></div>
    <div class="page-body"><div class="page-main"><div class="view" style="max-width:1200px">
      <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:10px"><input id="usbQ" class="search" style="width:320px;max-width:100%" placeholder="Keresés: fájlnév, felhasználó, meghajtó, gép…"
        oninput="clearTimeout(_usbT);_usbT=setTimeout(()=>{_usbQ=document.getElementById('usbQ').value.trim();_usbP=0;usbRows();},300)">
        ${_usbA?'<button class="btn small" onclick="showUsb(\'\')">Minden gép</button>':''}<button class="btn small" onclick="usbRows()">Frissítés</button></div>
      <div style="font-size:12px;color:var(--muted);margin-bottom:8px">A naplózás a Monitorozás → Mit mérjen? → „Másolás külső eszközre” kapcsolóval indul (alapból ki). Munkavállalói megfigyelés: a dolgozókat tájékoztatni kell (GDPR).</div>
      <div id="usbRows"><div class="empty">Betöltés…</div></div></div></div></div>`);
  usbRows();}
async function usbRows(){const d=await api('usb-log',{method:'POST',body:JSON.stringify({agentId:_usbA,q:_usbQ,page:_usbP})});const box=document.getElementById('usbRows');if(!box||!d)return;
  if(d.error){box.innerHTML=`<div class="empty">${esc(d.error)}</div>`;return;}
  if(!d.items.length){box.innerHTML=`<div class="empty">${_usbQ?'Nincs találat.':'Még nincs esemény.'}</div>`;return;}
  const pages=Math.max(1,Math.ceil(d.total/d.size)),sz=b=>b>=1073741824?(b/1073741824).toFixed(1)+' GB':b>=1048576?(b/1048576).toFixed(1)+' MB':b>=1024?Math.round(b/1024)+' KB':b?b+' B':'';
  const pager=pages>1?`<div style="display:flex;gap:8px;align-items:center;margin:8px 0"><button class="btn small" ${_usbP<=0?'disabled':''} onclick="_usbP--;usbRows()">‹ Előző</button>
    <span style="font-size:12px;color:var(--muted)">${_usbP+1}. / ${pages}</span><button class="btn small" ${_usbP>=pages-1?'disabled':''} onclick="_usbP++;usbRows()">Következő ›</button></div>`:'';
  box.innerHTML=`<div style="font-size:12px;color:var(--muted);margin-bottom:6px">${d.total} esemény (a legutóbbi 3000; minden a naplómappa usb.log fájljában)</div>${pager}
    <table class="upd-table"><tr><th>Idő</th>${_usbA?'':'<th>Gép</th>'}<th>Esemény</th><th>Meghajtó</th><th>Fájl</th><th style="text-align:right">Méret</th><th>Felhasználó</th></tr>
    ${d.items.map(e=>`<tr><td style="white-space:nowrap">${esc(e.at)}</td>${_usbA?'':`<td>${esc(e.machine)}</td>`}
      <td style="white-space:nowrap">${e.kind==='connect'||e.kind==='disconnect'?'<b>':''}${esc(USB_KIND[e.kind]||e.kind)}${e.kind==='connect'||e.kind==='disconnect'?'</b>':''}</td>
      <td style="white-space:nowrap">${esc(e.drive)} ${esc(e.label)}<div class="mono" style="font-size:10px">${esc(e.serial)}</div></td>
      <td style="word-break:break-all">${esc(e.path)}${e.oldPath?`<div style="font-size:11px;color:var(--muted)">régi név: ${esc(e.oldPath)}</div>`:''}</td>
      <td style="text-align:right;white-space:nowrap">${sz(e.size)}</td><td>${esc(e.user)}</td></tr>`).join('')}</table>${pager}`;}
function netTargets(){return document.getElementById('nwT').value.split(/[\s,;]+/).map(x=>x.trim()).filter(x=>x);}
async function saveNet(){const r=await api('netwatch-save',{method:'POST',body:JSON.stringify({enabled:document.getElementById('nwOn').checked,targets:netTargets(),minutes:parseInt(document.getElementById('nwM').value)||5})});
  if(r&&r.ok){toast('Internetfigyelés mentve','ok');loadNet();}else toast((r&&r.error)||'Nem sikerült','err');}
async function testNet(){const res=document.getElementById('nwRes');uiBusy(res,'Pingelés…');
  const r=await api('netwatch-test',{method:'POST',body:JSON.stringify({targets:netTargets()})});
  if(!r||!r.ok){res.innerHTML=`<span style="color:var(--err)">${esc((r&&r.error)||'Nem sikerült')}</span>`;return;}
  res.innerHTML=r.results.map(x=>x.rtt!=null?`<span style="color:var(--ok)">${esc(x.target)}: ${x.rtt} ms</span>`:`<span style="color:var(--err)">${esc(x.target)}: nincs válasz</span>`).join(' · ');}
async function saveOffline(){
  const groups=[...document.querySelectorAll('.offGrp:checked')].map(c=>window._offGroups[+c.value]);
  const r=await api('offline-save',{method:'POST',body:JSON.stringify({minutes:parseInt(document.getElementById('offMin').value)||0,groups})});
  if(r&&r.ok){toast(r.minutes>0?`Kiesés-riasztás: ${r.minutes} perc`:'Kiesés-riasztás kikapcsolva','ok');showAlerts();}else toast((r&&r.error)||'Hiba','err');
}
function quickAlert(key,label){ruEdit(null,{key,label,agentId:monAgent});}

// ---------------- biztonság: kulcsok, tokenek, műveleti napló – teljes oldalon (WebUi.KeysJs) ----------------
async function rotateKeys(){
  if(!await uiConfirm('Biztosan cseréled most a szerver kulcsát? A gépek a következő jelentkezéskor ellenőrzötten átállnak (előtte mentés készül).'))return;
  const r=await api('rotate-keys',{method:'POST',body:'{}'});
  if(r&&r.ok){toast('Kulcs cserélve – új ujjlenyomat: '+r.fingerprint.slice(0,12)+'…','ok');showSecurity('own');}
  else toast((r&&r.error)||'Nem sikerült','err');
}

// ---------------- C) gépkezelés: telepítés, Windows Update, csoportok, szabályok ----------------
let _gl=[];            // aktuális csoportnév-lista (index alapján hivatkozunk rá)
async function task(body,okMsg){
  const r=await api('task',{method:'POST',body:JSON.stringify(body)});
  if(r&&r.ok){toast((okMsg||'Feladat kiadva')+` – ${r.machines} gép, a következő jelentkezéskor`,'ok');return true;}
  toast((r&&r.error)||'Nem sikerült','err');return false;
}
function allGroups(){return [...new Set(agents.flatMap(a=>a.groups||[]))].sort((x,y)=>x.localeCompare(y,'hu'));}
// Az agent verziója a szerverbe épített agenthez képest: miért nem frissül, és kézi „frissítés most” (1.4.7).
function verNewer(a,b){const p=x=>String(x||'0').split('.').map(n=>parseInt(n)||0),x=p(a),y=p(b);for(let i=0;i<4;i++){if((x[i]||0)!==(y[i]||0))return (x[i]||0)>(y[i]||0);}return false;}
function agentUpdBox(a){const k=window._pkg||{};
  // A legutóbbi frissülés (honnan, hová, mikor) - hogy a kért frissítés megtörténte is látsszon.
  const done=a.lastUpdate?`<div style="background:rgba(49,192,122,.1);border:1px solid rgba(49,192,122,.35);border-radius:9px;padding:8px 12px;margin:10px 0;font-size:13px">✓ Az agent frissült: <b>${esc(a.lastUpdate.from)}</b> → <b>${esc(a.lastUpdate.to)}</b> (${esc(a.lastUpdate.at)})</div>`:'';
  if(a.revoked||!k.v||!verNewer(k.v,a.agentVersion))return done;
  return done+`<div style="background:rgba(224,165,43,.1);border:1px solid rgba(224,165,43,.35);border-radius:9px;padding:10px 12px;margin:10px 0;font-size:13px">
    Agent <b>${esc(a.agentVersion||'?')}</b> – a szerverbe épített: <b>${esc(k.v)}</b>. ${k.au?'Az automatikus frissítés be van kapcsolva: a gép a következő jelentkezéskor megkapja (ha nem frissül, a gépen az agent.log mutatja az okát).'
      :'<span style="color:var(--warn)">Az automatikus agent-frissítés KI van kapcsolva</span> (Beállítások, vagy a Központból).'}
    ${a.forceUpdate?'<div style="margin-top:6px;color:var(--warn)">Frissítés kérve – a gép a következő jelentkezéskor (legfeljebb 5 perc) frissül.</div>':''}
    ${window.ROLE>=1&&!a.forceUpdate?`<div style="margin-top:8px"><button class="btn small primary" onclick="agentUpdNow('${a.agentId}')">Agent frissítése most</button></div>`:''}</div>`;}
async function agentUpdNow(id){const r=await api('agent-update',{method:'POST',body:JSON.stringify({agentId:id})});
  if(r&&r.ok)toast('Kérve – a gép a következő jelentkezéskor frissül','ok');else toast((r&&r.error)||'Nem sikerült','err');}
function detailExtra(a){
  let h=agentUpdBox(a);
  const g=(a.groups||[]).map(x=>`<span class="badge ok" style="margin-right:4px">${esc(x)}</span>`).join('')||'<span style="color:var(--muted)">nincs csoport</span>';
  h+=`<div style="margin:10px 0;font-size:13px">Csoportok: ${g} ${window.ROLE>=1?`<button class="btn small" style="padding:2px 8px;font-size:11px" onclick="editGroups('${a.agentId}')">módosítás</button>`:''}</div>`;
  if(window.ROLE>=1) h+=`<div class="detail-actions" style="margin:10px 0">
      <button class="btn small" onclick="showInstall('${a.agentId}')">Program telepítése…</button>
      <button class="btn small" onclick="showUninstall('${a.agentId}')">Program eltávolítása…</button>
      <button class="btn small danger" onclick="showReboot({agentIds:['${a.agentId}']},'${esc(a.machineName)}')">Újraindítás…</button></div>`;
  const wu=a.windowsUpdates||[];
  h+=`<div style="border-top:1px solid var(--line);margin-top:14px;padding-top:12px">
    <div style="display:flex;justify-content:space-between;align-items:center"><div style="font-weight:600">Windows Update</div>
    ${a.pendingReboot?'<span class="badge upd">újraindításra vár</span>':''}</div>
    <div style="font-size:12px;color:var(--muted);margin:4px 0 8px">Forrás: ${esc(a.updateSource||'–')} · utolsó keresés: ${esc(a.wuScanned||'még nem')}${a.wuError?` · <span style="color:var(--err)">${esc(a.wuError)}</span>`:''}</div>`;
  if(wu.length){
    h+=`<table class="upd-table"><tr><th style="width:20px"></th><th>Frissítés</th><th>Súlyosság</th><th style="text-align:right">MB</th></tr>`+
      wu.map(w=>`<tr><td><input type="checkbox" class="wuchk" data-id="${esc(w.updateId)}" checked></td>
        <td>${esc(w.kb?w.kb+' – ':'')}${esc(w.title)}${w.rebootRequired?' <span style="color:var(--warn);font-size:11px">(újraindítást igényel)</span>':''}</td>
        <td>${esc(w.severity||'–')}</td><td style="text-align:right">${w.sizeMb||''}</td></tr>`).join('')+`</table>`;
  } else h+=`<div class="empty">${a.wuScanned?'✓ Nincs függő Windows-frissítés.':'Még nincs keresési eredmény.'}</div>`;
  if(window.ROLE>=1) h+=`<div class="detail-actions" style="margin-top:8px">
      ${wu.length?`<button class="btn small primary" onclick="wuSelected('${a.agentId}')">Kijelöltek telepítése</button>
      <button class="btn small" onclick="task({agentIds:['${a.agentId}'],kind:'wuinstall',target:'all'},'Windows-frissítés')">Összes telepítése</button>`:''}
      <button class="btn small" onclick="task({agentIds:['${a.agentId}'],kind:'wuscan'},'Keresés')">Keresés most</button></div>
      <div style="font-size:11px;color:var(--muted);margin-top:6px">Újraindítás csak külön, a te döntésedre történik.</div>`;
  h+='</div>'+topProcs(a.topProcesses);
  return h;
}
// A legtöbb memóriát / CPU-t használó folyamatok (1.5.0-s agenttől, jelentkezéskor frissül).
function topProcs(tp){if(!tp||!tp.length)return '';
  return `<div style="border-top:1px solid var(--line);margin-top:14px;padding-top:12px"><div style="font-weight:600;margin-bottom:6px">Legtöbb erőforrást használó folyamatok</div>
    <table class="upd-table"><tr><th>Folyamat</th><th style="text-align:right">Memória</th><th style="text-align:right">CPU</th><th style="text-align:right">Leíró / szál</th></tr>
    ${tp.slice().sort((x,y)=>y.memMb-x.memMb).map(p=>`<tr><td>${esc(p.name)}${p.count>1?` <span style="color:var(--muted);font-size:11px">×${p.count}</span>`:''}</td>
      <td style="text-align:right">${(+p.memMb).toLocaleString('hu-HU',{maximumFractionDigits:0})} MB</td><td style="text-align:right">${(+p.cpuPct).toLocaleString('hu-HU',{maximumFractionDigits:1})} %</td>
      <td style="text-align:right;color:var(--muted)">${p.handles} / ${p.threads}</td></tr>`).join('')}</table></div>`;}
function wuSelected(id){
  const ids=[...document.querySelectorAll('.wuchk:checked')].map(c=>c.dataset.id);
  if(!ids.length){toast('Jelölj ki frissítést','err');return;}
  task({agentIds:[id],kind:'wuinstall',target:ids.join(',')},'Windows-frissítés');
}
function editGroups(id){
  const a=agents.find(x=>x.agentId===id);
  openModal(`<h3>Csoportok – ${esc(a.machineName)}</h3>
    <label>Csoportok vesszővel elválasztva (pl. Könyvelés, Szerverek)</label>
    <input id="grpIn" value="${esc((a.groups||[]).join(', '))}">
    <div style="font-size:12px;color:var(--muted);margin-top:6px">Meglévők: ${allGroups().map(esc).join(', ')||'–'}</div>
    <button class="btn primary small" style="margin-top:12px" onclick="saveGroups('${id}')">Mentés</button>`);
}
async function saveGroups(id){
  const groups=document.getElementById('grpIn').value.split(',').map(x=>x.trim()).filter(Boolean);
  const r=await api('agent-groups',{method:'POST',body:JSON.stringify({agentId:id,groups})});
  if(r&&r.ok){toast('Csoportok mentve','ok');closeModal();refresh();}else toast('Nem sikerült','err');
}
async function showInstall(id,group){
  // Alkalmazáscsomag vagy program a programtárból (WebUi.CatalogJs) - azonosítót nem kell beírni.
  const who=group!=null?`a(z) „${esc(_gl[group])}” csoportra`:esc((agents.find(x=>x.agentId===id)||{}).machineName||'');
  const tgt=group!=null?{group:_gl[group]}:{agentIds:[id]};
  installPicker(`Program telepítése – ${who}`,(kind,target,label)=>
    task(Object.assign({},tgt,{kind,target}),(kind==='installbundle'?'Csomag: ':'Telepítés: ')+label));
}
function tgtOf(el){return el.dataset.group!=null&&el.dataset.group!==undefined&&el.dataset.group!==''?{group:_gl[+el.dataset.group]}:{agentIds:[el.dataset.agent]};}
function doInstall(el,free){
  const id=free?document.getElementById('instId').value.trim():el.dataset.id;
  if(!id){toast('Adj meg azonosítót','err');return;}
  task(Object.assign(tgtOf(el),{kind:'install',target:id}),'Telepítés');
}
function doBase(el){task(Object.assign(tgtOf(el),{kind:'installbase'}),'Alapcsomag');}
async function showUninstall(id){
  const a=agents.find(x=>x.agentId===id); if(!a)return;
  const d=await api('agent-installed',{method:'POST',body:JSON.stringify({agentId:id})}); const list=(d&&d.installed)||[];
  openModal(`<h3>Program eltávolítása – ${esc(a.machineName)}</h3>
    <input id="unFlt" placeholder="Szűrés…" oninput="fltUn()" style="margin-bottom:8px">
    <div id="unList" style="max-height:340px;overflow:auto">${list.length?list.map(i=>`<div class="unrow" data-n="${esc((i.name+' '+i.id).toLowerCase())}" style="display:flex;justify-content:space-between;align-items:center;padding:6px 0;border-bottom:1px solid var(--line)">
      <span>${esc(i.name)} <span style="font-size:11px;color:var(--muted)">${esc(i.version)} · ${esc(i.source||'rendszer')}</span></span>
      <button class="btn small danger" data-agent="${id}" data-id="${esc(i.id)}" data-name="${esc(i.name)}" onclick="doUninstall(this)">Eltávolítás</button></div>`).join('')
      :'<div class="empty">Még nincs programlista – a következő jelentkezés után jelenik meg.</div>'}</div>`);
}
function fltUn(){const q=document.getElementById('unFlt').value.toLowerCase();document.querySelectorAll('.unrow').forEach(r=>r.style.display=r.dataset.n.includes(q)?'':'none');}
async function doUninstall(el){
  if(!await uiConfirm('Biztosan eltávolítod: '+el.dataset.name+'?'))return;
  task({agentIds:[el.dataset.agent],kind:'uninstall',target:el.dataset.id},'Eltávolítás');
}
function showReboot(target,label){
  window._rbTarget=target;
  openModal(`<h3>Újraindítás – ${esc(label)}</h3>
    <p class="muted">A felhasználó figyelmeztetést kap, és addig mentheti a munkáját.</p>
    <label>Mikor</label>
    <select id="rbDelay" style="width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">
      <option value="now">azonnal (figyelmeztetés nélkül!)</option><option value="60">1 perc múlva</option><option value="300" selected>5 perc múlva</option><option value="900">15 perc múlva</option><option value="3600">1 óra múlva</option></select>
    <button class="btn small danger" style="margin-top:12px" onclick="doReboot()">Újraindítás</button>`);
}
async function doReboot(){
  const v=document.getElementById('rbDelay').value,now=v==='now';
  if(!await uiConfirm(now?'AZONNALI újraindítás – a felhasználó NEM kap figyelmeztetést, a nem mentett munka elveszhet. Biztos?':'Biztosan újraindítod?'))return;
  task(Object.assign({},window._rbTarget,now?{kind:'rebootnow'}:{kind:'reboot',target:v}),'Újraindítás').then(ok=>{if(ok)closeModal();});
}
// 1.5.10: szolgáltatás-kezelő panel. A lista a gép által felküldött szolgáltatásokból (1.5.1-es agenttől); a
// vezérlés (indít/leállít/újraindít) admin jog (ROLE>=2), a szerver aláírt feladatként adja ki az agentnek.
async function showServices(id,label){
  window._svcAgent=id;
  openModal(`<h3>Szolgáltatások – ${esc(label)}</h3><div id="svcBody" class="muted">Betöltés…</div>`);
  await loadServices();
}
async function loadServices(){
  const d=await api('machine-services',{method:'POST',body:JSON.stringify({agentId:window._svcAgent})});
  const body=document.getElementById('svcBody'); if(!body)return;
  if(!d||!d.ok){body.innerHTML=`<div class="empty">${esc((d&&d.error)||'Nem sikerült')}</div>`;return;}
  window._svcList=d.services||[];
  if(!d.reported){body.innerHTML='<div class="empty">A gép még nem küldött szolgáltatáslistát (az 1.5.1-es agent küldi, a következő jelentkezéskor).</div>';return;}
  const admin=window.ROLE>=2;
  body.innerHTML=`<div style="font-size:12px;color:var(--muted);margin-bottom:8px">${window._svcList.length} szolgáltatás · a művelet a gép következő jelentkezésekor fut le (legfeljebb pár perc).${admin?'':' <b>A vezérléshez admin jog kell.</b>'}</div>
    <input id="svcQ" placeholder="szűrés név szerint…" oninput="svcRender()" autocomplete="off" style="width:100%;padding:9px 12px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink);margin-bottom:8px">
    <div id="svcRows" style="max-height:52vh;overflow:auto"></div>`;
  svcRender();
}
function svcRender(){
  const q=((document.getElementById('svcQ')||{}).value||'').trim().toLowerCase();
  const admin=window.ROLE>=2, list=window._svcList||[];
  const rows=list.filter(s=>!q||(s.name+' '+(s.displayName||'')).toLowerCase().includes(q)).map(s=>{
    const dot=s.running?'<span class="badge ok">fut</span>':'<span class="badge">leállt</span>';
    const ctl=admin?`<span style="display:flex;gap:4px;flex:0 0 auto">
        <button class="btn small" ${s.running?'disabled':''} onclick="svcDo('${esc(s.name)}','servicestart')">Indít</button>
        <button class="btn small" ${s.running?'':'disabled'} onclick="svcDo('${esc(s.name)}','servicestop')">Leállít</button>
        <button class="btn small primary" onclick="svcDo('${esc(s.name)}','servicerestart')">Újraindít</button></span>`:'';
    return `<div style="display:flex;justify-content:space-between;align-items:center;gap:10px;padding:7px 2px;border-bottom:1px solid var(--line)">
      <div style="min-width:0"><div>${esc(s.displayName||s.name)} ${dot} ${s.auto?'<span class="badge" style="font-size:10px">auto</span>':''}</div>
      <div class="mono" style="font-size:11px;color:var(--muted)">${esc(s.name)}</div></div>${ctl}</div>`;
  }).join('')||'<div class="empty">Nincs a szűrőnek megfelelő szolgáltatás.</div>';
  document.getElementById('svcRows').innerHTML=rows;
}
async function svcDo(name,kind){
  const verb=kind==='servicestart'?'indítása':kind==='servicestop'?'leállítása':'újraindítása';
  if(kind!=='servicestart' && !await uiConfirm(`A(z) „${name}” szolgáltatás ${verb} – biztos?`))return;
  await task({agentIds:[window._svcAgent],kind:kind,target:name},`Szolgáltatás ${verb}`);
}
// A Csoportok oldal közös (WebUi.GroupsJs); a csoportra kiadható műveletek ezen az oldalon (i: a csoport indexe a _gl-ben).
function grActions(i){return `<button class="btn small" onclick="task({group:_gl[${i}],kind:'upgradeall'},'Programfrissítés')">Programok</button>
  <button class="btn small" onclick="task({group:_gl[${i}],kind:'wuinstall',target:'all'},'Windows-frissítés')">Windows</button>
  <button class="btn small" onclick="showInstall(null,${i})">Telepítés…</button>
  <button class="btn small danger" onclick="showReboot({group:_gl[${i}]},_gl[${i}])">Újraindítás…</button>`;}
// (a programtár és a csomagok kódja: WebUi.CatalogJs - közös a Központtal)
const DAYS=['V','H','K','Sze','Cs','P','Szo'];
async function showPolicies(){
  const d=(await api('policies',{method:'POST',body:'{}'}))||{policies:[],groups:[]};
  window._pol=d.policies||[]; window._polGroups=d.groups||[];
  const rows=window._pol.map((p,k)=>`<tr><td><b>${esc(p.name)}</b>${p.enabled?'':' <span style="color:var(--muted)">(kikapcsolva)</span>'}<br>
    <span style="font-size:12px;color:var(--muted)">${p.group?esc(p.group):'minden gép'} · ${[p.apps?'programok':'',p.windows?'Windows':'',p.reboot?'újraindítás':''].filter(Boolean).join(', ')} ·
    ${[1,2,3,4,5,6,0].filter(x=>p.days.includes(x)).map(x=>DAYS[x]).join(' ')} ${p.startHour}:00–${p.endHour}:00</span></td>
    <td style="text-align:right">${window.ROLE>=1?`<button class="btn small" onclick="editPolicy(${k})">Szerkesztés</button>
      <button class="btn small danger" onclick="delPolicy(${k})">Törlés</button>`:''}</td></tr>`).join('');
  openView(`<h3>Automatikus frissítési szabályok</h3>
    <p class="muted" style="font-size:12px">Alapból semmi nem automatikus. Egy szabály a karbantartási ablakban (a szerver helyi ideje szerint)
      egyszer kiadja a frissítést a hatókörébe tartozó gépeknek. Újraindítás csak ha bekapcsolod ÉS a gép jelzi, hogy arra vár.</p>
    <table class="upd-table">${rows||'<tr><td class="muted">Még nincs szabály.</td></tr>'}</table>
    ${window.ROLE>=1?'<button class="btn small primary" style="margin-top:10px" onclick="editPolicy(-1)">+ Új szabály</button>':''}
    ${(d.hubPolicies||[]).length?`<div style="font-weight:600;margin:14px 0 6px">A Központtól (itt csak olvasható)</div><table class="upd-table">${d.hubPolicies.map(p=>`<tr><td><b>${esc(p.name)}</b>${p.enabled?'':' (kikapcsolva)'}<br>
      <span style="font-size:12px;color:var(--muted)">${p.group?esc(p.group):'minden gép'} · ${[p.apps?'programok':'',p.windows?'Windows':'',p.reboot?'újraindítás':''].filter(Boolean).join(', ')} ·
      ${[1,2,3,4,5,6,0].filter(x=>p.days.includes(x)).map(x=>DAYS[x]).join(' ')} ${p.startHour}:00–${p.endHour}:00</span></td></tr>`).join('')}</table>`:''}`);
}
function editPolicy(k){
  const p=k>=0?window._pol[k]:{id:'',name:'',group:'',apps:true,windows:false,reboot:false,days:[1,2,3,4,5],startHour:22,endHour:4,enabled:true};
  window._pEdit=p;
  const opts=['<option value="">Minden gép</option>'].concat(window._polGroups.map((g,i)=>`<option value="${i}" ${g===p.group?'selected':''}>${esc(g)}</option>`)).join('');
  const hours=h=>Array.from({length:24},(_,i)=>`<option ${i===h?'selected':''}>${i}</option>`).join('');
  const cb=(id,l,on)=>`<label style="display:flex;align-items:center;gap:8px;margin:6px 0"><input type="checkbox" id="${id}" ${on?'checked':''}> ${l}</label>`;
  openModal(`<h3>${k>=0?'Szabály szerkesztése':'Új szabály'}</h3>
    <label>Név</label><input id="pName" value="${esc(p.name)}" placeholder="pl. Éjszakai frissítés">
    <label>Hatókör</label><select id="pGroup" style="width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">${opts}</select>
    ${cb('pApps','Programok frissítése (winget)',p.apps)}${cb('pWin','Windows-frissítések telepítése',p.windows)}${cb('pReb','Újraindítás, ha a gép arra vár',p.reboot)}
    <label>Napok</label><div style="display:flex;gap:10px;flex-wrap:wrap">${[1,2,3,4,5,6,0].map(x=>`<label style="display:flex;gap:4px;align-items:center"><input type="checkbox" class="pDay" value="${x}" ${p.days.includes(x)?'checked':''}>${DAYS[x]}</label>`).join('')}</div>
    <div class="row"><div class="col"><label>Kezdete (óra)</label><select id="pStart" style="width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">${hours(p.startHour)}</select></div>
      <div class="col"><label>Vége (óra)</label><select id="pEnd" style="width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">${hours(p.endHour)}</select></div></div>
    ${cb('pOn','Bekapcsolva',p.enabled)}
    <button class="btn primary small" style="margin-top:10px" onclick="savePolicy()">Mentés</button>`);
}
async function savePolicy(){
  const g=document.getElementById('pGroup').value;
  const p=Object.assign({},window._pEdit,{name:pName.value,group:g===''?'':window._polGroups[+g],
    apps:pApps.checked,windows:pWin.checked,reboot:pReb.checked,
    days:[...document.querySelectorAll('.pDay:checked')].map(c=>+c.value),
    startHour:+pStart.value,endHour:+pEnd.value,enabled:pOn.checked});
  const r=await api('policy-save',{method:'POST',body:JSON.stringify(p)});
  if(r&&r.ok){toast('Szabály mentve','ok');showPolicies();}else toast((r&&r.error)||'Nem sikerült','err');
}
async function delPolicy(k){if(!await uiConfirm('Törlöd a szabályt?'))return;
  const r=await api('policy-delete',{method:'POST',body:JSON.stringify(window._pol[k])});
  if(r&&r.ok){toast('Törölve','ok');showPolicies();}}

refresh();
setInterval(refresh,15000);
""";

    /// <summary>
    /// Teljes oldalas nézet és a monitorozó oldal kategóriánkénti menüvel - közös a FleetServer és a Központ felületén.
    /// Előfeltétel: api(), esc(), closeModal(); az oldalon &lt;div id="page" class="page"&gt;. A „+ küszöb” a hívó oldal
    /// monAlert(key,label) függvényét hívja (ha van). A gépről jövő szövegek (név, mértékegység) mindenhol esc()-en mennek át.
    /// </summary>
    public const string MonitorJs = """
// ---- Teljes oldalas nézet (a menüsáv mellett) – a hosszú tartalom nem szorul modális ablakba
function pageOpen(html){const p=document.getElementById('page');p.innerHTML=html;p.classList.add('show');}
function pageClose(){const p=document.getElementById('page');p.classList.remove('show');p.innerHTML='';_mp=null;_termA='';if(_termTimer){clearTimeout(_termTimer);_termTimer=null;}}
// Menüpont tartalma teljes oldalon (az ablak helyett): a tartalom eleji <h3> lesz a fejléc címe. A részletes szerkesztő
// ablakok (pl. felhasználó módosítása) az oldal fölött nyílnak; mentés után a menüpont újrarajzolása bezárja őket.
function openView(html){if(typeof closeModal==='function')closeModal();
  const m=html.match(/^\s*<h3[^>]*>([\s\S]*?)<\/h3>/),title=m?m[1]:'',body=m?html.slice(m[0].length):html;
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>${title}</h2><span class="sub"></span></div>
    <div class="page-body"><div class="page-main"><div class="view">${body}</div></div></div>`);}
// Összecsukható oldalsó menü (széles képernyőn): csak az ikonok maradnak; az állapotot a böngésző megjegyzi.
function toggleNav(){const on=document.body.classList.toggle('nav-min');try{localStorage.setItem('navMin',on?'1':'0');}catch(e){}}
try{if(localStorage.getItem('navMin')==='1')document.body.classList.add('nav-min');}catch(e){}
// ---- Monitorozás: bal oldalon kategóriák (Áttekintés, CPU, Memória, Lemez, …), jobbra nagy grafikonok
let _mp=null;
// A nem változó (vagy csak nővekvő) értékek grafikon helyett csempeként az Áttekintésben.
const monStatic=k=>/^(cpu\.(cores|threads|sockets|maxmhz)|mem\.(totalgb|commitlimitgb)|disk\..+\.totalgb|sys\.uptimeh|hw\.driver)$/.test(k);
const MON_KEY=['cpu.load','mem.usedpct','diskio.busypct','net.rxkbps','net.txkbps'];
function monFmt(v,u,key){if(v==null||isNaN(v))return '–';v=+v;
  if(key==='sys.uptimeh'&&v>=48)return (v/24).toLocaleString('hu-HU',{maximumFractionDigits:1})+' nap';
  const a=Math.abs(v);return v.toLocaleString('hu-HU',{maximumFractionDigits:a>=100?0:a>=10?1:2})+(u?' '+u:'');}
async function openMonitorPage(o){
  if(typeof closeModal==='function')closeModal();
  _mp=Object.assign({ranges:[[6,'6 óra'],[24,'24 óra'],[168,'7 nap'],[720,'30 nap']],hours:24,cat:0,keys:[],cats:[]},o);
  pageOpen(`<div class="page-head"><button class="btn small" onclick="monBack()">← Vissza</button><h2>${esc(o.title)}</h2>
    <span class="sub"${o.warn?' style="color:var(--warn)"':''}>${esc(o.warn||o.sub||'')}</span><span id="monRanges" style="display:flex;gap:6px;flex-wrap:wrap"></span>${o.extra||''}
    <button class="btn small" onclick="monReload()">Frissítés</button></div>
    <div class="page-body"><div class="page-menu" id="monMenu"></div><div class="page-main" id="monMain"><div class="empty">Betöltés…</div></div></div>`);
  await monReload();}
async function monReload(){const m=_mp;if(!m)return;const d=await api('metric-keys',{method:'POST',body:JSON.stringify(m.req)});if(_mp!==m)return;
  m.keys=(d&&d.keys)||[];const c={};
  // A SMART-adatok (az állapot is) egy helyre: Lemez-állapot – ott táblázat, nem grafikon (a felhasználó kérése, 1.5.6).
  for(const k of m.keys){if(monStatic(k.key))continue;const n=/^smart\./.test(k.key)?'Lemez-állapot':(k.category||'Egyéb');(c[n]=c[n]||[]).push(k);}
  m.cats=[['Áttekintés',null]].concat(Object.keys(c).map(n=>[n,c[n]]));if(m.cat>=m.cats.length)m.cat=0;monDraw();}
function monBack(){const b=_mp&&_mp.back;pageClose();if(b)b();}
function monCat(i){_mp.cat=i;monDraw();document.getElementById('monMain').scrollTop=0;}
function monSetRange(h){_mp.hours=h;monDraw();}
function monDraw(){const m=_mp;if(!m)return;
  document.getElementById('monRanges').innerHTML=m.ranges.map(r=>`<button class="btn small ${m.hours===r[0]?'primary':''}" onclick="monSetRange(${r[0]})">${r[1]}</button>`).join('');
  document.getElementById('monMenu').innerHTML=m.cats.map((c,i)=>`<button class="${m.cat===i?'sel':''}" onclick="monCat(${i})"><span>${esc(c[0])}</span>${c[1]?`<span class="cnt">${c[1].length}</span>`:''}</button>`).join('');
  const main=document.getElementById('monMain');
  if(!m.keys.length){main.innerHTML='<div class="empty">Erről a gépről még nincs mérési adat – a következő jelentkezések után jelenik meg.</div>';return;}
  let list,h='';
  if(m.cat===0){
    const tiles=m.keys.filter(k=>monStatic(k.key)||MON_KEY.includes(k.key)||/^disk\..+\.usedpct$/.test(k.key));
    h+=`<div class="tiles">${tiles.map(k=>`<div class="tile"><div class="v">${esc(monFmt(k.latest,k.unit,k.key))}</div><div class="l">${esc(k.label||k.key)}</div></div>`).join('')}</div>`;
    list=MON_KEY.map(x=>m.keys.find(k=>k.key===x)).filter(Boolean);
    if(list.length)h+='<div style="font-weight:600;margin:4px 0 10px">Fő mérések</div>';
  } else list=m.cats[m.cat][1];
  if(m.cat>0&&m.cats[m.cat][0]==='Lemez-állapot'){main.innerHTML=monSmart(list);return;}
  const alert=window.ROLE>=1&&typeof monAlert==='function';
  h+=`<div class="graphs">${list.map(k=>{const i=m.keys.indexOf(k);return `<div class="gcard"><div class="gh"><span>${esc(k.label||k.key)}${alert?
      ` <button class="btn small" style="padding:2px 8px;font-size:11px" onclick="monAlertI(${i})">+ küszöb</button>`:''}</span><span class="gv">${esc(monFmt(k.latest,k.unit,k.key))}</span></div>
    <div class="gbox" id="mg-${i}"></div><div class="gstat" id="ms-${i}"></div></div>`;}).join('')}</div>`;
  main.innerHTML=h;
  list.forEach(k=>monGraph(m.keys.indexOf(k)));}
function monAlertI(i){const k=_mp&&_mp.keys[i];if(k)monAlert(k.key,k.label||k.key);}
// ---- SMART lemezenként táblázatban: a lényeg a hátralévő élettartam (231: SSD élettartam, 177: kopáskiegyenlítés, vagy
//      100 − a Windows „wear” értéke); grafikon csak az élettartamhoz. A kritikus értékek (áthelyezett / függő szektorok,
//      javíthatatlan hibák, magas hőmérséklet, kevés élettartam) pirossal / sárgával.
const SMART_ORDER=['health','life','wearlevel','wearpct','temp','poh','cycles','realloc','pending','offunc','crc','readerr','writeerr','readerrtotal','rawreaderr','seekerr','readlatmax','writelatmax','lbaw','lbar'];
function smartLevel(a,v){if(v==null)return'';
  if(a==='health')return v>=2?'err':v>=1?'warn':'ok';
  if(['realloc','pending','offunc','readerr','writeerr'].includes(a))return v>0?'err':'ok';
  if(a==='crc')return v>0?'warn':'ok';
  if(a==='temp')return v>=60?'err':v>=50?'warn':'ok';
  return'';}
function monSmart(list){const by={};
  for(const k of list){const mm=/^smart\.(.+)\.([^.]+)$/.exec(k.key);if(!mm)continue;
    const d=by[mm[1]]=by[mm[1]]||{name:'',a:{}};d.a[mm[2]]=k;
    if(mm[2]==='health'&&/^SMART:/.test(k.label||''))d.name=k.label.replace(/^SMART:\s*/,'');
    else if(!d.name&&(k.label||'').includes(':'))d.name=k.label.split(':')[0].trim();}
  const disks=Object.values(by);if(!disks.length)return'<div class="empty">Nincs SMART-adat.</div>';
  const col=l=>l==='err'?'var(--err)':l==='warn'?'var(--warn)':l==='ok'?'var(--ok)':'var(--ink)';
  return disks.map(d=>{const a=d.a;
    const lifeK=a.life||a.wearlevel||a.wearpct,life=a.life?a.life.latest:a.wearlevel?a.wearlevel.latest:a.wearpct!=null?100-a.wearpct.latest:null;
    const lifeL=life==null?'':life<10?'err':life<30?'warn':'ok';
    const h=a.health?a.health.latest:null,hText=h==null?'':h>=2?'hibás':h>=1?'figyelmeztetés':'kifogástalan';
    const rows=SMART_ORDER.filter(x=>a[x]&&x!=='health').concat(Object.keys(a).filter(x=>!SMART_ORDER.includes(x)))
      .map(x=>{const k=a[x],lv=smartLevel(x,k.latest),lab=(k.label||x).includes(':')?k.label.split(':').slice(1).join(':').trim():(k.label||x);
        return `<tr><td>${esc(lab)}</td><td style="text-align:right;color:${col(lv)};font-weight:${lv==='err'||lv==='warn'?600:400}" class="mono">${esc(monFmt(k.latest,k.unit,k.key))}</td></tr>`;}).join('');
    const gi=lifeK?_mp.keys.indexOf(lifeK):-1;
    return `<div class="gcard" style="margin-bottom:14px"><div class="gh"><b>${esc(d.name||'lemez')}</b>${hText?`<span style="color:${col(smartLevel('health',h))};font-weight:600">${hText}</span>`:''}</div>
      ${life!=null?`<div style="margin:10px 0 4px;display:flex;align-items:center;gap:12px"><div style="font-size:22px;font-weight:700;color:${col(lifeL)}">${Math.round(life)}%</div>
        <div style="flex:1"><div style="font-size:12px;color:var(--muted);margin-bottom:4px">hátralévő élettartam${a.wearpct&&!a.life&&!a.wearlevel?' (100 − elhasználtság)':''}</div>
          <div style="height:8px;background:var(--panel2);border-radius:4px;overflow:hidden"><div style="height:100%;width:${Math.max(0,Math.min(100,life))}%;background:${col(lifeL)}"></div></div></div>
        ${gi>=0?`<button class="btn small" onclick="smartGraph(${gi})">Grafikon</button>`:''}</div>
        <div id="sg-${gi}"></div>`:'<div style="font-size:12px;color:var(--muted);margin:8px 0">Az élettartamot ez a lemez nem jelenti (pl. régi HDD vagy USB-s ház).</div>'}
      <table class="upd-table" style="margin-top:6px">${rows}</table></div>`;}).join('');}
function smartGraph(i){const box=document.getElementById('sg-'+i);if(!box)return;
  if(box.innerHTML){box.innerHTML='';return;}
  box.innerHTML=`<div class="gbox" id="mg-${i}"></div><div class="gstat" id="ms-${i}"></div>`;monGraph(i);}
function monTime(t,h){const d=new Date(t*1000),p=n=>String(n).padStart(2,'0');return (h>24?`${p(d.getMonth()+1)}.${p(d.getDate())}. `:'')+`${p(d.getHours())}:${p(d.getMinutes())}`;}
async function monGraph(i){const m=_mp,k=m.keys[i],hours=m.hours;
  const d=await api('metric-series',{method:'POST',body:JSON.stringify(Object.assign({},m.req,{key:k.key,hours}))});
  if(_mp!==m||m.hours!==hours)return;   // közben máshová lépett
  const s=(d&&d.series)||[],el=document.getElementById('mg-'+i),st=document.getElementById('ms-'+i);if(!el)return;
  if(s.length<2){el.innerHTML='<div style="color:var(--muted);font-size:12px;text-align:center;line-height:130px">ebben az időszakban nincs elég adat</div>';return;}
  const W=Math.max(200,el.clientWidth||600),H=130,pt=18,pb=6,xs=s.map(p=>p.t),ys=s.map(p=>p.v),x0=Math.min(...xs),x1=Math.max(...xs);
  const lo=Math.min(...ys),hi=Math.max(...ys);
  // Nulláról indul (nem nagyítja fel a pici ingadozást). A teteje (a felhasználó kérése, 1.5.6): százaléknál 100%,
  // a lemez foglalt / szabad helyénél a lemez mérete - így látszik, mennyi a hely; egyébként a csúcs fölött kis hellyel.
  let y0=Math.min(0,lo),y1=hi>y0?hi+(hi-y0)*0.08:y0+1;
  if(k.unit==='%'&&hi<=100&&lo>=0)y1=100;
  const dm=/^disk\.(.+)\.(usedgb|freegb)$/.exec(k.key),tot=dm&&m.keys.find(x=>x.key===`disk.${dm[1]}.totalgb`);
  if(tot&&tot.latest>hi)y1=tot.latest;
  const nx=(t,j)=>(x1-x0)<1?j/(s.length-1)*W:(t-x0)/(x1-x0)*W,ny=v=>pt+(1-(v-y0)/(y1-y0))*(H-pt-pb);
  const P=s.map((p,j)=>[nx(p.t,j),ny(p.v)]),pts=P.map(q=>q[0].toFixed(1)+','+q[1].toFixed(1)).join(' ');
  el.innerHTML=`<svg width="100%" height="${H}" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" style="display:block">
    <line x1="0" x2="${W}" y1="${ny(hi).toFixed(1)}" y2="${ny(hi).toFixed(1)}" stroke="var(--line)" stroke-dasharray="4 4"/>
    <polygon points="${P[0][0].toFixed(1)},${H-pb} ${pts} ${P[P.length-1][0].toFixed(1)},${H-pb}" fill="var(--accent)" opacity="0.13"/>
    <polyline points="${pts}" fill="none" stroke="var(--accent)" stroke-width="1.6" vector-effect="non-scaling-stroke"/>
    <line class="gcur" x1="0" x2="0" y1="0" y2="${H}" stroke="var(--muted)" stroke-width="1" style="display:none"/></svg>
    <span class="gax" style="top:2px">${esc(monFmt(y1>hi*1.2||k.unit==='%'?y1:hi,k.unit))}</span><div class="gtip"></div>`;
  const avg=ys.reduce((a,b)=>a+b,0)/ys.length;
  st.innerHTML=`<span>min ${esc(monFmt(lo,k.unit))}</span><span>átlag ${esc(monFmt(avg,k.unit))}</span><span>max ${esc(monFmt(hi,k.unit))}</span><span>${monTime(x0,hours)} – ${monTime(x1,hours)}</span>`;
  const tip=el.querySelector('.gtip'),cur=el.querySelector('.gcur');
  el.onmousemove=e=>{const r=el.getBoundingClientRect(),x=(e.clientX-r.left)/r.width*W;let b=0;
    for(let j=1;j<P.length;j++)if(Math.abs(P[j][0]-x)<Math.abs(P[b][0]-x))b=j;
    cur.setAttribute('x1',P[b][0]);cur.setAttribute('x2',P[b][0]);cur.style.display='';
    tip.textContent=monTime(s[b].t,hours)+'  ·  '+monFmt(s[b].v,k.unit);tip.style.display='block';
    tip.style.left=Math.max(0,Math.min(r.width-tip.offsetWidth-4,P[b][0]/W*r.width-tip.offsetWidth/2))+'px';};
  el.onmouseleave=()=>{tip.style.display='none';cur.style.display='none';};}
""";

    /// <summary>
    /// Biztonság – kulcsok teljes oldalon (közös a FleetServer és a Központ felületén, csak admin): a saját kulcs és
    /// előzménye, automatikus csere beállítása, a gépek / telephelyek kulcsai előzménnyel, a kiadott tokenek, műveleti napló.
    /// Előfeltétel: api(), esc(), toast(), pageOpen(); az oldal rotateKeys() függvénye (kézi csere).
    /// </summary>
    public const string KeysJs = """
// ---- Biztonság – kulcsok (közös a FleetServer és a Központ felületén)
let _ks=null,_kv='own';
async function showSecurity(view){if(typeof closeModal==='function')closeModal();if(view)_kv=view;
  const d=await api('keys',{method:'POST',body:'{}'});if(!d||!d.owner){toast('Ehhez admin jog kell','err');return;}_ks=d;
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Biztonság – kulcsok</h2>
    <span class="sub">ECDSA-kulcspárok; csere láncolt átadással (a régi kulcs aláírja az újat)</span></div>
    <div class="page-body"><div class="page-menu" id="ksMenu"></div><div class="page-main" id="ksMain"></div></div>`);ksDraw();}
const fpS=f=>esc(String(f||'').slice(0,16))+'…';
function ksHist(h){return `<table class="upd-table"><tr><th>Ujjlenyomat</th><th>Algoritmus</th><th>Létrehozva</th><th>Lecserélve</th><th>Megjegyzés</th></tr>
  ${h.map((k,i)=>`<tr><td class="mono" title="${esc(k.fingerprint)}">${fpS(k.fingerprint)}</td><td>${esc(k.algorithm)}</td><td>${esc(k.created||'')}</td>
    <td>${k.retired?esc(k.retired):'<span class="sbadge repo">aktív</span>'}</td><td style="font-size:12px;color:var(--muted)">${esc(k.reason||'')}</td></tr>`).join('')}</table>`;}
function ksDraw(){const d=_ks,ag=d.peerKind==='agent',main=document.getElementById('ksMain');if(!main)return;
  const V=[['own',d.owner.title,d.owner.history.length],['peers',ag?'Agentek kulcsai':'Telephelyek kulcsai',d.peers.length],
    ['tokens',ag?'Beléptető tokenek':'Telephely-tokenek',d.tokens.length],['audit','Műveleti napló','']];
  document.getElementById('ksMenu').innerHTML=V.map(v=>`<button class="${_kv===v[0]?'sel':''}" onclick="_kv='${v[0]}';ksDraw()"><span>${v[1]}</span><span class="cnt">${v[2]}</span></button>`).join('');
  const o=d.owner,box='background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px;margin-bottom:14px';
  if(_kv==='own'){
    main.innerHTML=`<div style="${box}"><div style="font-size:13px;color:var(--muted)">Jelenlegi kulcs · ${esc(o.algorithm)} · létrehozva ${esc(o.created||'')}</div>
        <div class="token" style="font-size:13px">${esc(o.fingerprint)}</div>
        <div style="font-size:13px">${o.rotateDays>0?`Automatikus csere ${o.rotateDays} naponta · következő: <b>${esc(o.next||'')}</b>`:'<span style="color:var(--warn)">Automatikus csere kikapcsolva</span>'}</div>
        ${o.strongNote?`<div style="font-size:12px;color:var(--warn);margin-top:8px">${esc(o.strongNote)}</div>`:''}</div>
      <div style="${box}"><div style="font-weight:600;margin-bottom:6px">Automatikus kulcscsere</div>
        <div style="font-size:12px;color:var(--muted);margin-bottom:8px">A csere láncolt: a régi kulcs aláírja az újat, a ${ag?'gépek':'telephelyek'} a következő jelentkezéskor ellenőrzötten átállnak (nem kell újraregisztrálni).
          Az új kulcs ECDSA P-384, ha már minden ${ag?'agent':'telephely'} tudja ellenőrizni (1.4.5+). 0 = kikapcsolva.</div>
        <div class="row"><div class="col"><label style="margin-top:0">${esc(o.title)} cseréje (nap)</label><input id="ksSrv" type="number" value="${o.rotateDays}"></div>
          ${ag?`<div class="col"><label style="margin-top:0">Agentek kulcsának cseréje (nap)</label><input id="ksAg" type="number" value="${d.peerRotateDays}"></div>`:''}</div>
        <div style="display:flex;gap:8px;margin-top:12px"><button class="btn small primary" onclick="ksSave()">Mentés</button>
          <button class="btn small danger" onclick="rotateKeys()">Kulcscsere most</button></div></div>
      <div style="font-weight:600;margin:4px 0 8px">Előzmények (legújabb elöl)</div>${ksHist(o.history)}`;
  } else if(_kv==='peers'){
    main.innerHTML=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">${ag?`Minden agent saját kulcspárt használ; a privát kulcs a gépet nem hagyja el. ${d.peerRotateDays>0?`Az agentek ${d.peerRotateDays} naponta új kulcsot készítenek (a régivel aláírva, a szerver aláírva visszaigazolja); amíg az agent az újjal nem jelentkezik, a régi is érvényes (megerősítésre vár).`:'Az agentek kulcscseréje ki van kapcsolva.'}`
      :'A telephelyek kulcsa a regisztrációkor keletkezett; a Központ minden betárcsázást ezzel ellenőriz.'}</p>
      <table class="upd-table"><tr><th>${ag?'Gép':'Telephely'}</th><th>Kulcs</th><th>Mióta</th><th>Állapot</th><th>Előzmény</th></tr>
      ${d.peers.map(p=>`<tr><td><b>${esc(p.name)}</b><div style="font-size:11px;color:var(--muted)">${esc(p.version||'')}</div></td>
        <td><span class="mono" title="${esc(p.fingerprint)}">${fpS(p.fingerprint)}</span><div style="font-size:11px;color:var(--muted)">${esc(p.algorithm)}</div></td>
        <td>${esc(p.since||'')}</td><td>${p.revoked?'<span class="sbadge rejected">visszavonva</span>':p.pending?'<span class="sbadge pending">megerősítésre vár</span>':'<span class="sbadge repo">aktív</span>'}</td>
        <td><details><summary style="cursor:pointer;font-size:12px">${p.history.length} kulcs</summary>${ksHist(p.history)}</details></td></tr>`).join('')
        ||`<tr><td colspan="5" style="color:var(--muted)">Még nincs ${ag?'gép':'telephely'}.</td></tr>`}</table>`;
  } else if(_kv==='tokens'){
    const st={'aktív':'repo','letiltva':'rejected'};
    main.innerHTML=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">A kiadott ${ag?'beléptető':'telephely-'}tokenek. A szerver csak a lenyomatukat tárolja – maga a token nem nézhető vissza. Az aktív token letiltható.</p>
      <table class="upd-table"><tr><th>Címke</th><th>Kiadva</th><th>Lejár</th><th>Felhasználva</th><th>Állapot</th><th></th></tr>
      ${d.tokens.map((t,i)=>`<tr><td>${esc(t.label)}<div class="mono" style="font-size:11px">${fpS(t.id)}</div></td><td>${esc(t.created||'')}</td><td>${esc(t.expires||'nincs lejárat')}</td>
        <td>${t.uses} / ${t.maxUses>0?t.maxUses:'∞'}</td><td><span class="sbadge ${st[t.status]||'pending'}">${esc(t.status)}</span></td>
        <td style="text-align:right;white-space:nowrap">${t.status==='aktív'?`<button class="btn small" onclick="ksTokOff(${i})">Letiltás</button> `:''}<button class="btn small danger" onclick="ksTokDel(${i})">Törlés</button></td></tr>`).join('')
        ||'<tr><td colspan="6" style="color:var(--muted)">Még nem adtál ki tokent.</td></tr>'}</table>`;
  } else {
    main.innerHTML='<div class="empty">Betöltés…</div>';
    api('audit',{method:'POST',body:'{}'}).then(a=>{if(_kv!=='audit')return;main.innerHTML=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">Ki, mikor, mit csinált (legújabb elöl; jelszó nélkül).</p>
      <pre style="background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:12px;font-size:12px;white-space:pre-wrap">${esc((a&&a.log)||'(üres)')}</pre>`;});
  }}
async function ksSave(){const v=id=>{const e=document.getElementById(id);return e?parseInt(e.value)||0:0;};
  const r=await api('key-settings',{method:'POST',body:JSON.stringify({serverDays:v('ksSrv'),peerDays:v('ksAg')})});
  if(r&&r.ok){toast('Automatikus kulcscsere beállítva','ok');showSecurity('own');}else toast((r&&r.error)||'Hiba','err');}
async function ksTokDel(i){const t=_ks.tokens[i];if(!t||!await uiConfirm(`Véglegesen törlöd a(z) „${t.label}” tokent? Utána nem lehet vele regisztrálni, és a listából is eltűnik.`))return;
  const r=await api('token-delete',{method:'POST',body:JSON.stringify({id:t.id})});
  if(r&&r.ok){toast('Token törölve','ok');showSecurity('tokens');}else toast((r&&r.error)||'Hiba','err');}
async function ksTokOff(i){const t=_ks.tokens[i];if(!t||!await uiConfirm(`Letiltod a(z) „${t.label}” tokent? Utána nem lehet vele regisztrálni.`))return;
  const r=await api('token-disable',{method:'POST',body:JSON.stringify({id:t.id})});
  if(r&&r.ok){toast('Token letiltva','ok');showSecurity('tokens');}else toast((r&&r.error)||'Hiba','err');}
""";

    /// <summary>CSV-jelentések letöltése - közös a FleetServer és a Központ felületén (a /web/report végpontot hívja).</summary>
    public const string ReportsJs = """
// ---- CSV-jelentések (közös a FleetServer és a Központ felületén)
function showReports(){
  const it=[['gepek','Géplista','gépenként: állapot, utolsó jelentkezés, OS, agent-verzió, frissítések száma, újraindításra vár, csoportok'],
    ['programok','Telepített programok','gépenként minden program, verzióval és azonosítóval'],
    ['frissitesek','Elérhető frissítések','frissíthető programok és függő Windows-frissítések gépenként'],
    ['riasztasok','Riasztásnapló','küszöb-riasztások és kiesések időrendben (megszűnéssel együtt)']];
  const sel='padding:7px;background:var(--panel2);border:1px solid var(--line);border-radius:7px;color:var(--ink);margin-top:6px';
  openView(`<h3>Jelentések (CSV)</h3>
    <p class="muted" style="font-size:12px">Excelben megnyitható táblázatok (pontosvesszővel tagolt, UTF-8). A letöltés a mostani állapotot tartalmazza.</p>
    <table class="upd-table">${it.map(x=>`<tr><td><b>${x[1]}</b><div style="font-size:12px;color:var(--muted)">${x[2]}</div></td>
      <td style="text-align:right"><a class="btn small primary" href="/web/report?r=${x[0]}">Letöltés</a></td></tr>`).join('')}
      <tr><td><b>Metrika-összesítő</b><div style="font-size:12px;color:var(--muted)">gépenként és mérésenként napi átlag, minimum, maximum</div>
        <select id="repDays" style="${sel}"><option value="1">utolsó 1 nap</option><option value="7" selected>utolsó 7 nap</option><option value="30">utolsó 30 nap</option><option value="90">utolsó 90 nap</option></select></td>
      <td style="text-align:right"><a class="btn small primary" href="/web/report?r=metrikak" onclick="this.href='/web/report?r=metrikak&napok='+document.getElementById('repDays').value">Letöltés</a></td></tr>
    </table>`);
}
""";

    /// <summary>
    /// Monitorozás – szabályok és mérés teljes oldalon (közös a FleetServer és a Központ felületén; window.IS_HUB: Központ).
    /// Küszöb-szabályok listája, szerkesztő metrika-választóval (a gépek mért kulcsai + „bármelyik” minták), hatókör
    /// (minden gép / gépcsoport / gép; a Központban telephelyek és leküldés), a Központtól kapott szabályok (telephelyen,
    /// csak olvasható), és a „Mit mérjen?” beállítás. Előfeltétel: api(), esc(), toast(), openModal(), closeModal(), pageOpen().
    /// </summary>
    public const string RulesJs = """
// ---- Monitorozás – szabályok és mérés (közös a FleetServer és a Központ felületén)
let _ru=null,_rv='rules',_mcat=null;
const RU_OPS=['nagyobb mint (&gt;)','kisebb mint (&lt;)','egyenlő (=)'];
async function showRules(view){if(typeof closeModal==='function')closeModal();if(view)_rv=view;
  const d=await api('alerts',{method:'POST',body:'{}'});if(!d){toast('Nem érhető el','err');return;}_ru=d;
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Monitorozás – szabályok</h2>
    <span class="sub">${window.IS_HUB?'A Központ szabályai az 5 perces átlagokon futnak, és (ha bejelölöd) lemennek a telephelyekre is, ahol percre pontosan figyelnek':'Küszöb-szabályok és a mérés beállítása'}</span>
    ${window.ROLE>=1?'<button class="btn small primary" onclick="ruEdit(null)">+ Új szabály</button>':''}</div>
    <div class="page-body"><div class="page-menu" id="ruMenu"></div><div class="page-main" id="ruMain"></div></div>`);
  ruDraw();}
function ruScope(r){const s=[];
  if(window.IS_HUB){const sn=id=>((_ru.sites||[]).find(x=>x.siteId===id)||{name:'?'}).name;
    if(!r.agentId)s.push((r.sites||[]).length?r.sites.map(x=>esc(sn(x))).join(', '):'minden telephely');}
  s.push(r.agentId?'gép: '+esc(r.machine||'?'):r.group?'csoport: '+esc(r.group):'minden gép');
  if(window.IS_HUB)s.push(r.pushDown?'<span class="sbadge repo">lemegy a telephelyre</span>':'<span class="sbadge pending">csak a Központban</span>');
  return s.join(' · ');}
function ruDraw(){const d=_ru,main=document.getElementById('ruMain');if(!main||!d)return;
  const V=[['rules','Küszöb-szabályok',(d.rules||[]).length]];
  if(!window.IS_HUB)V.push(['hub','A Központtól',(d.hubRules||[]).length]);
  else V.push(['site','Telephelyek szabályai','']);
  V.push(['measure','Mit mérjen?','']);
  document.getElementById('ruMenu').innerHTML=V.map(v=>`<button class="${_rv===v[0]?'sel':''}" onclick="_rv='${v[0]}';ruDraw()"><span>${v[1]}</span><span class="cnt">${v[2]}</span></button>`).join('');
  _ruCtx=null;
  if(_rv==='measure')return ruMeasure();
  if(_rv==='site')return ruSiteDraw();
  const hub=_rv==='hub',list=hub?(d.hubRules||[]):(d.rules||[]),e=window.ROLE>=1&&!hub;
  main.innerHTML=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">${hub?'A Központ által leküldött szabályok: itt csak olvashatók, a helyi szabályok mellett futnak (a Központban módosíthatók).'
      :'Alapból semmi nem riaszt. Egy szabály egy mérésre (vagy a „bármelyik” mintával minden példányára, pl. bármelyik lemezre) figyel; riaszt, ha a feltétel ennyi egymást követő mérésnél teljesül, és megszűnik, ha már nem. Értesítés: a riasztásnaplóban'+(window.IS_HUB?' és e-mailben (Beállítások).':', a Központban e-mailben is.')}</p>
    <table class="upd-table"><tr><th>Név</th><th>Feltétel</th><th>Hatókör</th><th></th></tr>
    ${list.map((r,i)=>`<tr><td><b>${esc(r.name)}</b>${r.activeCount>0?` <span class="badge upd">${r.activeCount} riaszt</span>`:''}${r.enabled?'':' <span style="color:var(--muted)">(kikapcsolva)</span>'}</td>
      <td><span class="mono">${esc(r.metricKey)}</span> ${esc(r.opText)} <b>${esc(ruThr(r))}</b><div style="font-size:11px;color:var(--muted)">${r.forSamples} egymást követő ${window.IS_HUB?'5 perces átlag':'mérés'}${/\*/.test(r.metricKey)?' · minden példányra külön':''}</div></td>
      <td style="font-size:12px">${ruScope(r)}</td>
      <td style="text-align:right;white-space:nowrap">${e?`<button class="btn small" onclick="ruEdit(${i})">Szerkesztés</button> <button class="btn small" onclick="ruToggle(${i})">${r.enabled?'Kikapcsolás':'Bekapcsolás'}</button> <button class="btn small danger" onclick="ruDel(${i})">Törlés</button>`:''}</td></tr>`).join('')
      ||`<tr><td colspan="4" style="color:var(--muted)">${hub?'A Központ nem küldött szabályt.':'Még nincs szabály – „+ Új szabály”, vagy a gép Monitorozás nézetében a „+ küszöb” gomb.'}</td></tr>`}</table>`;}
// ---- 1.5.7 (Központ): a telephely SAJÁT szabályai – szerkeszthetők; a telephely hajtja végre a következő betárcsázáskor.
let _ruCtx=null,_ruSiteId='';
async function ruSiteDraw(){const main=document.getElementById('ruMain');if(!main)return;const sites=_ru.sites||[];
  if(!_ruSiteId&&sites.length)_ruSiteId=sites[0].siteId;
  if(!_ruSiteId){main.innerHTML='<div class="empty">Még nincs telephely.</div>';return;}
  const d=await api('site-rules',{method:'POST',body:JSON.stringify({siteId:_ruSiteId})});if(!d||!d.ok){main.innerHTML=`<div class="empty">${esc((d&&d.error)||'Nem érhető el')}</div>`;return;}
  _ruCtx={siteId:_ruSiteId,data:d};const e=window.ROLE>=1&&d.supports,list=d.rules||[];
  main.innerHTML=`<div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-bottom:10px">
      <select class="grpsel" onchange="_ruSiteId=this.value;ruSiteDraw()">${sites.map(s=>`<option value="${esc(s.siteId)}" ${s.siteId===_ruSiteId?'selected':''}>${esc(s.name)}</option>`).join('')}</select>
      ${e?'<button class="btn small primary" onclick="ruEdit(null)">+ Új szabály ezen a telephelyen</button>':''}
      ${d.pending?`<span class="badge upd">${d.pending} módosítás vár leküldésre${d.nextDial?' (kb. '+esc(d.nextDial)+')':''}</span>`:''}</div>
    <p style="font-size:13px;color:var(--muted);margin:0 0 12px">${d.supports?'A telephely szerverének saját (helyi) szabályai, a telephely percenkénti mérésein futnak. Itt módosítva a telephely a következő betárcsázáskor átveszi és ő menti (a műveleti naplójába a te neveddel).'
      :'A telephely szervere még nem ismeri a Központból való szerkesztést (1.5.7-től) – frissítsd; addig csak olvasható.'}</p>
    <table class="upd-table"><tr><th>Név</th><th>Feltétel</th><th>Hatókör</th><th></th></tr>
    ${list.map((r,i)=>`<tr><td><b>${esc(r.name)}</b>${r.enabled?'':' <span style="color:var(--muted)">(kikapcsolva)</span>'}</td>
      <td><span class="mono">${esc(r.metricKey)}</span> ${esc(r.opText)} <b>${esc(ruThr(r))}</b><div style="font-size:11px;color:var(--muted)">${r.forSamples} egymást követő mérés${/\*/.test(r.metricKey)?' · minden példányra külön':''}</div></td>
      <td style="font-size:12px">${r.agentId?'gép: '+esc(r.machine||'?'):r.group?'csoport: '+esc(r.group):'minden gép'}</td>
      <td style="text-align:right;white-space:nowrap">${e?`<button class="btn small" onclick="ruEdit(${i})">Szerkesztés</button> <button class="btn small" onclick="ruToggle(${i})">${r.enabled?'Kikapcsolás':'Bekapcsolás'}</button> <button class="btn small danger" onclick="ruDel(${i})">Törlés</button>`:''}</td></tr>`).join('')
      ||'<tr><td colspan="4" style="color:var(--muted)">A telephelynek nincs saját szabálya.</td></tr>'}</table>`;}
async function ruCatalog(){if(!_mcat)_mcat=(await api('metric-catalog',{method:'POST',body:'{}'}))||{keys:[],patterns:[]};return _mcat;}
// A metrika-választó: előbb a „bármelyik” minták, utána a mért kulcsok kategóriánként; szűrhető.
function ruOptions(sel,q){const c=_mcat||{keys:[],patterns:[]};q=(q||'').toLowerCase();
  const hit=k=>!q||(k.label+' '+k.key).toLowerCase().includes(q);
  const o=k=>`<option value="${esc(k.key)}" ${k.key===sel?'selected':''}>${esc(k.label||k.key)}${k.unit?' ['+esc(k.unit)+']':''} – ${esc(k.key)}</option>`;
  const cats={};for(const k of c.keys.filter(hit)){(cats[k.category||'Egyéb']=cats[k.category||'Egyéb']||[]).push(k);}
  let h='<option value="">– válassz mérést –</option>';
  const pats=c.patterns.filter(hit);if(pats.length)h+=`<optgroup label="Bármelyik példány (minta)">${pats.map(o).join('')}</optgroup>`;
  for(const n of Object.keys(cats).sort())h+=`<optgroup label="${esc(n)}">${cats[n].map(o).join('')}</optgroup>`;
  if(sel&&!c.keys.some(k=>k.key===sel)&&!c.patterns.some(k=>k.key===sel))h+=`<option value="${esc(sel)}" selected>${esc(sel)} (kézi)</option>`;
  return h;}
function ruFilter(){const s=document.getElementById('reKey');s.innerHTML=ruOptions(s.value,document.getElementById('reF').value);}
// ---- Mértékegység a küszöbhöz (1.5.6, a felhasználó kérése): a küszöb a mérés saját egységében tárolódik (CPU, memória,
//      lemez %; hálózat KB/s vagy Mbit/s), a felület átvált – a hálózatnál Mbit/s és Gbit/s is (megabites és gigabites vonal).
const RU_UNITS={'KB/s':[['KB/s',1],['MB/s',1024],['GB/s',1048576],['kbit/s',1000/8/1024],['Mbit/s',1e6/8/1024],['Gbit/s',1e9/8/1024]],
  'Mbps':[['Mbit/s',1],['Gbit/s',1000],['kbit/s',0.001]],'GB':[['GB',1],['TB',1000],['MB',0.001]]};
function ruUnitOf(key){const c=_mcat||{keys:[],patterns:[]};const k=c.keys.concat(c.patterns).find(x=>x.key===key);
  if(k)return k.unit||'';return /\.(rx|tx)kbps$|kbps$/.test(key)?'KB/s':/mbps$/.test(key)?'Mbps':/(pct|load)$/.test(key)?'%':/gb$/.test(key)?'GB':'';}
function ruFactorOf(key,unit){const o=(RU_UNITS[ruUnitOf(key)]||[]).find(x=>x[0]===unit);return o?o[1]:1;}
function ruThr(r){const base=ruUnitOf(r.metricKey);const u=r.displayUnit&&RU_UNITS[base]?r.displayUnit:'';const f=u?ruFactorOf(r.metricKey,u):1;
  const v=+(r.threshold/f).toFixed(f===1?2:3);return v+' '+(u||(base==='Mbps'?'Mbit/s':base));}
function ruKeyNow(){return (document.getElementById('reKeyFree').value.trim()||document.getElementById('reKey').value||'').trim();}
function ruUnitUi(unit){const key=ruKeyNow(),base=ruUnitOf(key),opts=RU_UNITS[base],box=document.getElementById('reUnitBox');if(!box)return;
  const cur=unit||(document.getElementById('reUnit')||{}).value||'';
  box.innerHTML=opts?`<select id="reUnit" class="grpsel" style="width:100%">${opts.map(([u])=>`<option ${u===(cur||opts[0][0])?'selected':''}>${u}</option>`).join('')}</select>`
    :`<div style="padding:9px 0;color:var(--muted)">${esc(base==='%'?'% (százalék)':base||'–')}</div>`;}
// Gyakori szabályok: a „bármelyik” minta MINDEN példányra külön figyel (minden CPU-mag, minden lemez – HDD, SSD, NVMe –, minden hálózati kártya).
const RU_QUICK=[['Teljes CPU-terhelés (minden mag együtt)','cpu.load',0,90,''],['Bármelyik CPU-mag','cpu.core*.load',0,95,''],['Memória-használat','mem.usedpct',0,90,''],
  ['Bármelyik lemez telítettsége','disk.*.usedpct',0,90,''],['Bármelyik lemez szabad helye','disk.*.freegb',1,10,'GB'],
  ['Bármelyik HDD / SSD hőmérséklete','smart.*.temp',0,55,''],['Bármelyik SSD hátralévő élettartama','smart.*.life',1,20,''],['Bármelyik lemez elhasználtsága (Windows)','smart.*.wearpct',0,80,''],
  ['Áthelyezett szektorok (bármelyik HDD)','smart.*.realloc',0,0,''],['Hálózati fogadás (összes)','net.rxmbps',0,800,'Mbit/s'],['Hálózati küldés (összes)','net.txmbps',0,800,'Mbit/s'],
  ['Bármelyik hálózati kártya kihasználtsága','net.*.utilpct',0,80,'']];
function ruQuick(k){const q=RU_QUICK[k],g=id=>document.getElementById(id),s=g('reKey');
  if([...s.options].some(o=>o.value===q[1])){s.value=q[1];g('reKeyFree').value='';}else g('reKeyFree').value=q[1];
  g('reOp').value=q[2];g('reVal').value=q[3];if(!g('reName').value.trim())g('reName').value=q[0];ruUnitUi(q[4]);}
async function ruEdit(i,pre){if(!_ru)_ru=(await api('alerts',{method:'POST',body:'{}'}))||{rules:[]};await ruCatalog();
  // A Központban a telephely saját szabályainál (_ruCtx) a telephely csoportjai és gépei számítanak, telephely-választó nélkül.
  const src=_ruCtx?_ruCtx.data:_ru,hubRule=window.IS_HUB&&!_ruCtx;
  const r=i!=null?Object.assign({},src.rules[i]):{id:'',name:pre?pre.label+' figyelmeztetés':'',metricKey:pre?pre.key:'',op:0,threshold:'',
    agentId:(pre&&pre.agentId)||'',group:'',forSamples:2,enabled:true,sites:[],pushDown:true};
  window._re=r;const sel='width:100%;padding:9px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)';
  const scope=r.agentId?'agent':r.group?'group':'all';
  const grp=(src.groups||[]),mach=(src.machines||[]);
  openModal(`<h3>${i!=null?'Szabály szerkesztése':'Új küszöb-szabály'}${_ruCtx?' – '+esc(_ruCtx.data.name):''}</h3>
    <label>Név</label><input id="reName" value="${esc(r.name)}" placeholder="pl. Lemez megtelik">
    <label>Gyakori szabályok</label><div class="chips" style="margin-bottom:4px">${RU_QUICK.map((q,k)=>`<span class="chip" onclick="ruQuick(${k})">${esc(q[0])}</span>`).join('')}</div>
    <div style="font-size:11px;color:var(--muted);margin-bottom:6px">A „Bármelyik …” (minta) szabály minden példányra külön figyel: minden CPU-magra, minden lemezre (HDD, SSD, NVMe), minden hálózati kártyára – amelyik túllépi, az riaszt.
      A „Teljes CPU-terhelés” a processzor egészére (minden mag együtt) vonatkozik, %-ban.</div>
    <label>Mérés</label><input id="reF" placeholder="Szűrés a mérések között (pl. lemez, hő, ping)…" oninput="ruFilter()" style="margin-bottom:6px">
    <select id="reKey" style="${sel}" size="8" onchange="ruUnitUi()">${ruOptions(r.metricKey,'')}</select>
    <details style="margin-top:6px"><summary style="cursor:pointer;font-size:12px">Saját kulcs (haladó)</summary>
      <input id="reKeyFree" placeholder="pl. disk.*.usedpct vagy svc.MSSQLSERVER.running" style="margin-top:6px" oninput="ruUnitUi()">
      <div style="font-size:11px;color:var(--muted);margin-top:4px">A * egy kulcsrész bármilyen értéke (pl. disk.*.usedpct = bármelyik lemez). Ha kitöltöd, ez számít.</div></details>
    <div class="row"><div class="col"><label>Feltétel</label><select id="reOp" style="${sel}">${RU_OPS.map((t,k)=>`<option value="${k}" ${+r.op===k?'selected':''}>${t}</option>`).join('')}</select></div>
      <div class="col"><label>Érték</label><input id="reVal" type="number" step="any" value="${esc(r.threshold===''?'':+(r.threshold/ruFactorOf(r.metricKey,r.displayUnit||'')).toFixed(3))}" placeholder="pl. 90"></div>
      <div class="col"><label>Mértékegység</label><div id="reUnitBox"></div></div>
      <div class="col"><label>Egymást követő ${window.IS_HUB?'átlag':'mérés'}</label><input id="reFor" type="number" value="${r.forSamples||1}"></div></div>
    <label>Hatókör</label><select id="reScope" style="${sel}" onchange="ruScopeUi()">
      <option value="all" ${scope==='all'?'selected':''}>Minden gép</option><option value="group" ${scope==='group'?'selected':''}>Gépcsoport</option>
      <option value="agent" ${scope==='agent'?'selected':''}>Egy gép</option></select>
    <div id="reGroupBox" style="margin-top:8px"><select id="reGroup" style="${sel}">${grp.map(g=>`<option ${g===r.group?'selected':''}>${esc(g)}</option>`).join('')||'<option value="">(még nincs csoport)</option>'}</select></div>
    <div id="reAgentBox" style="margin-top:8px"><select id="reAgent" style="${sel}">${mach.map(m=>`<option value="${esc(m.agentId)}" ${m.agentId===r.agentId?'selected':''}>${esc(m.name)}</option>`).join('')}</select></div>
    ${hubRule?`<div id="reSitesBox"><label>Telephelyek (ha egyik sincs bejelölve: mind)</label><div style="display:flex;gap:10px;flex-wrap:wrap">${(_ru.sites||[]).map(s=>`<label style="display:flex;gap:4px;align-items:center;margin:0"><input type="checkbox" class="reSite" value="${esc(s.siteId)}" ${(r.sites||[]).includes(s.siteId)?'checked':''}>${esc(s.name)}</label>`).join('')}</div></div>
      <label style="display:flex;align-items:center;gap:8px;margin-top:10px"><input type="checkbox" id="rePush" ${r.pushDown!==false?'checked':''}> Leküldés a telephelyekre is (ott a helyi szabályok mellett, percenkénti méréseken fut, csak olvasható)</label>`:''}
    <label style="display:flex;align-items:center;gap:8px;margin-top:10px"><input type="checkbox" id="reOn" ${r.enabled?'checked':''}> Bekapcsolva</label>
    <div style="display:flex;gap:8px;margin-top:12px"><button class="btn small" onclick="closeModal()">Mégse</button><button class="btn small primary" onclick="ruSave()">Mentés</button></div>`,true);
  ruScopeUi();ruUnitUi(r.displayUnit||'');}
function ruScopeUi(){const v=document.getElementById('reScope').value;
  document.getElementById('reGroupBox').style.display=v==='group'?'':'none';document.getElementById('reAgentBox').style.display=v==='agent'?'':'none';
  const sb=document.getElementById('reSitesBox');if(sb)sb.style.display=v==='agent'?'none':'';}
async function ruSave(){const g=id=>document.getElementById(id),sc=g('reScope').value;
  const key=(g('reKeyFree').value.trim()||g('reKey').value||'').trim();
  if(!key){toast('Válassz mérést','err');return;}
  const tv=parseFloat(g('reVal').value);if(isNaN(tv)){toast('Adj meg értéket','err');return;}
  if(sc==='group'&&!g('reGroup').value){toast('Nincs csoport – előbb hozz létre egyet (Csoportok)','err');return;}
  // A választott egységből a mérés saját egységébe (pl. 1 Gbit/s → KB/s).
  const unit=g('reUnit')?g('reUnit').value:'',th=+(tv*ruFactorOf(key,unit)).toFixed(4);
  const body={id:window._re.id||'',name:g('reName').value.trim(),metricKey:key,op:parseInt(g('reOp').value),threshold:th,displayUnit:RU_UNITS[ruUnitOf(key)]?unit:'',
    forSamples:parseInt(g('reFor').value)||1,enabled:g('reOn').checked,agentId:sc==='agent'?g('reAgent').value:'',group:sc==='group'?g('reGroup').value:''};
  if(_ruCtx){body.siteId=_ruCtx.siteId;
    const r=await api('site-rule-save',{method:'POST',body:JSON.stringify(body)});
    if(r&&r.ok){toast('Mentve – a telephely a következő betárcsázáskor átveszi','ok');closeModal();ruSiteDraw();}else toast((r&&r.error)||'Nem sikerült','err');return;}
  if(window.IS_HUB){body.sites=sc==='agent'?[]:[...document.querySelectorAll('.reSite:checked')].map(c=>c.value);body.pushDown=g('rePush').checked;}
  const r=await api('alert-save',{method:'POST',body:JSON.stringify(body)});
  if(r&&r.ok){toast(window.IS_HUB&&body.pushDown?'Szabály mentve – a telephelyek a következő betárcsázáskor átveszik':'Szabály mentve','ok');closeModal();if(document.getElementById('ruMain'))showRules('rules');}
  else toast((r&&r.error)||'Nem sikerült','err');}
async function ruToggle(i){const src=_ruCtx?_ruCtx.data:_ru,r=Object.assign({},src.rules[i],{enabled:!src.rules[i].enabled});
  if(_ruCtx)r.siteId=_ruCtx.siteId;
  const x=await api(_ruCtx?'site-rule-save':'alert-save',{method:'POST',body:JSON.stringify(r)});
  if(x&&x.ok){toast(r.enabled?'Bekapcsolva':'Kikapcsolva','ok');_ruCtx?ruSiteDraw():showRules('rules');}else toast((x&&x.error)||'Hiba','err');}
async function ruDel(i){const src=_ruCtx?_ruCtx.data:_ru,r=src.rules[i];if(!await uiConfirm(`Törlöd a(z) „${r.name}” szabályt?`))return;
  const x=await api(_ruCtx?'site-rule-delete':'alert-delete',{method:'POST',body:JSON.stringify({id:r.id,siteId:_ruCtx?_ruCtx.siteId:''})});
  if(x&&x.ok){toast(_ruCtx?'Törölve – a telephely a következő betárcsázáskor átveszi':'Törölve','ok');_ruCtx?ruSiteDraw():showRules('rules');}else toast((x&&x.error)||'Hiba','err');}
// ---- Mit mérjen? (a telephelyen az agentek mérése; a Központban a telephelyeknek küldött kérés)
const RU_SVC=[['MSSQLSERVER','MSSQL'],['W3SVC','IIS'],['DHCPServer','DHCP'],['DNS','DNS'],['NTDS','Active Directory'],['Spooler','Nyomtatás'],['TermService','Távoli asztal'],['WinRM','WinRM']];
async function ruMeasure(){const main=document.getElementById('ruMain');main.innerHTML='<div class="empty">Betöltés…</div>';
  const c=await api('monitor-config',{method:'POST',body:'{}'});if(!c||_rv!=='measure')return;const hub=!!window.IS_HUB,ed=hub?window.ROLE>=2:window.ROLE>=1,dis=ed?'':'disabled';
  const cb=(id,lab,on,hint)=>`<label style="display:flex;align-items:flex-start;gap:8px;margin:7px 0"><input type="checkbox" id="${id}" ${on?'checked':''} ${dis} style="margin-top:3px"> <span>${lab}${hint?`<div style="font-size:11px;color:var(--muted)">${hint}</div>`:''}</span></label>`;
  const box='background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px;margin-bottom:12px';
  const h=c.hub;
  main.innerHTML=`<div class="view">
    ${hub?`<div style="${box}"><label style="display:flex;align-items:center;gap:8px;margin:0;font-weight:600"><input type="checkbox" id="mPush" ${c.pushEnabled?'checked':''} ${dis}> A mérés-kérés leküldése a telephelyekre</label>
      <div style="font-size:12px;color:var(--muted);margin:6px 0">A telephely a saját beállításával összefésüli: amit bármelyik kér, azt méri (a Központ a helyi mérést nem kapcsolja ki).</div>
      <div style="display:flex;gap:10px;flex-wrap:wrap">${(c.allSites||[]).map(s=>`<label style="display:flex;gap:4px;align-items:center;margin:0"><input type="checkbox" class="mSite" value="${esc(s.siteId)}" ${(c.pushSites||[]).includes(s.siteId)?'checked':''} ${dis}>${esc(s.name)}</label>`).join('')}</div>
      <div style="font-size:11px;color:var(--muted);margin-top:4px">Ha egyik telephely sincs bejelölve: mindegyikre.</div></div>`
     :(h&&(h.cpu||h.hardware||h.memory)?`<div style="${box};font-size:12px;color:var(--muted)">A Központ is kér mérést (a helyivel összefésülve megy a gépekhez): ${['cpu','memory','disk','hardware','gpu','security','ping','hyperV'].filter(k=>h[k]).join(', ')}${(h.services||[]).length?' · szolgáltatások: '+esc(h.services.join(', ')):''}</div>`:'')}
    <div style="${box}"><div style="font-weight:600">Processzor és memória</div>
      ${cb('mCpu','CPU-terhelés, várakozási sor, folyamatok, futásidő',c.cpu)}
      ${cb('mCpuD','CPU részletek: órajel, turbó, lassítás (throttling), C-állapotok, megszakítások, környezetváltás',c.cpuDetail)}
      ${cb('mCore','Magonkénti terhelés (és -hőmérséklet)',c.perCore,'Sok magnál sok mérés – alapból kikapcsolva.')}
      ${cb('mMem','Memória: foglalt, szabad, gyorsítótár, commit, lapozás (swap), laphibák',c.memory)}</div>
    <div style="${box}"><div style="font-weight:600">Lemez és hálózat</div>
      ${cb('mDisk','Lemezek: méret, szabad / foglalt hely partíciónként',c.disk)}
      ${cb('mDio','Lemez-aktivitás: kihasználtság, IOPS, MB/s, sor, válaszidő',c.diskIo)}
      ${cb('mSmart','Lemez-állapot: SMART, NVMe, kopás, hibák, üzemórák',c.diskSmart)}
      ${cb('mNet','Hálózat: forgalom (Mbps), kihasználtság, hibás / eldobott csomagok, TCP / UDP kapcsolatok',c.network)}
      ${cb('mPing','Ping: válaszidő, ingadozás, csomagvesztés a szerverre és az átjáróra',c.ping)}
      <label style="margin-top:4px">További ping-célok (vesszővel, pl. 8.8.8.8, router.local)</label><input id="mPingT" value="${esc((c.pingTargets||[]).join(', '))}" ${dis}></div>
    <div style="${box}"><div style="font-weight:600">Hardver</div>
      ${cb('mHw','Hardver-érzékelők: CPU-, GPU-, alaplap-, lemez- és memória-hőmérséklet, ventilátorok, fogyasztás (W), GPU-terhelés',c.hardware,'LibreHardwareMonitor az agentben.')}
      ${cb('mDrv','Az érzékelő-meghajtó (PawnIO) telepítése, ha hiányzik',c.sensorDriver,'A CPU- és alaplap-érzékelőkhöz kell; az agent wingettel telepíti (namazso.PawnIO, aláírt meghajtó). Nélküle csak a GPU és a lemezek értékei jönnek.')}
      ${cb('mGpu','GPU-terhelés és videómemória a Windows számlálóiból (meghajtó nélkül is)',c.gpu)}
      ${cb('mTemp','ACPI hőmérséklet-zónák (régi módszer)',c.temperature)}</div>
    <div style="${box}"><div style="font-weight:600">Napló, biztonság, virtualizáció</div>
      ${cb('mEvt','Eseménynapló: kritikus / hiba / figyelmeztetés, lemezhibák, szolgáltatás- és program-összeomlás, váratlan leállás, hardverhibák (WHEA, ECC)',c.eventLog)}
      ${cb('mSec','Biztonság: sikeres / sikertelen belépés, fiókzárolás, próbálkozás egy forrásból (jelszótörés)',c.security)}
      ${cb('mHv','Hyper-V: virtuális gépek állapota, terhelése, ellenőrzőpontok',c.hyperV)}</div>
    <div style="${box}"><div style="font-weight:600">Szolgáltatások és folyamatok</div>
      <label>Figyelt szolgáltatások – leállásukra küszöb-szabály állítható (… &lt; 1)</label>
      ${ed?`<button class="btn small primary" style="margin-bottom:8px" onclick="ruSvcPick()">Választás a gépeken levő szolgáltatások közül…</button>`:''}
      <input id="mSvc" value="${esc((c.services||[]).join(', '))}" ${dis} placeholder="szolgáltatásnév, vesszővel (a választó ide írja)">
      ${ed?`<div style="display:flex;gap:6px;flex-wrap:wrap;margin-top:6px">${RU_SVC.map(s=>`<button class="btn small" onclick="ruAddSvc('${s[0]}')">+ ${s[1]}</button>`).join('')}</div>`:''}
      <div class="row"><div class="col"><label>Szolgáltatás-figyelés: a fenti szolgáltatásokat ennyi másodpercenként nézi (0 = ki)</label>
        <input id="mWatch" type="number" min="0" max="300" value="${c.watchSeconds??5}" ${dis}></div><div class="col"></div></div>
      <div style="font-size:11px;color:var(--muted)">Ha egy figyelt szolgáltatás leáll, az agent azonnal jelez, és riasztás lesz belőle („Szolgáltatás leállt”); újraindulásakor megszűnik.</div>
      <label>Figyelt folyamatok (vesszővel, pl. sqlservr, nginx) – példányszám, CPU, memória, leírók, szálak</label><input id="mProc" value="${esc((c.processes||[]).join(', '))}" ${dis}></div>
    <div style="${box}"><div style="font-weight:600">Frissítések és külső eszközök</div>
      <div class="row"><div class="col"><label>Programfrissítések keresése (percenként, 15–1440)</label><input id="mUpd" type="number" value="${c.appUpdateMinutes||30}" ${dis}></div>
        <div class="col"><label>Windows-frissítések keresése (óránként, 1–48)</label><input id="mWu" type="number" value="${c.wuScanHours||6}" ${dis}></div></div>
      <div style="font-size:11px;color:var(--muted);margin-bottom:6px">Így a gépek frissítés-listája mindig friss: ami már nem kell, eltűnik. Programtelepítés után az agent azonnal újra megnézi.</div>
      ${cb('mUsb','Másolás külső eszközre: a csatlakoztatott USB- / cserélhető meghajtókra írt fájlok naplózása (melyik gépen, mikor, milyen fájl, ki volt bejelentkezve)',c.usbAudit,
        'Munkavállalói megfigyelésnek számít: a GDPR miatt a dolgozókat előre tájékoztatni kell. Alapból kikapcsolva.')}
      ${hub?'':cb('mTray','Tálca-ikon a gépeken: a felhasználó a jóváhagyott (repó) programokat telepítheti, másikat kérhet; „Belépés adminként” kezelőknek',c.tray!==false,
        'A kért programok a Programkérések oldalon (és a Központban) dönthetők el; jóváhagyás után maguktól települnek. Alapból bekapcsolva.')}</div>
    <div style="${box}"><div class="row"><div class="col"><label style="margin-top:0">Mintavétel (másodperc, 15–3600)</label><input id="mSmp" type="number" value="${c.sampleSeconds||60}" ${dis}></div>
      ${hub?'':`<div class="col"><label style="margin-top:0">Részletes megőrzés (nap)</label><input id="mDet" type="number" value="${c.detailDays}" ${dis}></div>
      <div class="col"><label style="margin-top:0">Teljes megőrzés (nap)</label><input id="mKeep" type="number" value="${c.hubKeepDays||c.keepDays}" ${dis} ${c.hubKeepDays?'disabled':''}></div>`}</div>
      ${!hub&&c.hubKeepDays?`<div style="font-size:12px;color:var(--muted);margin-top:6px">A megőrzést a Központ állítja (${c.hubKeepDays} nap): a mérések naponta külön fájlba kerülnek, a lezárt nap fájlja ${c.hubKeepDays} nap után feltöltődik a Központba, és innen törlődik – utána csak ott látszik.</div>`:''}</div>
    ${ed?'<button class="btn primary small" onclick="ruMeasureSave()">Mentés</button>':`<div style="font-size:12px;color:var(--muted)">${hub?'Csak admin módosíthatja.':'Módosításhoz kezelői jog kell.'}</div>`}
    <div style="font-size:12px;color:var(--muted);margin-top:8px">${hub?'A telephelyek a következő betárcsázáskor, a gépek az azutáni jelentkezéskor veszik át.':'A gépek a következő jelentkezéskor átveszik.'}</div></div>`;}
function ruAddSvc(n){const i=document.getElementById('mSvc'),l=i.value.split(',').map(x=>x.trim()).filter(Boolean);if(!l.includes(n))l.push(n);i.value=l.join(', ');}
// Választólista: a gépek által felküldött szolgáltatások (1.5.1-es agenttől), hány gépen van / fut. A kijelölés a
// mezőbe kerül (a kézzel beírt, listában nem szereplő nevek megmaradnak); mentés a „Mentés” gombbal.
async function ruSvcPick(){const d=(await api('services-catalog',{method:'POST',body:'{}'}))||{services:[]};const sv=d.services||[];window._svl=sv;
  const cur=document.getElementById('mSvc').value.split(',').map(x=>x.trim().toLowerCase()).filter(Boolean);
  openModal(`<h3>Szolgáltatások a gépeken</h3>
    <div style="font-size:12px;color:var(--muted);margin-bottom:8px">${sv.length} szolgáltatás · ${d.reported||0} / ${d.machines||0} ${window.IS_HUB?'telephely':'gép'} küldött listát
      ${(d.reported||0)<(d.machines||0)?' (a lista az 1.5.1-es agenttől jön, a következő jelentkezéskor)':''}</div>
    <input id="svF" placeholder="Szűrés név szerint (pl. sql, print, dhcp)…" oninput="fltRows('svF','svrow')" style="margin-bottom:6px">
    <label style="display:flex;gap:8px;align-items:center;margin:4px 0 8px;color:var(--muted)"><input type="checkbox" id="svOnly" onchange="document.querySelectorAll('.svrow').forEach(r=>r.style.display=(!this.checked||r.dataset.run==='1')&&r.dataset.n.includes(document.getElementById('svF').value.toLowerCase())?'':'none')"> csak a futók</label>
    <div style="max-height:380px;overflow:auto">${sv.map((s,i)=>`<label class="svrow" data-run="${s.running>0?1:0}" data-n="${esc((s.displayName+' '+s.name).toLowerCase())}" style="display:flex;gap:8px;align-items:center;margin:3px 0">
      <input type="checkbox" class="svC" value="${i}" ${cur.includes(s.name.toLowerCase())?'checked':''}> <span>${esc(s.displayName||s.name)} <span class="mono" style="font-size:11px">${esc(s.name)}</span>
      <span style="font-size:11px;color:var(--muted)"> · ${s.machines} gépen${s.running?', '+s.running+' fut':''}</span></span></label>`).join('')
      ||'<div class="empty">Még egy gép sem küldött szolgáltatáslistát (az 1.5.1-es agent küldi, a következő jelentkezéskor).</div>'}</div>
    <div style="display:flex;gap:8px;margin-top:10px"><button class="btn small" onclick="closeModal()">Mégse</button><button class="btn small primary" onclick="ruSvcApply()">Kijelöltek átvétele</button></div>`);}
function ruSvcApply(){const sv=window._svl||[],i=document.getElementById('mSvc');
  const known=new Set(sv.map(s=>s.name.toLowerCase())),manual=i.value.split(',').map(x=>x.trim()).filter(x=>x&&!known.has(x.toLowerCase()));
  i.value=manual.concat([...document.querySelectorAll('.svC:checked')].map(c=>sv[+c.value].name)).join(', ');closeModal();toast('Átvéve – a mentéshez kattints a „Mentés” gombra','ok');}
async function ruMeasureSave(){const g=id=>document.getElementById(id),L=id=>g(id).value.split(',').map(x=>x.trim()).filter(Boolean);
  const b={cpu:g('mCpu').checked,cpuDetail:g('mCpuD').checked,perCore:g('mCore').checked,memory:g('mMem').checked,disk:g('mDisk').checked,diskIo:g('mDio').checked,
    diskSmart:g('mSmart').checked,network:g('mNet').checked,ping:g('mPing').checked,pingTargets:L('mPingT'),hardware:g('mHw').checked,sensorDriver:g('mDrv').checked,
    gpu:g('mGpu').checked,temperature:g('mTemp').checked,eventLog:g('mEvt').checked,security:g('mSec').checked,hyperV:g('mHv').checked,
    services:L('mSvc'),processes:L('mProc'),sampleSeconds:parseInt(g('mSmp').value)||60,
    watchSeconds:Math.max(0,parseInt(g('mWatch').value)||0),appUpdateMinutes:parseInt(g('mUpd').value)||30,wuScanHours:parseInt(g('mWu').value)||6,usbAudit:g('mUsb').checked};
  if(window.IS_HUB){b.enabled=g('mPush').checked;b.sites=[...document.querySelectorAll('.mSite:checked')].map(c=>c.value);}
  else{b.detailDays=parseInt(g('mDet').value)||7;b.keepDays=parseInt(g('mKeep').value)||90;b.tray=g('mTray').checked;}
  const r=await api('monitor-save',{method:'POST',body:JSON.stringify(b)});
  if(r&&r.ok){toast('Mérés-beállítás mentve','ok');ruMeasure();}else toast((r&&r.error)||'Nem sikerült','err');}
""";

    /// <summary>
    /// Programkérések (1.5.7): a gépek tálca-ikonjáról kért programok jóváhagyási sora – közös a szerver és a Központ
    /// felületén (a Központban a tételnek telephelye is van: a döntés a következő betárcsázáskor megy le).
    /// </summary>
    public const string RequestsJs = """
const RQ_STATE={pending:['döntésre vár','var(--warn)'],installing:['jóváhagyva – települ','var(--accent)'],installed:['települt','var(--ok)'],
  failed:['nem sikerült','var(--err)'],denied:['elutasítva','var(--muted)'],approved:['jóváhagyva','var(--accent)']};
let _rqAll=false;
async function showRequests(){const d=await api('requests',{method:'POST',body:'{}'});if(!d)return;if(typeof closeModal==='function')closeModal();
  const items=d.items||[],pend=items.filter(i=>i.state==='pending'),rest=items.filter(i=>i.state!=='pending'),hub=!!window.IS_HUB,w=window.ROLE>=1;
  const st=i=>{const s=RQ_STATE[i.state]||[i.state,'var(--muted)'];return `<span class="badge" style="color:${s[1]};border-color:${s[1]}">${esc(s[0])}</span>`;};
  const row=(i,k)=>`<tr>${hub?`<td>${esc(i.site)}</td>`:''}<td>${esc(i.machine)}<div style="font-size:11px;color:var(--muted)">${esc(i.user||'')}</div></td>
    <td><b>${esc(i.name)}</b><div class="mono" style="font-size:11px;color:var(--muted)">${esc(i.packageId)}</div>${i.reason?`<div style="font-size:12px;margin-top:2px">„${esc(i.reason)}”</div>`:''}</td>
    <td style="white-space:nowrap">${esc(i.created)}</td><td>${st(i)}${i.decidedBy?`<div style="font-size:11px;color:var(--muted)">${esc(i.decidedBy)}${i.decided?' · '+esc(i.decided):''}</div>`:''}${i.note?`<div style="font-size:11px">${esc(i.note)}</div>`:''}</td>
    <td style="text-align:right;white-space:nowrap">${w&&i.state==='pending'?(hub&&!i.supports?'<span style="font-size:11px;color:var(--muted)">a telephely szervere régi</span>'
      :`<button class="btn small primary" onclick="rqDecide(${k},true)">Jóváhagyás</button> <button class="btn small" onclick="rqDecide(${k},false)">Elutasítás</button>`):''}</td></tr>`;
  window._rq=items;
  const head=`<tr>${hub?'<th>Telephely</th>':''}<th>Gép / felhasználó</th><th>Program</th><th>Kérve</th><th>Állapot</th><th></th></tr>`;
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Programkérések</h2>
    <span class="sub">a gépek tálca-ikonjáról: a jóváhagyott program magától települ${hub?' (a döntés a telephely következő betárcsázásakor megy le)':''}</span></div>
    <div class="page-body"><div class="page-main"><div class="view" style="max-width:1200px">
      <h3 style="margin:0 0 8px">Döntésre vár (${pend.length})</h3>
      ${pend.length?`<table class="upd-table">${head}${pend.map(i=>row(i,items.indexOf(i))).join('')}</table>`:'<div class="empty">Nincs döntésre váró kérés.</div>'}
      <h3 style="margin:18px 0 8px">Korábbiak</h3>
      ${rest.length?`<table class="upd-table">${head}${rest.slice(0,_rqAll?500:50).map(i=>row(i,items.indexOf(i))).join('')}</table>
        ${rest.length>50&&!_rqAll?'<button class="btn small" style="margin-top:8px" onclick="_rqAll=true;showRequests()">Mind</button>':''}`:'<div class="empty">Még nincs.</div>'}
      <div style="font-size:12px;color:var(--muted);margin-top:12px">A tálca-ikon a Monitorozás → Mit mérjen? → „Tálca-ikon a gépeken” kapcsolóval kapcsolható ki (a telephely szerverén). A repóban levő programot a felhasználó kérés nélkül telepítheti; az elutasított (letiltott) programot nem kérheti.</div>
    </div></div></div>`);}
async function rqDecide(k,approve){const i=(window._rq||[])[k];if(!i)return;
  const note=approve?'':await uiPrompt('Elutasítás – megjegyzés a felhasználónak (nem kötelező):','');if(note===null)return;
  if(approve&&!await uiConfirm(`Jóváhagyod? A(z) „${i.name}” a(z) ${i.machine} gépre magától települ.`))return;
  const r=await api('request-decide',{method:'POST',body:JSON.stringify({siteId:i.siteId||'',id:i.id,approve,note})});
  if(r&&r.ok){toast(approve?(window.IS_HUB?'Jóváhagyva – a telephely a következő betárcsázáskor átveszi':'Jóváhagyva – a gép a következő jelentkezéskor telepíti'):'Elutasítva','ok');showRequests();}
  else toast((r&&r.error)||'Nem sikerült','err');}
""";

    /// <summary>
    /// Sérülékenységek (1.5.6): a Központ párosítása (NVD + CISA KEV + EPSS) – közös a szerver gép-oldalán, a Központ
    /// gép-oldalán és a Központ Sérülékenységek oldalán. cveSum(számok), cveTable(lista, {machines}) – a szűrés a DOM-ban.
    /// </summary>
    public const string CveJs = """
const CVE_SEV={CRITICAL:['kritikus','var(--err)'],HIGH:['magas','var(--warn)'],MEDIUM:['közepes','var(--accent)'],LOW:['alacsony','var(--muted)'],NONE:['–','var(--muted)']};
const CVE_KIND={app:'program',driver:'driver',bios:'BIOS'};
const CVE_NOTE='Forrás: NVD (NIST), CISA KEV (ténylegesen kihasznált hibák), EPSS (FIRST: a kihasználás valószínűsége 30 napon belül). '+
  'A párosítás óvatos: csak az NVD verziótartományai számítanak. A Microsoft saját termékeit (Windows, Office) a Windows-frissítések mutatják; '+
  'driverek közül az NVIDIA és az Intel grafikus driver, BIOS a Dell / HP / Lenovo gépeknél.';
function cveSev(f){const s=CVE_SEV[f.severity]||['?','var(--muted)'];
  return `<span class="badge" style="color:${s[1]};border-color:${s[1]};white-space:nowrap">${f.score?Number(f.score).toFixed(1)+' ':''}${s[0]}</span>`;}
function cveLink(id){return `<a href="https://nvd.nist.gov/vuln/detail/${encodeURIComponent(id)}" target="_blank" rel="noopener noreferrer" class="mono" style="color:var(--accent)">${esc(id)}</a>`;}
function cveSum(c){c=c||{};const t=(v,l,col)=>`<div class="tile"><div class="v" style="color:${v?col:'var(--ok)'}">${v||0}</div><div class="l">${l}</div></div>`;
  return `<div class="tiles">${t(c.exploited,'Ténylegesen kihasznált (KEV)','var(--err)')}${t(c.critical,'Kritikus','var(--err)')}${t(c.high,'Magas','var(--warn)')}${t(c.medium,'Közepes','var(--accent)')}</div>`;}
function cveTable(list,opt){opt=opt||{};list=list||[];if(!list.length)return '<div class="empty">✓ Nincs ismert sérülékenység.</div>';
  const id='cv'+Math.random().toString(36).slice(2,8);
  return `<div style="display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-bottom:8px">
      <input class="search" id="${id}q" style="width:280px;max-width:100%" placeholder="Szűrés: CVE, program, leírás…" oninput="cveFilter('${id}')">
      <label style="font-size:13px"><input type="checkbox" id="${id}k" onchange="cveFilter('${id}')"> csak KEV</label>
      <label style="font-size:13px"><input type="checkbox" id="${id}h" onchange="cveFilter('${id}')"> csak kritikus / magas</label>
      <span style="font-size:12px;color:var(--muted)">${list.length} tétel</span></div>
    <table class="upd-table" id="${id}"><tr><th>CVE</th><th>Súlyosság</th><th>Kihasználás</th>${opt.machines?'<th style="text-align:right">Gép</th>':''}<th>Érintett</th><th>Javítva</th></tr>
    ${list.map(f=>`<tr data-k="${f.kev?1:0}" data-h="${f.severity==='CRITICAL'||f.severity==='HIGH'?1:0}">
      <td style="white-space:nowrap">${cveLink(f.id)}<div style="font-size:11px;color:var(--muted)">${esc(f.published||'')}</div></td>
      <td>${cveSev(f)}</td>
      <td style="white-space:nowrap">${f.kev?`<span class="badge" style="color:var(--err);border-color:var(--err)" title="CISA KEV: ténylegesen kihasználják">KEV${f.ransomware?' · zsarolóvírus':''}</span> `:''}${f.epss?`<span style="font-size:12px" title="EPSS: a kihasználás valószínűsége 30 napon belül">${(f.epss*100).toFixed(f.epss<0.1?1:0)}%</span>`:''}</td>
      ${opt.machines?`<td style="text-align:right">${f.machines}</td>`:''}
      <td>${esc(f.product||'')}${f.version?` <span class="ver-old">${esc(f.version)}</span>`:''}${f.kind&&f.kind!=='app'?` <span class="badge">${esc(CVE_KIND[f.kind]||f.kind)}</span>`:''}
        <div style="font-size:11px;color:var(--muted);max-width:640px">${esc(f.summary||'')}</div></td>
      <td class="ver-new" style="white-space:nowrap">${esc(f.fixedIn||'')}</td></tr>`).join('')}</table>`;}
function cveFilter(id){const g=x=>document.getElementById(id+x),q=(g('q').value||'').toLowerCase(),k=g('k').checked,h=g('h').checked;
  document.querySelectorAll('#'+id+' tr[data-k]').forEach(r=>{r.style.display=(!k||r.dataset.k==='1')&&(!h||r.dataset.h==='1')&&(!q||r.textContent.toLowerCase().includes(q))?'':'none';});}
""";

    /// <summary>
    /// Oldalon belüli megerősítő / szövegbekérő ablak a böngésző confirm() / prompt() helyett (a felhasználó kérése, 1.5.6:
    /// a böngésző „… közlendője” ablaka idegennek hat). Közös a Fleet Server és a Fleet HUB felületén; egy nyitott ablak
    /// fölött is megjelenik. uiConfirm(szöveg) → Promise&lt;bool&gt;, uiPrompt(szöveg, alapérték) → Promise&lt;string|null&gt;.
    /// </summary>
    public const string DialogJs = """
function _dlgEsc(s){return String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}
function _dlg(msg,withInput,def){return new Promise(res=>{
  const back=document.createElement('div');back.className='modal-back show';back.style.zIndex='90';
  back.innerHTML=`<div class="modal" style="width:460px"><h3>${withInput?'Adat megadása':'Megerősítés'}</h3>
    <div style="font-size:14px;line-height:1.5;white-space:pre-wrap">${_dlgEsc(msg)}</div>
    ${withInput?`<input id="_dlgIn" style="margin-top:12px" value="${_dlgEsc(def||'')}">`:''}
    <div style="display:flex;gap:8px;justify-content:flex-end;margin-top:16px"><button class="btn small" id="_dlgNo">Mégse</button>
      <button class="btn small primary" id="_dlgOk">${withInput?'Rendben':'Igen'}</button></div></div>`;
  const done=v=>{document.removeEventListener('keydown',key,true);back.remove();res(v);};
  const val=()=>withInput?back.querySelector('#_dlgIn').value:true;
  const key=e=>{if(e.key==='Escape'){e.preventDefault();e.stopPropagation();done(withInput?null:false);}
    else if(e.key==='Enter'){e.preventDefault();e.stopPropagation();done(val());}};
  back.addEventListener('click',e=>{if(e.target===back)done(withInput?null:false);});
  document.body.appendChild(back);document.addEventListener('keydown',key,true);
  back.querySelector('#_dlgNo').onclick=()=>done(withInput?null:false);
  back.querySelector('#_dlgOk').onclick=()=>done(val());
  setTimeout(()=>{const i=back.querySelector(withInput?'#_dlgIn':'#_dlgOk');if(i){i.focus();if(withInput)i.select();}},30);});}
function uiConfirm(msg){return _dlg(msg,false);}
function uiPrompt(msg,def){return _dlg(msg,true,def);}
// ---- Haladásjelző (1.5.6, a felhasználó kérése): feltöltésnél százalék, MB és sebesség; hosszabb műveletnél „folyamatban” sáv.
function uiBar(pct,text){return `<div class="pbar${pct==null?' ind':''}"><div style="${pct==null?'':'width:'+Math.max(0,Math.min(100,pct)).toFixed(1)+'%'}"></div></div>`+
  (text?`<div style="font-size:12px;color:var(--muted);margin-top:4px">${_dlgEsc(text)}</div>`:'');}
function uiBusy(el,text){if(el)el.innerHTML=uiBar(null,text);}
function uiUpload(url,file,el){return new Promise(res=>{const x=new XMLHttpRequest(),t0=Date.now();x.open('POST',url);
  x.upload.onprogress=e=>{if(!e.lengthComputable||!el)return;const p=e.loaded/e.total*100,s=(Date.now()-t0)/1000,sp=s>0.3?e.loaded/s:0;
    // 1 MB = 1024 × 1024 bájt, mint a Windows fájlkezelőjében (különben a kijelzett méret eltérne tőle).
    el.innerHTML=uiBar(p,`${(e.loaded/1048576).toFixed(1)} / ${(e.total/1048576).toFixed(1)} MB · ${Math.round(p)}%`+(sp?` · ${(sp/1048576).toFixed(1)} MB/s`:'')+
      (sp&&p<100?` · még kb. ${Math.max(1,Math.round((e.total-e.loaded)/sp))} mp`:''));};
  x.upload.onload=()=>{if(el)el.innerHTML=uiBar(null,'Feltöltve – ellenőrzés a szerveren (fájl, verzió)…');};
  x.onload=()=>{if(x.status===401){location.href='/web';return;}let j=null;try{j=JSON.parse(x.responseText);}catch(e){}res(j||{ok:false,error:'HTTP '+x.status});};
  x.onerror=()=>res({ok:false,error:'Hálózati hiba a feltöltés közben.'});x.send(file);});}
""";

    /// <summary>
    /// SNMP-trap fogadás oldala (csak a telephelyi szerveren): beérkezett trapek, beállítások (admin), v3-felhasználók,
    /// útmutató az eszközök beállításához. Előfeltétel: api(), esc(), toast(), openModal(), pageOpen().
    /// </summary>
    public const string SnmpJs = """
// ---- SNMP (1.5.5): a jelszavak és a közösségnevek a böngészőbe nem jönnek vissza (a közösségnevet csak az admin látja).
let _snS=null,_snV='list',_snQ='',_snP=0,_snL=null,_snT=null;
const SN_VIEWS=[['list','Beérkezett trapek'],['set','Beállítások'],['users','SNMPv3-felhasználók'],['help','Útmutató']];
async function showSnmp(v){if(typeof closeModal==='function')closeModal();if(v)_snV=v;_snS=await api('snmp',{method:'POST',body:'{}'});if(!_snS){toast('Nem érhető el','err');return;}
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>SNMP</h2><span class="sub" id="snSub"></span></div>
    <div class="page-body"><div class="page-menu" id="snMenu"></div><div class="page-main" id="snMain"></div></div>`);snDraw();}
function snGo(v){_snV=v;snDraw();}
async function snReload(){_snS=await api('snmp',{method:'POST',body:'{}'})||_snS;snDraw();}
function snDraw(){const s=_snS;if(!document.getElementById('snMain'))return;
  document.getElementById('snMenu').innerHTML=SN_VIEWS.map(v=>`<button class="${_snV===v[0]?'sel':''}" onclick="snGo('${v[0]}')"><span>${v[1]}</span>${v[0]==='users'?`<span class="cnt">${s.users.length}</span>`:''}</button>`).join('');
  document.getElementById('snSub').innerHTML=s.enabled?`<span style="color:${s.status.startsWith('figyel')?'var(--ok)':'var(--err)'}">${esc(s.status)}</span>${s.lastAt?' · utolsó trap: '+esc(s.lastAt):''}${s.error?' · <span style="color:var(--err)">'+esc(s.error)+'</span>':''}`:'kikapcsolva';
  if(_snV==='list')return snList();if(_snV==='set')return snSet();if(_snV==='users')return snUsers();return snHelp();}
function snList(){document.getElementById('snMain').innerHTML=`<div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:10px">
    <input id="snQ" class="search" style="width:320px;max-width:100%" placeholder="Keresés: forrás, név, OID, szöveg…" value="${esc(_snQ)}" oninput="clearTimeout(_snT);_snT=setTimeout(()=>{_snQ=document.getElementById('snQ').value.trim();_snP=0;snRows();},300)">
    <button class="btn small" onclick="snRows()">Frissítés</button>${window.ROLE>=1&&_snS.enabled?'<button class="btn small" onclick="snTest()">Próba-trap küldése</button>':''}</div>
    ${_snS.enabled?'':'<div style="color:var(--warn);font-size:13px;margin-bottom:10px">Az SNMP-fogadás ki van kapcsolva – a Beállításokban kapcsolható be (admin).</div>'}
    <div id="snRows"><div class="empty">Betöltés…</div></div>`;snRows();}
async function snRows(){const d=await api('snmp-traps',{method:'POST',body:JSON.stringify({q:_snQ,page:_snP})});const box=document.getElementById('snRows');if(!box||!d)return;_snL=d;
  if(!d.items.length){box.innerHTML=`<div class="empty">${_snQ?'Nincs találat.':'Még nem érkezett trap.'}</div>`;return;}
  const pages=Math.max(1,Math.ceil(d.total/d.size));
  const pager=pages>1?`<div style="display:flex;gap:8px;align-items:center;margin:8px 0"><button class="btn small" ${_snP<=0?'disabled':''} onclick="_snP--;snRows()">‹ Előző</button>
    <span style="font-size:12px;color:var(--muted)">${_snP+1}. / ${pages} oldal</span><button class="btn small" ${_snP>=pages-1?'disabled':''} onclick="_snP++;snRows()">Következő ›</button></div>`:'';
  box.innerHTML=`<div style="font-size:12px;color:var(--muted);margin-bottom:6px">${d.total} trap (a legutóbbi 1000 látszik; minden a naplómappa snmp.log fájljában)</div>${pager}
    <table class="upd-table"><tr><th>Idő</th><th>Forrás</th><th>Trap</th><th>Verzió</th><th></th></tr>
    ${d.items.map((t,i)=>`<tr><td style="white-space:nowrap">${esc(t.at)}</td><td class="mono">${esc(t.source)}${t.agentAddr&&t.agentAddr!==t.source?'<div style="font-size:11px">ügynök: '+esc(t.agentAddr)+'</div>':''}</td>
      <td>${t.alert?'<span class="sbadge rejected">riaszt</span> ':''}<b>${esc(t.trapName)}</b>${t.inform?' <span class="sbadge auto">inform</span>':''}<div style="font-size:12px;color:var(--muted)">${esc(t.text)}</div></td>
      <td style="white-space:nowrap;font-size:12px">${esc(t.version)}<div style="color:var(--muted)">${esc(t.security)}</div></td>
      <td><button class="btn small" onclick="snShow(${i})">Részletek</button></td></tr>`).join('')}</table>${pager}`;}
function snShow(i){const t=_snL.items[i];
  openModal(`<h3>${esc(t.trapName)}</h3><div style="font-size:13px;color:var(--muted);margin-bottom:10px">${esc(t.at)} · ${esc(t.source)} · ${esc(t.version)} (${esc(t.security)})${t.uptime?' · az eszköz futásideje: '+esc(t.uptime):''}</div>
    <div class="mono" style="margin-bottom:8px">trap-OID: ${esc(t.trapOid)}</div>
    <table class="upd-table"><tr><th>Változó</th><th>Típus</th><th>Érték</th></tr>${t.varBinds.map(v=>`<tr><td>${esc(v.name)}${v.name!==v.oid?`<div class="mono" style="font-size:11px">${esc(v.oid)}</div>`:''}</td>
      <td style="font-size:12px">${esc(v.type)}</td><td style="word-break:break-all">${esc(v.value)}</td></tr>`).join('')||'<tr><td colspan="3" style="color:var(--muted)">Nincs változó.</td></tr>'}</table>
    <div style="margin-top:12px"><button class="btn small" onclick="closeModal()">Bezárás</button></div>`,true);}
async function snTest(){const r=await api('snmp-test',{method:'POST',body:'{}'});if(r&&r.ok){toast('A próba-trap megérkezett','ok');snRows();snReload();}else toast((r&&r.error)||'Nem sikerült','err');}
const SN_CNT=[['received','beérkezett csomag'],['accepted','elfogadott trap / inform'],['notAllowed','nem megengedett címről'],['unknownCommunity','ismeretlen közösségnév'],
  ['unknownUser','ismeretlen v3-felhasználó'],['wrongDigest','hibás hitelesítés'],['decryptErrors','visszafejtési hiba'],['unsupportedLevel','eltérő biztonsági szint'],
  ['notInTime','időablakon kívüli inform (újraszinkron)'],['replayed','ismételt (visszajátszott)'],['discovery','v3-felderítés'],['parseErrors','hibás csomag'],['rateLimited','terheléskorlát miatt eldobva'],['tooBig','túl nagy csomag']];
function snSet(){const s=_snS,a=window.ROLE>=2,dis=a?'':'disabled';
  document.getElementById('snMain').innerHTML=`<div class="view">
    ${a?'':'<div style="color:var(--muted);font-size:13px;margin-bottom:10px">A beállítást csak admin módosíthatja.</div>'}
    <label style="display:flex;gap:6px;align-items:center;color:var(--ink)"><input type="checkbox" id="snOn" ${s.enabled?'checked':''} ${dis}> SNMP-trap fogadás bekapcsolva</label>
    <div class="row"><div class="col"><label>UDP-port (szabványos: 162)</label><input id="snPort" type="number" value="${s.port}" ${dis}></div><div class="col"></div></div>
    <label>Közösségnevek (community) a v1 / v2c üzenetekhez – soronként; üresen a v1 / v2c tiltva (csak v3)</label>
    ${a?`<textarea id="snCom" rows="3">${esc((s.communities||[]).join('\n'))}</textarea>`:`<div style="font-size:13px">${s.communityCount} közösségnév beállítva (csak admin látja).</div>`}
    <label>Fogadás csak ezekről a címekről / tartományokból (soronként, pl. 192.168.1.0/24 vagy 10.0.0.5) – üresen a belső (magán) hálózatok</label>
    <textarea id="snAllow" rows="3" ${dis} placeholder="${esc(s.defaultAllow.join('\n'))}">${esc(s.allowFrom.join('\n'))}</textarea>
    <label>Riasztást adó trapek: trap-OID vagy előtag, soronként (* = minden trap)</label>
    <textarea id="snAlert" rows="4" ${dis}>${esc(s.alertOids.join('\n'))}</textarea>
    <div style="font-size:12px;color:var(--muted);margin-top:4px">Gyakoriak: 1.3.6.1.6.3.1.1.5.1 coldStart · 1.3.6.1.6.3.1.1.5.2 warmStart · 1.3.6.1.6.3.1.1.5.3 linkDown · 1.3.6.1.6.3.1.1.5.4 linkUp ·
      1.3.6.1.6.3.1.1.5.5 authenticationFailure · 1.3.6.1.2.1.33.2 UPS-riasztások. A riasztás a riasztásnaplóba kerül, és a Központba is felmegy (e-mail, Janus).</div>
    ${a?'<button class="btn primary small" style="margin-top:12px" onclick="snSave()">Mentés</button>':''}
    <h3>Számlálók (a szolgáltatás indulása óta)</h3>
    <table class="upd-table">${SN_CNT.map(c=>`<tr><td>${c[1]}</td><td style="text-align:right" class="mono">${s.counters[c[0]]||0}</td></tr>`).join('')}</table></div>`;}
const snLines=id=>(document.getElementById(id)?.value||'').split(/[\r\n]+/).map(x=>x.trim()).filter(x=>x);
async function snSave(){const on=document.getElementById('snOn').checked;
  if(on&&!_snS.enabled&&!await uiConfirm('Bekapcsolod az SNMP-trap fogadást? A szerver UDP-portot nyit (a tűzfal csak a megengedett címekről enged be).'))return;
  const r=await api('snmp-save',{method:'POST',body:JSON.stringify({enabled:on,port:parseInt(document.getElementById('snPort').value)||162,
    communities:snLines('snCom'),allowFrom:snLines('snAllow'),alertOids:snLines('snAlert')})});
  if(r&&r.ok)toast('SNMP-beállítás mentve','ok');else toast((r&&r.error)||'Nem sikerült','err');await snReload();}
function snUsers(){const s=_snS,a=window.ROLE>=2;
  document.getElementById('snMain').innerHTML=`<div class="view"><p style="font-size:13px;color:var(--muted);margin:0 0 10px">SNMPv3 (USM): az eszközön ugyanezzel a felhasználónévvel, protokollokkal és jelszavakkal küldd a trapet.
    A jelszavak titkosítva tárolódnak, és nem jönnek vissza a felületre. A „c” végű AES (aes192c / aes256c) és a 3DES a Cisco-féle kulcsbővítés; a sima aes192 / aes256 a Net-SNMP-féle.</p>
    ${a?'<button class="btn small primary" style="margin-bottom:10px" onclick="snUserEdit(-1)">+ Új felhasználó</button>':''}
    <table class="upd-table"><tr><th>Felhasználó</th><th>Hitelesítés</th><th>Titkosítás</th><th></th></tr>
    ${s.users.map((u,i)=>`<tr><td><b>${esc(u.name)}</b></td><td>${esc(u.auth)}</td><td>${esc(u.priv)}</td>
      <td style="text-align:right;white-space:nowrap">${a?`<button class="btn small" onclick="snUserEdit(${i})">Szerkesztés</button> <button class="btn small danger" onclick="snUserDel(${i})">Törlés</button>`:''}</td></tr>`).join('')
      ||'<tr><td colspan="4" style="color:var(--muted)">Még nincs v3-felhasználó.</td></tr>'}</table>
    <div style="font-size:12px;color:var(--muted);margin-top:10px">A szerver SNMP engineID-ja (a v3 informhoz – a legtöbb eszköz magától felderíti): <span class="mono">${esc(s.engineId||'(a bekapcsoláskor készül)')}</span></div></div>`;}
function snUserEdit(i){const s=_snS,u=i>=0?s.users[i]:{name:'',auth:'sha256',priv:'aes128'};window._snU=i;
  const opt=(list,cur)=>list.map(x=>`<option value="${x}" ${x===cur?'selected':''}>${x}</option>`).join('');
  openModal(`<h3>${i>=0?'SNMPv3-felhasználó: '+esc(u.name):'Új SNMPv3-felhasználó'}</h3>
    <label>Felhasználónév</label><input id="suN" value="${esc(u.name)}" ${i>=0?'disabled':''} maxlength="32">
    <div class="row"><div class="col"><label>Hitelesítés</label><select id="suA" class="grpsel" style="width:100%">${opt(s.authNames,u.auth)}</select></div>
      <div class="col"><label>Hitelesítési jelszó (min. 8)</label><input id="suAP" type="password" autocomplete="new-password" placeholder="${i>=0&&u.hasAuth?'üresen hagyva marad':''}"></div></div>
    <div class="row"><div class="col"><label>Titkosítás</label><select id="suP" class="grpsel" style="width:100%">${opt(s.privNames,u.priv)}</select></div>
      <div class="col"><label>Titkosítási jelszó (min. 8)</label><input id="suPP" type="password" autocomplete="new-password" placeholder="${i>=0&&u.hasPriv?'üresen hagyva marad':''}"></div></div>
    <div style="font-size:12px;color:var(--muted);margin-top:8px">Hitelesítés nélkül (none) titkosítás sincs. Ajánlott: sha256 + aes128 (vagy amit az eszköz tud).</div>
    <div style="display:flex;gap:8px;margin-top:12px"><button class="btn small" onclick="closeModal()">Mégse</button><button class="btn small primary" onclick="snUserSave()">Mentés</button></div>`);}
async function snUserSave(){const g=id=>document.getElementById(id).value;
  const r=await api('snmp-user-save',{method:'POST',body:JSON.stringify({name:g('suN').trim(),auth:g('suA'),authPassword:g('suAP'),priv:g('suP'),privPassword:g('suPP')})});
  if(r&&r.ok){closeModal();toast('Felhasználó mentve'+(r.error?' – '+r.error:''),r.error?'err':'ok');await snReload();}else toast((r&&r.error)||'Nem sikerült','err');}
async function snUserDel(i){const u=_snS.users[i];if(!await uiConfirm(`Törlöd a(z) „${u.name}” v3-felhasználót? Az ő trapjeit ezután nem fogadjuk.`))return;
  const r=await api('snmp-user-delete',{method:'POST',body:JSON.stringify({name:u.name})});if(r&&r.ok){toast('Törölve','ok');await snReload();}else toast((r&&r.error)||'Nem sikerült','err');}
function snHelp(){const s=_snS,ip=s.serverIp||'<szerver IP-címe>';
  document.getElementById('snMain').innerHTML=`<div class="view"><h3 style="margin-top:0">Az eszköz (switch, router, UPS, nyomtató, NAS…) beállítása</h3>
    <ol style="font-size:13px;line-height:1.7"><li>Trap-cél (trap receiver / notification host): <b class="mono">${esc(ip)}</b>, UDP-port <b>${s.port}</b>.</li>
      <li><b>v1 / v2c:</b> a közösségnév (community) egyezzen a Beállításokban megadottal. <b>v2c inform:</b> a szerver nyugtázza (Response).</li>
      <li><b>v3:</b> vegyél fel itt egy felhasználót, és az eszközön ugyanazt add meg (név, hitelesítési protokoll + jelszó, titkosítási protokoll + jelszó).
        Traphez az eszköz a saját engineID-jét küldi – itt nem kell megadni. <b>v3 inform:</b> a szerver a hiteles fél, az eszköz felderíti az engineID-t (${esc(s.engineId||'bekapcsoláskor készül')}).</li>
      <li>A szerver csak a megengedett (alapból belső) címekről fogad; a Windows tűzfal is csak onnan engedi be.</li></ol>
    <h3>Ha nem érkezik semmi</h3>
    <ul style="font-size:13px;line-height:1.7"><li>A 162-es portot a Windows „SNMP Trap” szolgáltatása is foglalhatja – ilyenkor a Beállításoknál ezt írja; állítsd le (services.msc), vagy használj más portot.</li>
      <li>Nézd meg a Beállítások alatti számlálókat: ismeretlen közösségnév / felhasználó, hibás hitelesítés, visszafejtési hiba, nem megengedett cím.</li>
      <li>Próba Net-SNMP-vel egy másik gépről:<div class="mono" style="margin-top:4px">snmptrap -v 2c -c public ${esc(ip)}:${s.port} '' 1.3.6.1.6.3.1.1.5.3</div>
        <div class="mono">snmptrap -v 3 -u felhasznalo -l authPriv -a SHA-256 -A jelszo1234 -x AES -X jelszo5678 ${esc(ip)}:${s.port} '' 1.3.6.1.6.3.1.1.5.1</div></li></ul></div>`;}
""";

    /// <summary>
    /// Tárolás (admin) – közös a FleetServer és a Központ Beállítások oldalán: a naplók, a mentések és a mérési
    /// adatbázis helye (Storage.cs). Előfeltétel: api(), esc(), toast(), és egy #storageBox elem az oldalon.
    /// </summary>
    public const string StorageJs = """
// ---- Tárolás: üres mező = alapértelmezés. Mentéskor a fájlok átköltöznek; új mappa jogai: csak SYSTEM + rendszergazdák.
async function loadStorage(){const box=document.getElementById('storageBox');if(!box)return;
  const s=await api('storage',{method:'POST',body:'{}'});if(!s||s.error||!s.items){box.innerHTML='';return;}
  box.innerHTML=`<div style="font-weight:600;margin-bottom:8px">Tárolás – naplók, mentések, mérési adatbázis</div>
    <div style="font-size:12px;color:var(--muted);margin-bottom:10px">Üresen hagyva az alapértelmezett hely marad. Mentéskor a meglévő fájlok átköltöznek az új helyre
      (a naplók azonnal, a mérési adatbázis rövid szünettel – addig a beérkező mérések várnak). Helyi meghajtón levő, új vagy üres mappát adj meg:
      a jogait a program a SYSTEM-re és a rendszergazdákra szűkíti. A kulcsok és a beállítások az adatmappában maradnak:
      <span class="mono">${esc(s.dataDir)}</span> (${esc(s.dataSize)}).</div>
    <table class="upd-table"><tr><th>Mit</th><th>Jelenlegi hely</th><th>Méret</th></tr>
    ${s.items.map(i=>`<tr><td><b>${esc(i.label)}</b><div style="font-size:11px;color:var(--muted)">${esc(i.what)}</div></td>
      <td><span class="mono" style="color:var(--ink)">${esc(i.path)}</span>${i.custom?'':' <span class="sbadge auto">alapértelmezett</span>'}</td>
      <td style="white-space:nowrap">${esc(i.size)}</td></tr>`).join('')}</table>
    ${s.items.map(i=>`<label>${esc(i.label)} – hely (üresen: ${esc(i.def)})</label><input id="st_${i.key}" value="${esc(i.custom||'')}" placeholder="${esc(i.def)}">`).join('')}
    <label>Naplófájl legnagyobb mérete (MB) – fölötte a régebbi a .1 / .old fájlba kerül</label><input id="st_mb" type="number" min="1" max="100" value="${s.logMaxMb}" style="max-width:160px">
    <div style="display:flex;gap:10px;align-items:center;margin-top:12px"><button class="btn primary small" onclick="saveStorage()">Tárolás mentése</button><span id="stRes" style="font-size:12px"></span></div>`;}
async function saveStorage(){const v=id=>((document.getElementById(id)||{}).value||'').trim();
  const body={logDir:v('st_logs'),backupDir:v('st_backup'),metricsDir:v('st_metrics'),logMaxMb:parseInt(v('st_mb'))||2};
  const res=document.getElementById('stRes');uiBusy(res,'Mentés, átköltöztetés…');
  const r=await api('storage-save',{method:'POST',body:JSON.stringify(body)});
  if(!r||!r.ok){res.innerHTML=`<span style="color:var(--err)">${esc((r&&r.error)||'Nem sikerült')}</span>`;return;}
  toast(r.changed&&r.changed.length?'Tárolás mentve – '+r.changed.join('; '):'Nem változott semmi','ok');loadStorage();}
""";

    /// <summary>
    /// Gépcsoportok teljes oldalon (közös a FleetServer és a Központ felületén; window.IS_HUB: telephelyenként).
    /// Csoport létrehozása, tagok kijelölése, átnevezés, törlés; a Központban a művelet a telephelyre megy le.
    /// Automatikus csoportok (1.5.4): csak olvashatók. Itt van a gépek listájának csoportosítása is (machGroups).
    /// A csoportra kiadható műveletek gombjait az oldal adja: grActions(i) – i a csoport indexe a _gl tömbben.
    /// </summary>
    public const string GroupsJs = """
// ---- Eszköz-összegzés (1.5.6): Biztonság és Állapot (0–100), átlaguk, és ami pontot vont le (szerver és Központ gép-oldala).
function scoreCol(s){return s>=85?'var(--ok)':s>=65?'var(--accent)':s>=40?'var(--warn)':'var(--err)';}
function scoreBox(s){if(!s)return'';
  const part=(l,v)=>`<div style="flex:1;min-width:120px"><div style="font-size:12px;color:var(--muted)">${l}</div><div style="font-size:20px;font-weight:700;color:${scoreCol(v)}">${v}</div>
    <div class="pbar"><div style="width:${v}%;background:${scoreCol(v)}"></div></div></div>`;
  return `<div style="background:var(--panel2);border:1px solid var(--line);border-radius:10px;padding:12px;margin-bottom:12px">
    <div style="display:flex;gap:16px;align-items:center;flex-wrap:wrap"><div><div style="font-size:12px;color:var(--muted)">Eszköz-összegzés</div>
      <div style="font-size:28px;font-weight:700;color:${scoreCol(s.score)}">${s.score}<span style="font-size:13px;font-weight:500;color:var(--muted)"> / 100 · ${esc(s.grade)}</span></div></div>
      ${part('Biztonság',s.security)}${part('Állapot',s.health)}</div>
    ${(s.items||[]).length?'<ul style="margin:10px 0 0;padding-left:18px;font-size:13px">'+s.items.map(x=>`<li style="color:${x.level==='err'?'var(--err)':'var(--warn)'}">${esc(x.text)} <span style="color:var(--muted);font-size:11px">(${esc(x.area)}, −${x.penalty})</span></li>`).join('')+'</ul>'
      :'<div style="font-size:12px;color:var(--ok);margin-top:8px">Nincs levonás – a gép rendben van.</div>'}</div>`;}
function scoreBadge(s){return s?`<span class="badge" title="Eszköz-összegzés: Biztonság ${s.security}, Állapot ${s.health}" style="color:${scoreCol(s.score)};border-color:${scoreCol(s.score)}">${s.score}</span>`:'';}

// ---- A gépek listájának csoportosítása (szerver főoldal, Központ: a telephely gépei). by: kind | os | hw | grp | '' (nincs).
//      a.classes: a szerver besorolása (Szerverek / Kliensek, Windows-verzió, Virtuális / Fizikai gépek), a.groups: kézi csoportok.
const MG_LABEL={'':'Nincs csoportosítás',kind:'Szerver / kliens',os:'Windows-verzió',hw:'Virtuális / fizikai',grp:'Csoport'};
let _mgBy='',_mgClosed=new Set();
try{_mgBy=localStorage.getItem('mgBy')||'';_mgClosed=new Set(JSON.parse(localStorage.getItem('mgClosed')||'[]'));}catch(e){}
function mgSet(v){_mgBy=MG_LABEL[v]!==undefined?v:'';try{localStorage.setItem('mgBy',_mgBy);}catch(e){}}
function mgToggle(k){if(_mgClosed.has(k))_mgClosed.delete(k);else _mgClosed.add(k);try{localStorage.setItem('mgClosed',JSON.stringify([..._mgClosed]));}catch(e){}}
function mgKeys(a,by){const c=a.classes||[];
  if(by==='kind')return[c.find(g=>g==='Szerverek'||g==='Kliensek')||'Ismeretlen'];
  if(by==='os')return[c.find(g=>/^Windows /.test(g))||'Ismeretlen'];
  if(by==='hw')return[c.find(g=>g==='Virtuális gépek'||g==='Fizikai gépek')||'Ismeretlen'];
  if(by==='grp')return(a.groups||[]).length?a.groups:['Csoport nélkül'];
  return[''];}
// [név, gépek] párok: a „Szerverek” előbb, az ismeretlen / csoport nélküli a végén, egyébként név szerint (számokkal helyesen).
function machGroups(list,by){const m=new Map();list.forEach(a=>mgKeys(a,by).forEach(k=>{if(!m.has(k))m.set(k,[]);m.get(k).push(a);}));
  const rank=k=>k==='Szerverek'?0:k==='Ismeretlen'||k==='Csoport nélkül'?2:1;
  return[...m.entries()].sort((x,y)=>rank(x[0])-rank(y[0])||x[0].localeCompare(y[0],'hu',{numeric:true}));}
function mgSel(id,onch){return `<select id="${id}" class="grpsel" onchange="${onch}" title="A gépek csoportosítása">`+
  Object.entries(MG_LABEL).map(([k,v])=>`<option value="${k}" ${k===_mgBy?'selected':''}>${v}</option>`).join('')+'</select>';}
function mgHead(key,label,list){const closed=_mgClosed.has(key),on=list.filter(a=>a.online&&!a.revoked).length;
  return {closed,html:`${closed?'▸':'▾'} ${esc(label)}<span>${list.length} gép · ${on} online</span>`};}

// ---- Gépcsoportok (közös a FleetServer és a Központ felületén)
let _grD=null,_grS=0;
async function showGroups(i){if(typeof closeModal==='function')closeModal();if(i!=null)_grS=i;
  const d=await api('groups',{method:'POST',body:'{}'});if(!d){toast('Nem érhető el','err');return;}_grD=d;const hub=!!window.IS_HUB;
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Gépcsoportok</h2>
    <span class="sub">${hub?'Telephelyenként; a módosítás a következő betárcsázáskor megy le':'A csoportra szóló műveletek a csoport összes gépére mennek, a következő jelentkezéskor'}</span>
    ${window.ROLE>=1?`<label style="display:flex;gap:6px;align-items:center;font-size:13px;color:var(--muted);margin:0" title="Szerverek / Kliensek, Windows-verzió, Virtuális / Fizikai gépek – a gépek adataiból, magától">
      <input type="checkbox" ${d.autoOn!==false?'checked':''} onchange="grAuto(this.checked)"> automatikus csoportok</label>
      <button class="btn small primary" onclick="grNew()">+ Új csoport</button>`:''}</div>
    <div class="page-body">${hub?'<div class="page-menu" id="grMenu"></div>':''}<div class="page-main" id="grMain"></div></div>`);
  grDraw();}
function grSite(){return window.IS_HUB?(_grD.sites||[])[_grS]:_grD;}
function grDraw(){const main=document.getElementById('grMain');if(!main||!_grD)return;
  if(window.IS_HUB)document.getElementById('grMenu').innerHTML=(_grD.sites||[]).map((s,i)=>`<button class="${_grS===i?'sel':''}" onclick="_grS=${i};grDraw()"><span>${esc(s.name)}</span><span class="cnt">${(s.groups||[]).length}</span></button>`).join('');
  const s=grSite();if(!s){main.innerHTML='<div class="empty">Nincs telephely.</div>';return;}
  const ag=s.agents||[],nm=id=>(ag.find(a=>a.agentId===id)||{machineName:'?'}).machineName;_gl=(s.groups||[]).map(g=>g.name);
  const e=window.ROLE>=1&&(!window.IS_HUB||s.supports),none=ag.filter(a=>!(a.groups||[]).length).length;
  main.innerHTML=`<div class="view">${window.IS_HUB&&!s.supports?'<div style="color:var(--warn);font-size:13px;margin-bottom:10px">Ez a telephely még 1.5.0 előtti: a csoportok itt csak megtekinthetők – előbb frissítsd a telephely szerverét.</div>':''}
    ${window.IS_HUB&&s.pending?`<div style="color:var(--warn);font-size:13px;margin-bottom:10px">${s.pending} csoport-művelet vár a következő betárcsázásra.</div>`:''}
    <table class="upd-table"><tr><th>Csoport</th><th>Gépek</th><th></th></tr>
    ${(s.groups||[]).map((g,i)=>`${g.auto&&!(s.groups[i-1]||{}).auto?'<tr><td colspan="3" style="padding-top:18px;font-size:11px;text-transform:uppercase;letter-spacing:.5px;color:var(--accent);font-weight:600">Automatikus csoportok – az operációs rendszer és a hardver szerint</td></tr>':''}
      <tr><td><b>${esc(g.name)}</b>${g.auto?' <span class="sbadge auto">automatikus</span>':''}<div style="font-size:12px;color:var(--muted)">${g.members.length} gép</div></td>
      <td style="font-size:12px">${g.members.slice(0,8).map(id=>esc(nm(id))).join(', ')}${g.members.length>8?' …':''}${g.members.length?'':'<span style="color:var(--muted)">üres</span>'}</td>
      <td style="text-align:right;white-space:nowrap">${e&&!g.auto?`<button class="btn small" onclick="grMembers(${i})">Tagok…</button> <button class="btn small" onclick="grRename(${i})">Átnevezés</button> <button class="btn small danger" onclick="grDelete(${i})">Törlés</button>`:''}
        ${window.ROLE>=1&&g.members.length&&typeof grActions==='function'?'<div style="margin-top:6px">'+grActions(i)+'</div>':''}</td></tr>`).join('')
      ||'<tr><td colspan="3" style="color:var(--muted)">Még nincs csoport.</td></tr>'}</table>
    <div style="font-size:12px;color:var(--muted);margin-top:10px">${ag.length} gép, ebből kézi csoport nélkül: ${none}. Egy gép több csoportban is lehet. A csoport (az automatikus is) a szabályok, a kiesés-figyelés és a küszöb-szabályok hatóköre is.
      Az automatikus csoportok tagsága a gépek adataiból magától alakul (Szerverek / Kliensek, Windows-verzió, Virtuális / Fizikai gépek), kézzel nem módosítható.</div></div>`;}
async function grAuto(on){const r=await api('auto-groups',{method:'POST',body:JSON.stringify({on})});
  if(!r||!r.ok){toast((r&&r.error)||'Nem sikerült','err');return;}toast(on?'Automatikus csoportok bekapcsolva':'Automatikus csoportok kikapcsolva','ok');await showGroups();if(typeof refresh==='function')refresh();}
function grMembers(i){const s=grSite(),g=s.groups[i];window._gm=i;
  openModal(`<h3>Tagok – ${esc(g.name)}</h3><input id="gmF" placeholder="Szűrés…" oninput="fltRows('gmF','gmrow')" style="margin-bottom:8px">
    <div style="display:flex;gap:8px;margin-bottom:6px"><button class="btn small" onclick="document.querySelectorAll('.gmC').forEach(c=>{if(c.closest('.gmrow').style.display!=='none')c.checked=true})">Mind</button>
      <button class="btn small" onclick="document.querySelectorAll('.gmC').forEach(c=>c.checked=false)">Egyik sem</button></div>
    <div style="max-height:340px;overflow:auto">${(s.agents||[]).map(a=>`<label class="gmrow" data-n="${esc(a.machineName.toLowerCase())}" style="display:flex;gap:8px;align-items:center;margin:4px 0">
      <input type="checkbox" class="gmC" value="${esc(a.agentId)}" ${g.members.includes(a.agentId)?'checked':''}> ${esc(a.machineName)}
      <span style="font-size:11px;color:var(--muted)">${esc((a.groups||[]).filter(x=>x!==g.name).join(', '))}</span></label>`).join('')||'<div class="empty">Nincs gép.</div>'}</div>
    <div style="display:flex;gap:8px;margin-top:10px"><button class="btn small" onclick="closeModal()">Mégse</button><button class="btn small primary" onclick="grMembersSave()">Mentés</button></div>`);}
function grMembersSave(){const g=grSite().groups[window._gm];grOp({op:'members',group:g.name,agentIds:[...document.querySelectorAll('.gmC:checked')].map(c=>c.value)});}
async function grRename(i){const g=grSite().groups[i];const n=(await uiPrompt('Új név:',g.name)||'').trim();if(n&&n!==g.name)grOp({op:'rename',group:g.name,newName:n});}
async function grDelete(i){const g=grSite().groups[i];if(await uiConfirm(`Törlöd a(z) „${g.name}” csoportot? A gépek kikerülnek belőle (a gépek maradnak); a rá szűkített automatikus és küszöb-szabályok kikapcsolódnak.`))grOp({op:'delete',group:g.name});}
async function grNew(){const s=grSite();if(window.IS_HUB&&(!s||!s.supports)){toast('Ez a telephely még nem ismeri a csoport-műveleteket','err');return;}
  const n=(await uiPrompt('Az új csoport neve (pl. Könyvelés, Szerverek):')||'').trim();if(!n)return;
  if(await grOp({op:'create',group:n},true)){const k=(grSite().groups||[]).findIndex(g=>g.name.toLowerCase()===n.toLowerCase());if(k>=0)grMembers(k);}}
async function grOp(o,quiet){if(window.IS_HUB)o.siteId=grSite().siteId;
  const r=await api('group-op',{method:'POST',body:JSON.stringify(o)});
  if(!r||!r.ok){toast((r&&r.error)||'Nem sikerült','err');return false;}
  if(!quiet)toast(window.IS_HUB?'Kérés rögzítve – a telephely a következő betárcsázáskor végrehajtja':'Mentve','ok');
  closeModal();await showGroups();if(typeof refresh==='function')refresh();return true;}
""";

    /// <summary>
    /// Programtar es alkalmazascsomagok - kozos a FleetServer es a Kozpont feluleten (mindketto behuzza).
    /// Elofeltetel az oldalon: api(), esc(), toast(), openModal(); window.CAT_MIN_ROLE = a szerkeszteshez kello szerepkor.
    /// </summary>
    public const string CatalogJs = """
// ---- Programtár („saját repó”) teljes oldalon (közös a FleetServer és a Központ felületén):
//      saját repó · döntésre vár (a winget-tárba újonnan került) · teljes winget-tár · elutasított · gépeken talált · csomagok.
//      Minden döntés azonnal a szerverre megy (catalog-decide); a listák szerveroldalon lapoznak és keresnek.
let _cat=[],_bun=[],_hcat=[],_hbun=[],_cst={wg:{}},_cv='repo',_cq='',_cp=0,_cl=null,_cqT=null,_cDec=false,_cc='',_cpub='',_ccats=[];
const catEdit=()=>window.ROLE>=(window.CAT_MIN_ROLE||1);
// _hcat / _hbun: a Központ repója és csomagjai (csak a telephelyen; a Központ a mester - itt csak olvashatók).
// pub / cat: a program fejlesztője és kategóriája (a szerver adja a winget-tárból).
const catItem=i=>({id:i.id,name:i.name,pub:i.publisher||'',cat:i.category||'Egyéb'});
async function loadCatalog(){const c=(await api('catalog',{method:'POST',body:'{}'}))||{};
  _cat=(c.items||[]).map(catItem);_bun=(c.bundles||[]).map(b=>({id:b.id,name:b.name,apps:b.apps||[]}));
  _hcat=(c.hubItems||[]).map(catItem);_hbun=(c.hubBundles||[]).map(b=>({id:b.id,name:b.name,apps:b.apps||[],hub:true}));
  _ccats=c.categories||[];
  _cst={pending:c.pendingCount||0,rejected:c.rejectedCount||0,wg:c.winget||{},undecided:c.undecided};}
// A telepíthető programok: a saját repó és a Központ repója (ami mindkettőben van, egyszer).
const catAll=()=>_cat.concat(_hcat.filter(h=>!_cat.some(c=>c.id.toLowerCase()===h.id.toLowerCase())));
const catName=id=>(catAll().find(c=>c.id.toLowerCase()===String(id).toLowerCase())||{name:id}).name;
const CAT_VIEWS=[['repo','Saját repó'],['pending','Döntésre vár'],['all','Teljes winget-tár'],['rejected','Elutasított'],['inv','Gépeken talált'],['bun','Alkalmazáscsomagok']];
const CAT_INFO={repo:'A saját repód: telepítéskor (gépre, csoportra, alkalmazáscsomagba) innen választasz. Telephelyen a Központ repója is itt van (a Központ a mester – azok itt csak olvashatók).',
  pending:'Csak a winget-tárba újonnan (az előző napi letöltés óta) került programok, 30 napig. Döntsd el, kell-e a repódba – amit elutasítasz, az Elutasított alá kerül, és többé nem kérdezzük; amiről nem döntesz, 30 nap után döntés nélkül a teljes tárban marad.',
  all:'A teljes winget-tár (a Microsoft hivatalos forrása, naponta frissül) – a már eldöntött (repóban levő vagy elutasított) programok nélkül. Szűrj kategóriára vagy fejlesztőre (kattints a nevére), keress, és vedd fel a repódba, ami kell.',
  rejected:'Elutasított programok: nem kerülnek a repóba, és újra sem jelennek meg döntésre. Innen visszavehetők.'};
async function showCatalog(view){if(typeof closeModal==='function')closeModal();if(view)_cv=view;_cq='';_cp=0;await loadCatalog();
  pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Programtár</h2><span class="sub" id="catWg"></span>
    ${catEdit()?'<button class="btn small" onclick="catRefreshWg()">Winget-tár frissítése most</button>':''}</div>
    <div class="page-body"><div class="page-menu" id="catMenu"></div><div class="page-main" id="catMain"></div></div>`);
  catDraw();}
function catGo(v){_cv=v;_cq='';_cp=0;_cc='';_cpub='';catDraw();}
function catSetCat(c){_cc=c;_cp=0;catList();}
function catPubI(i){const a=_cl&&_cl.items[i];if(a){_cpub=a.publisher;_cc='';_cp=0;catList();}}
function catPubClear(){_cpub='';_cp=0;catList();}
// Kézi kategória a repó programjának (1.5.6, a felhasználó kérése: az „Egyéb” besorolásúak rendezése).
async function catSetItem(i,c){const a=_cl&&_cl.items[i];if(!a)return;
  const r=await api('catalog-category',{method:'POST',body:JSON.stringify({id:a.id,category:c})});
  if(r&&r.ok){toast(`${a.name}: ${c}`,'ok');await loadCatalog();catList();}else toast((r&&r.error)||'Nem sikerült','err');}
function catDraw(){if(!document.getElementById('catMain'))return;const w=_cst.wg||{};
  const cnt={repo:catAll().length,pending:_cst.pending,all:_cst.undecided??(w.count||0),rejected:_cst.rejected,inv:'',bun:_bun.length+_hbun.length};
  document.getElementById('catMenu').innerHTML=CAT_VIEWS.map(v=>`<button class="${_cv===v[0]?'sel':''}" onclick="catGo('${v[0]}')"><span>${v[1]}</span>
    <span class="cnt" ${v[0]==='pending'&&cnt.pending>0?'style="color:var(--warn);font-weight:600"':''}>${cnt[v[0]]}</span></button>`).join('');
  document.getElementById('catWg').innerHTML=`Winget-tár: ${w.count||0} program · ${w.updated?'frissítve '+esc(w.updated)+(w.source?' ('+esc(w.source)+')':''):'még nincs letöltve'}`+
    (w.refreshing?' · <span style="color:var(--warn)">frissítés folyamatban…</span>':'')+(w.error?` · <span style="color:var(--err)">${esc(w.error)}</span>`:'');
  if(_cv==='inv')return catInv();
  if(_cv==='bun')return catBundles();
  const e=catEdit();
  document.getElementById('catMain').innerHTML=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">${CAT_INFO[_cv]}</p>
    <div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:10px">
      <input id="catQ" class="search" style="width:320px;max-width:100%" placeholder="Keresés név vagy azonosító szerint…" value="${esc(_cq)}" oninput="catSearch()">
      ${e&&_cv!=='rejected'?'<button class="btn small" onclick="catBulk(\'accept\')">Kijelöltek felvétele</button>':''}
      ${e&&_cv!=='rejected'?'<button class="btn small danger" onclick="catBulk(\'reject\')">Kijelöltek elutasítása</button>':''}
      ${e&&_cv==='rejected'?'<button class="btn small" onclick="catBulk(\'unreject\')">Kijelöltek visszavétele</button>':''}
      ${e&&_cv==='pending'&&_cst.pending>0?'<button class="btn small danger" onclick="catAllPending(\'reject\')">Mindet elutasítom</button>':''}
      ${e&&_cv==='repo'?'<button class="btn small" onclick="catManual()">+ Azonosító kézzel</button>':''}
      ${_cv==='all'?`<label style="display:flex;align-items:center;gap:6px;margin:0;font-size:13px;color:var(--muted)"><input type="checkbox" ${_cDec?'checked':''} onchange="_cDec=this.checked;_cp=0;catList()"> az eldöntöttek is</label>`:''}</div>
    <div id="catCats"></div>
    <div id="catRows"><div class="empty">Betöltés…</div></div>`;
  catList();}
function catSearch(){clearTimeout(_cqT);_cqT=setTimeout(()=>{_cq=document.getElementById('catQ').value.trim();_cp=0;catList();},300);}
const CAT_BADGE={repo:'<span class="sbadge repo">a repóban</span>',pending:'<span class="sbadge pending">döntésre vár</span>',rejected:'<span class="sbadge rejected">elutasítva</span>',
  hubrepo:'<span class="sbadge repo">a Központ repójában</span>',hubrejected:'<span class="sbadge rejected">a Központ elutasította</span>'};
async function catList(){const view=_cv;const d=await api('winget-catalog',{method:'POST',body:JSON.stringify({filter:view,q:_cq,page:_cp,showDecided:_cDec,cat:_cc,pub:_cpub})});
  const box=document.getElementById('catRows');if(!box||!d||view!==_cv)return;_cl=d;const e=catEdit(),w=_cst.wg||{};
  // Kategória-címkék darabszámmal (a keresésre és a fejlesztőre szűrve) + a kiválasztott fejlesztő törölhető címkéje.
  const all=(d.cats||[]).reduce((s,c)=>s+c.count,0),cb=document.getElementById('catCats');
  if(cb)cb.innerHTML=(_cpub?`<div class="chips"><span class="chip sel" onclick="catPubClear()" title="Fejlesztő-szűrés törlése">Fejlesztő: <b>${esc(_cpub)}</b> ✕</span></div>`:'')+
    ((d.cats||[]).length>1||_cc?`<div class="chips"><span class="chip ${_cc?'':'sel'}" onclick="catSetCat('')">Mind<span class="n">${all}</span></span>`+
      (d.cats||[]).map(c=>`<span class="chip ${_cc===c.name?'sel':''}" onclick="catSetCat(${esc(JSON.stringify(c.name))})">${esc(c.name)}<span class="n">${c.count}</span></span>`).join('')+'</div>':'');
  if(!d.items.length){box.innerHTML=`<div class="empty">${_cq||_cc||_cpub?'Nincs találat.':view==='all'&&!w.count?'A winget-tár még nincs letöltve – a szerver indulása után néhány perccel magától letölti, vagy kattints fent a „Winget-tár frissítése most” gombra.'
    :view==='pending'?'Nincs döntésre váró program.':view==='rejected'?'Nincs elutasított program.':'A repó üres – a „Teljes winget-tár” vagy a „Gépeken talált” menüből vehetsz fel programot.'}</div>`;return;}
  const btn=(st,i)=>!e||st==='hubrepo'||st==='hubrejected'?'':st==='repo'?`<button class="btn small" onclick="catOne(${i},'remove')">Ki a repóból</button> <button class="btn small danger" onclick="catOne(${i},'reject')">Elutasítás</button>`
    :st==='rejected'?`<button class="btn small" onclick="catOne(${i},'unreject')">Visszavétel</button> <button class="btn small primary" onclick="catOne(${i},'accept')">Felvétel</button>`
    :`<button class="btn small primary" onclick="catOne(${i},'accept')">Felvétel</button> <button class="btn small danger" onclick="catOne(${i},'reject')">Elutasítás</button>`;
  const pager=d.pages>1?`<div style="display:flex;gap:8px;align-items:center;margin:10px 0"><button class="btn small" ${d.page<=0?'disabled':''} onclick="_cp=${d.page-1};catList()">‹ Előző</button>
    <span style="font-size:12px;color:var(--muted)">${d.page+1}. / ${d.pages} oldal</span><button class="btn small" ${d.page>=d.pages-1?'disabled':''} onclick="_cp=${d.page+1};catList()">Következő ›</button></div>`:'';
  box.innerHTML=`<div style="font-size:12px;color:var(--muted);margin-bottom:6px">${d.total} program</div>${pager}
    <table class="upd-table"><tr>${e?'<th style="width:24px"><input type="checkbox" class="chk" onclick="document.querySelectorAll(\'.catC\').forEach(c=>c.checked=this.checked)"></th>':''}<th>Program</th><th>Fejlesztő</th><th>Kategória</th><th>Verzió</th><th>Állapot</th><th></th></tr>
    ${d.items.map((a,i)=>`<tr>${e?`<td><input type="checkbox" class="chk catC" value="${i}"></td>`:''}<td>${esc(a.name)}<div class="mono" style="font-size:11px">${esc(a.id)}</div></td>
      <td><span class="publink" onclick="catPubI(${i})" title="Csak ennek a fejlesztőnek a programjai">${esc(a.publisher||'')}</span></td>
      <td class="catcell">${view==='repo'&&e&&a.state==='repo'?`<select class="grpsel" style="font-size:12px;padding:3px 6px" onchange="catSetItem(${i},this.value)" title="Kézi kategória (felülírja az automatikusat)">
        ${(_ccats.length?_ccats:[a.category]).map(c=>`<option ${c===a.category?'selected':''}>${esc(c)}</option>`).join('')}</select>`:esc(a.category||'')}</td>
      <td class="ver-old">${esc(a.version||'')}</td><td>${CAT_BADGE[a.state]||''}${a.extra?` <span style="font-size:11px;color:var(--muted)">${esc(a.extra)}</span>`:''}${a.inWinget?'':' <span style="font-size:11px;color:var(--muted)">(nincs a winget-tárban)</span>'}</td>
      <td style="text-align:right;white-space:nowrap">${btn(a.state,i)}</td></tr>`).join('')}</table>${pager}`;}
const CAT_DONE={accept:'felvéve a repóba',reject:'elutasítva',remove:'kivéve a repóból',unreject:'elutasítás visszavonva'};
async function catDecide(decision,ids,names,all){const r=await api('catalog-decide',{method:'POST',body:JSON.stringify({decision,ids,names:names||null,allPending:!!all})});
  if(r&&r.ok){toast(`${r.changed} program ${CAT_DONE[decision]}`,'ok');await loadCatalog();const q=_cq,p=_cp;catDraw();_cq=q;_cp=p;}
  else toast((r&&r.error)||'Nem sikerült','err');}
function catOne(i,dec){const a=_cl&&_cl.items[i];if(a)catDecide(dec,[a.id]);}
function catBulk(dec){const ids=[...document.querySelectorAll('.catC:checked')].map(c=>_cl.items[+c.value].id);
  if(!ids.length){toast('Jelölj ki legalább egy programot','err');return;}catDecide(dec,ids);}
async function catAllPending(dec){if(!await uiConfirm(`Mind a(z) ${_cst.pending} döntésre váró program ${dec==='reject'?'elutasítása':'felvétele'}?`))return;catDecide(dec,[],null,true);}
async function catManual(){const id=(await uiPrompt('winget-azonosító (pl. 7zip.7zip):')||'').trim();if(id)catDecide('accept',[id]);}
async function catRefreshWg(){const r=await api('winget-catalog-refresh',{method:'POST',body:'{}'});
  if(!r||!r.ok){toast((r&&r.error)||'Nem sikerült','err');return;}
  toast('A winget-tár letöltése elindult (1–2 perc)','ok');
  for(let i=0;i<60;i++){await new Promise(z=>setTimeout(z,4000));if(!document.getElementById('catMain'))return;await loadCatalog();catDraw();if(!_cst.wg.refreshing)break;}}
// Gépeken talált: a winget által ismert programok, amelyek a gépeken fent vannak – egy kattintással a repóba.
async function catInv(){const main=document.getElementById('catMain');main.innerHTML='<div class="empty">Betöltés…</div>';
  const d=(await api('inventory',{method:'POST',body:'{}'}))||{apps:[]};if(_cv!=='inv')return;window._inv=d.apps||[];const e=catEdit();
  main.innerHTML=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">A felügyelt gépeken talált, winget által ismert (így telepíthető) programok; zárójelben: hány gépen van fent.</p>
    <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:10px"><input id="invF" class="search" style="width:320px;max-width:100%" placeholder="Szűrés…" oninput="fltRows('invF','invrow')">
      ${e?'<button class="btn small primary" onclick="catInvAdd()">Kijelöltek felvétele</button>':''}</div>
    <table class="upd-table">${window._inv.map((a,i)=>`<tr class="invrow" data-n="${esc((a.name+' '+a.id+' '+(a.publisher||'')+' '+(a.category||'')).toLowerCase())}">${e?`<td style="width:24px">${a.state==='repo'?'':`<input type="checkbox" class="chk invC" value="${i}">`}</td>`:''}
      <td>${esc(a.name)}<div class="mono" style="font-size:11px">${esc(a.id)}</div></td><td class="catcell">${esc(a.publisher||'')}<div>${esc(a.category||'')}</div></td>
      <td style="color:var(--muted)">${a.count} gép</td><td>${CAT_BADGE[a.state]||''}</td></tr>`).join('')
      ||'<tr><td class="empty">Nincs ilyen program (vagy a gépek még nem küldtek programlistát).</td></tr>'}</table>`;}
function catInvAdd(){const sel=[...document.querySelectorAll('.invC:checked')].map(c=>window._inv[+c.value]);
  if(!sel.length){toast('Jelölj ki legalább egy programot','err');return;}
  catDecide('accept',sel.map(a=>a.id),Object.fromEntries(sel.map(a=>[a.id,a.name])));}
// Alkalmazáscsomagok: a repó programjaiból összeválogatott, elnevezett csomag – mentés azonnal.
function catBundles(){const e=catEdit();
  document.getElementById('catMain').innerHTML=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">A repó programjaiból összeválogatott, elnevezett csomag (pl. „Irodai alap”, „Könyvelés”) – egy gombbal telepíthető gépre vagy csoportra.</p>
    ${e?'<button class="btn small primary" style="margin-bottom:10px" onclick="editBundle(-1)">+ Új csomag</button>':''}
    <table class="upd-table">${_bun.map((b,k)=>`<tr><td><b>${esc(b.name)}</b><div style="font-size:12px;color:var(--muted)">${b.apps.map(id=>esc(catName(id))).join(', ')||'üres'}</div></td>
      <td style="text-align:right;white-space:nowrap">${e?`<button class="btn small" onclick="editBundle(${k})">Szerkesztés</button> <button class="btn small danger" onclick="delBundle(${k})">Törlés</button>`:''}</td></tr>`).join('')
      ||(_hbun.length?'':'<tr><td style="color:var(--muted)">Még nincs csomag.</td></tr>')}
      ${_hbun.map(b=>`<tr><td><b>${esc(b.name)}</b> <span class="sbadge repo">a Központtól</span><div style="font-size:12px;color:var(--muted)">${b.apps.map(id=>esc(catName(id))).join(', ')||'üres'}</div></td>
        <td style="text-align:right;font-size:12px;color:var(--muted)">csak olvasható</td></tr>`).join('')}</table>`;}
async function delBundle(k){if(!await uiConfirm(`Törlöd a(z) „${_bun[k].name}” csomagot?`))return;_bun.splice(k,1);await saveCatalog();}
function fltRows(inp,cls){const q=document.getElementById(inp).value.toLowerCase();document.querySelectorAll('.'+cls).forEach(r=>r.style.display=r.dataset.n.includes(q)?'':'none');
  pgrpSync();}
// A kategória-fejléc csak akkor látszik, ha alatta van látható program (szűrés után).
function pgrpSync(){document.querySelectorAll('.pgrp').forEach(g=>{g.style.display=[...g.querySelectorAll('.prow')].some(r=>r.style.display!=='none')?'':'none';});}
// Programlista kategóriánként (a szerver sorrendjében), kategórián belül név szerint. row(c,i): egy sor HTML-je (i: index a list-ben).
function catGroups(list,row){const by={};list.forEach((c,i)=>{const k=c.cat||'Egyéb';(by[k]=by[k]||[]).push([c,i]);});
  const keys=_ccats.filter(k=>by[k]).concat(Object.keys(by).filter(k=>!_ccats.includes(k)));
  return keys.map(k=>`<div class="pgrp"><div class="pgh">${esc(k)}<span>${by[k].length} program</span></div>`+
    by[k].sort((x,y)=>x[0].name.localeCompare(y[0].name,'hu')).map(([c,i])=>row(c,i)).join('')+'</div>').join('');}
function editBundle(k){const b=k>=0?_bun[k]:{id:'',name:'',apps:[]};window._bEdit={k,b};const all=catAll();
  const on=c=>b.apps.some(x=>x.toLowerCase()===c.id.toLowerCase());
  openModal(`<h3>${k>=0?'Csomag szerkesztése':'Új alkalmazáscsomag'}</h3><label>Név</label><input id="bName" value="${esc(b.name)}" placeholder="pl. Irodai alap">
    <label>Programok <span style="font-size:12px">(kategóriánként; a repó programjai${_hcat.length?' és a Központ repója':''})</span></label>
    <div class="ptools"><input id="bF" class="search" style="width:280px;max-width:100%" placeholder="Szűrés név, fejlesztő, azonosító szerint…" oninput="bOnly()">
      <label style="display:flex;gap:6px;align-items:center;margin:0;font-size:13px;color:var(--muted)"><input type="checkbox" id="bOnly" onchange="bOnly()"> csak a kijelöltek</label>
      <span class="cnt" id="bCnt"></span></div>
    <div class="plist">${catGroups(all,(c,i)=>`<label class="brow prow" data-n="${esc((c.name+' '+c.id+' '+c.pub+' '+c.cat).toLowerCase())}">
      <input type="checkbox" class="bApp" value="${i}" onchange="bCount()" ${on(c)?'checked':''}><span class="pn">${esc(c.name)}</span>
      <span class="pp">${esc(c.pub)}</span><span class="mono pid" title="${esc(c.id)}">${esc(c.id)}</span></label>`)
      ||'<div class="empty">Előbb vegyél fel programokat a repóba (Teljes winget-tár vagy Gépeken talált).</div>'}</div>
    <div style="display:flex;gap:8px;margin-top:12px"><button class="btn small" onclick="closeModal()">Mégse</button><button class="btn small primary" onclick="saveBundle()">Mentés</button></div>`,true);
  bCount();}
function bCount(){const n=document.querySelectorAll('.bApp:checked').length,el=document.getElementById('bCnt');if(el)el.textContent=`${n} kijelölve / ${document.querySelectorAll('.bApp').length}`;
  if(document.getElementById('bOnly')?.checked)bOnly();}
function bOnly(){const only=document.getElementById('bOnly').checked,q=(document.getElementById('bF').value||'').toLowerCase();
  document.querySelectorAll('.brow').forEach(r=>{const c=r.querySelector('.bApp');r.style.display=(!only||c.checked)&&r.dataset.n.includes(q)?'':'none';});pgrpSync();}
async function saveBundle(){const e=window._bEdit,name=document.getElementById('bName').value.trim();if(!name){toast('Adj nevet a csomagnak','err');return;}
  const all=catAll(),b=Object.assign({},e.b,{name,apps:[...document.querySelectorAll('.bApp:checked')].map(c=>all[+c.value].id)});
  if(e.k>=0)_bun[e.k]=b;else _bun.push(b);closeModal();await saveCatalog();}
// A csomagok mentése azonnal; a repó listája is megy (a legutóbb betöltött, friss állapot), a szerver ellenőrzi.
async function saveCatalog(){const r=await api('catalog-save',{method:'POST',body:JSON.stringify({items:_cat,bundles:_bun})});
  if(r&&r.ok){toast(`Mentve (${r.bundles} csomag)`,'ok');await loadCatalog();catDraw();}else toast((r&&r.error)||'Nem sikerült','err');}
// Telepítés gépre/csoportra: alkalmazáscsomag vagy program a programtárból. send(kind,target,label) a hívó oldal feladat-küldője.
async function installPicker(title,send){await loadCatalog();window._send=send;window._ipB=_bun.concat(_hbun);window._ipC=catAll();
  openModal(`<h3>${title}</h3>
    ${window._ipB.length?'<div style="font-weight:600;margin:4px 0 6px">Alkalmazáscsomagok</div>'+window._ipB.map((b,k)=>`<div style="display:flex;justify-content:space-between;align-items:center;padding:6px 0;border-bottom:1px solid var(--line)">
      <span><b>${esc(b.name)}</b>${b.hub?' <span class="sbadge repo">a Központtól</span>':''} <span style="font-size:12px;color:var(--muted)">${b.apps.length} program: ${b.apps.map(id=>esc(catName(id))).join(', ')}</span></span>
      <button class="btn small primary" onclick="window._send('installbundle',window._ipB[${k}].id,window._ipB[${k}].name)">Csomag telepítése</button></div>`).join(''):''}
    <div style="font-weight:600;margin:12px 0 6px">Programtár</div><input id="ipF" placeholder="Szűrés név, fejlesztő, kategória szerint…" oninput="fltRows('ipF','iprow')" style="margin-bottom:6px">
    <div class="plist" style="max-height:min(46vh,420px)">${catGroups(window._ipC,(c,i)=>`<div class="iprow prow" data-n="${esc((c.name+' '+c.id+' '+c.pub+' '+c.cat).toLowerCase())}">
      <span class="pn">${esc(c.name)} <span class="mono" style="font-size:11px;color:var(--muted)">${esc(c.id)}</span></span><span class="pp">${esc(c.pub)}</span>
      <button class="btn small" onclick="window._send('install',window._ipC[${i}].id,window._ipC[${i}].name)">Telepítés</button></div>`)
      ||'<div class="empty">A repó üres – a Programok menüben töltheted fel (a teljes winget-tárból vagy a gépeken talált programokból).</div>'}</div>
    <div style="font-size:12px;color:var(--muted);margin-top:10px">Gépszintű, csendes telepítés wingettel, rendszerjogon.</div>`,true);}
""";
}
