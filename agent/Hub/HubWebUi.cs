using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

/// <summary>A Kozpont webfelulete. A FleetServer WebUi stilusat (Css) hasznalja ujra.</summary>
public static class HubWebUi
{
    public static string LoginPage(string? error) => Page("Bejelentkezés – Központ", $$"""
    <form class="login-card" method="post" action="/web/login">
      <div class="brand"><div class="brand-mark">◆</div><div class="brand-name">Fleet <span>HUB</span></div></div>
      <h1>Bejelentkezés</h1>
      <p class="muted">Központi felügyelet a telephelyi Fleet szerverekhez.</p>
      {{Err(error)}}
      <label>Felhasználónév</label><input name="user" autofocus required>
      <label>Jelszó</label><input name="password" type="password" required>
      <button type="submit">Belépés</button>
    </form>
    """, center: true);

    public static string SetupPage(string? error, int webPort, int agentPort, string domain) => Page("Első beállítás – Központ", $$"""
    <form class="login-card wide" method="post" action="/web/setup">
      <div class="brand"><div class="brand-mark">◆</div><div class="brand-name">Fleet <span>HUB</span></div></div>
      <h1>Központ beállítása</h1>
      <p class="muted">Ez a központ fogadja a telephelyi Fleet szerverek betárcsázását, publikus
        hálón át. Hozd létre az admin fiókot, és add meg a publikus domaint a TLS-hez.</p>
      {{Err(error)}}
      <label>Admin felhasználónév</label><input name="user" value="admin" required>
      <label>Jelszó</label><input name="password" type="password" required>
      <div class="hint">Legalább 12 karakter, háromféle típussal.</div>
      <label>Jelszó megerősítése</label><input name="password2" type="password" required>

      <label>Publikus domain</label>
      <input name="domain" placeholder="fleet.poltoli.hu" value="{{domain}}" required>
      <div class="hint">Erre kér az NGINX Proxy Manager Let's Encrypt tanúsítványt. IP-cím nem elég.</div>

      <label>Belső port</label><input name="webPort" type="number" value="{{webPort}}" required>
      <div class="hint">Az admin felület és a telephelyek betárcsázása (/hub/) is ezen az egy porton
        megy – az NPM-nek egyetlen proxy host kell. A Cloudflare maradjon DNS only.</div>
      <button type="submit">Beállítás mentése és belépés</button>
    </form>
    """, center: true);

    public static string Dashboard(string user, Role role) => Page("Fleet HUB", """
    <header class="topbar">
      <div class="brand"><div class="brand-mark">◆</div><div class="brand-name">Fleet <span>HUB</span></div>
        <button class="navtog" onclick="toggleNav()" title="Menü összecsukása / kinyitása">☰</button></div>
      <div class="top-actions">
        <button class="ghost" onclick="pageClose();refresh()" title="Telephelyek"><span class="i">▣</span><span class="t">Telephelyek</span></button>
        {{NAV_GROUPS}}{{NAV_RULES}}{{NAV_ALERTS}}{{NAV_CVE}}{{NAV_REPORTS}}{{NAV_CATALOG}}{{NAV_REQ}}
        {{ROLE_TOKEN}}
        {{NAV_SETTINGS}}
        {{ROLE_USERS}}
        <span class="user">{{USER}} · {{ROLE_LABEL}}</span>
        <a class="ghost" href="/web/logout" title="Kilépés"><span class="i">⏻</span><span class="t">Kilépés</span></a>
      </div>
    </header>
    <div class="stats" id="stats"></div>
    <main class="layout">
      <section class="panel">
        <div class="panel-head"><h2>Telephelyek</h2><input id="search" class="search" placeholder="Keresés…" oninput="renderSites()"></div>
        <div id="siteList" class="agent-list"></div>
      </section>
      <section class="panel">
        <div class="panel-head"><h2 id="detailTitle">Válassz telephelyet</h2><div id="detailActions"></div></div>
        <div id="detail" class="detail"></div>
      </section>
    </main>
    <div id="toast" class="toast"></div>
    <div id="page" class="page"></div>
    <div id="modal" class="modal-back" onclick="if(event.target===this)closeModal()"><div class="modal"><div id="modalBody"></div></div></div>
    <script>{{JS}}</script>
    <script>{{MONJS}}</script>
    <script>{{KEYSJS}}</script>
    <script>window.CAT_MIN_ROLE=2;window.IS_HUB=true;</script>
    <script>{{CATJS}}</script>
    <script>{{REPJS}}</script>
    <script>{{RULESJS}}</script>
    <script>{{GROUPSJS}}</script>
    <script>{{STORJS}}</script>
    <script>{{DLGJS}}</script>
    <script>{{CVEJS}}</script>
    <script>{{REQJS}}</script>
    """.Replace("{{USER}}", user)
        .Replace("{{NAV_CVE}}", WebUi.Nav("showCves()", "⚑", "Sérülékenységek"))
        .Replace("{{NAV_REQ}}", WebUi.Nav("showRequests()", "✋", "Programkérések"))
        .Replace("{{NAV_GROUPS}}", WebUi.Nav("showGroups()", "▦", "Csoportok"))
        .Replace("{{NAV_RULES}}", WebUi.Nav("showRules()", "◎", "Monitorozás"))
        .Replace("{{ROLE_LABEL}}", role == Role.Admin ? "Adminisztrátor" : role == Role.Kezelo ? "Kezelő" : "Megfigyelő")
        .Replace("{{NAV_ALERTS}}", WebUi.Nav("showAlerts()", "⚠", "Riasztások"))
        .Replace("{{NAV_REPORTS}}", WebUi.Nav("showReports()", "⇩", "Jelentések"))
        .Replace("{{NAV_CATALOG}}", WebUi.Nav("showCatalog()", "▤", "Programok"))
        .Replace("{{NAV_SETTINGS}}", WebUi.Nav("showSettings()", "⚙", "Beállítások"))
        .Replace("{{ROLE_TOKEN}}", role >= Role.Kezelo ? WebUi.Nav("showToken()", "＋", "Új telephely") : "")
        .Replace("{{ROLE_USERS}}", role >= Role.Admin ? WebUi.Nav("showUsers()", "☺", "Felhasználók") + WebUi.Nav("showSecurity()", "⚿", "Biztonság") : "")
        // A szerepkör a beillesztett szkriptben van (window.ROLE=window.__ROLE__) - ezért a szkript UTÁN cseréljük.
        .Replace("{{JS}}", Js).Replace("{{MONJS}}", WebUi.MonitorJs).Replace("{{KEYSJS}}", WebUi.KeysJs).Replace("{{CATJS}}", WebUi.CatalogJs).Replace("{{REPJS}}", WebUi.ReportsJs)
        .Replace("{{RULESJS}}", WebUi.RulesJs).Replace("{{GROUPSJS}}", WebUi.GroupsJs).Replace("{{STORJS}}", WebUi.StorageJs).Replace("{{DLGJS}}", WebUi.DialogJs)
        .Replace("{{CVEJS}}", WebUi.CveJs).Replace("{{REQJS}}", WebUi.RequestsJs)
        .Replace("window.__ROLE__", ((int)role).ToString()), center: false);

    private static string Err(string? e) => e is null ? "" : $"<div class=\"alert\">{e}</div>";

    private static string Page(string title, string body, bool center) => $$"""
    <!DOCTYPE html><html lang="hu"><head><meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1"><title>{{title}}</title>
    <style>{{WebUi.Css}}</style></head>
    <body{{(center ? " class=\"login-body\"" : "")}}>{{body}}</body></html>
    """;

    private const string Js = """
    window.ROLE=window.__ROLE__;
    let sites=[], selId=null;
    async function api(p,o){const r=await fetch('/web/api/'+p,{...o,headers:{'Content-Type':'application/json'}});
      if(r.status===401){location.href='/web';return null}return r.json();}
    async function refresh(){const d=await api('sites',{method:'POST',body:'{}'});if(!d)return;sites=d.sites;renderStats();renderSites();if(selId)renderDetail();}
    function renderStats(){const on=sites.filter(s=>s.online&&!s.revoked).length;
      const upd=sites.reduce((a,s)=>a+(s.updatesAvailable||0),0);
      const ag=sites.reduce((a,s)=>a+(s.agentCount||0),0);
      document.getElementById('stats').innerHTML=
        `<div class="stat"><div class="n">${sites.length}</div><div class="l">Telephely</div></div>
         <div class="stat ok"><div class="n">${on}</div><div class="l">Online</div></div>
         <div class="stat"><div class="n">${ag}</div><div class="l">Gép összesen</div></div>
         <div class="stat ${upd?'warn':''}"><div class="n">${upd}</div><div class="l">Elavult alkalmazás</div></div>`;}
    function renderSites(){const q=(document.getElementById('search').value||'').toLowerCase();
      const l=document.getElementById('siteList');const sh=sites.filter(s=>s.name.toLowerCase().includes(q));
      if(!sh.length){l.innerHTML='<div class="empty">Nincs telephely. Kattints az „Új telephely” gombra.</div>';return}
      l.innerHTML=sh.map(s=>{const st=s.revoked?'revoked':(s.online?'online':'offline');
        const b=s.revoked?'':((s.updatesAvailable||0)>0?`<span class="badge upd">${s.updatesAvailable} frissítés</span>`:`<span class="badge ok">${s.agentCount} gép</span>`);
        return `<div class="agent ${s.siteId===selId?'sel':''}" onclick="sel('${s.siteId}')"><span class="dot ${st}"></span>
          <span class="name">${esc(s.name)}</span>${b}<span class="meta">${s.lastSeen||'–'}</span></div>`;}).join('');}
    function sel(id){selId=id;renderSites();renderDetail();}
    function renderDetail(){const s=sites.find(x=>x.siteId===selId);const t=document.getElementById('detailTitle');
      const box=document.getElementById('detail');const act=document.getElementById('detailActions');
      if(!s){t.textContent='Válassz telephelyet';box.innerHTML='';act.innerHTML='';return}
      t.textContent=s.name;
      act.innerHTML=(s.revoked||window.ROLE<1)?'':`<div class="detail-actions">
        <button class="btn small primary" onclick="showAgentTok('${s.siteId}')">Új agent</button>
        <button class="btn small danger" onclick="revoke('${s.siteId}')">Visszavonás</button></div>`;
      const mins=Math.round((s.intervalSeconds||300)/60*10)/10;
      let h=`<div class="subtitle">Fleet ${esc(s.serverVersion||'?')} · ${s.agentCount} gép · ujjlenyomat <span class="mono">${s.fingerprint||''}</span>${s.agentPort?` · agent-port ${s.agentPort}`:''}${s.oldAgentPort?` <span style="color:var(--warn)">(portváltás: a régi ${s.oldAgentPort} még figyel)</span>`:''}<br>
        Utoljára: ${s.lastSeen||'–'} · regisztrálva: ${s.enrolled||'–'}</div>${updBox(s)}
        <div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin-bottom:14px">
          <div style="font-weight:600;margin-bottom:8px">Betárcsázás gyakorisága</div>
          <div style="display:flex;gap:8px;align-items:center">
            <input id="ival" type="number" value="${mins}" style="width:80px;background:var(--panel);border:1px solid var(--line);border-radius:7px;padding:7px;color:var(--ink)"> perc
            <button class="btn small primary" onclick="setInterval2('${s.siteId}')">Beállít</button>
          </div>
          <div style="font-size:12px;color:var(--muted);margin-top:6px">A telephely a következő jelentkezéskor átveszi.${s.nextDial?' Következő betárcsázás kb. '+esc(s.nextDial)+'.':''}</div>
          ${window.ROLE>=1?`<div style="display:flex;gap:8px;align-items:center;margin-top:8px"><button class="btn small" onclick="siteFast('${s.siteId}')">Gyors betárcsázás (10 percig 30 mp-enként)</button>
            ${s.fastUntil?`<span style="font-size:12px;color:var(--ok)">bekapcsolva ${esc(s.fastUntil)}-ig</span>`:''}</div>
          <div style="font-size:11px;color:var(--muted);margin-top:4px">A Központ nem hívhatja fel a telephelyet: a gyors mód a következő betárcsázástól él. Azonnal a telephely szerverén indítható (Beállítások → Kapcsolódás most). Minden itt kért művelet után magától is bekapcsol.</div>`:''}
        </div>`;
      h+=siteNetBox(s);
      if(window.ROLE>=2)h+=`<div style="display:flex;gap:8px;margin-bottom:12px"><button class="btn small" onclick="showSiteLogs('${s.siteId}','audit')">Naplók (központi napló)</button>
        <button class="btn small" onclick="_ruSiteId='${s.siteId}';showRules('site')">A telephely szabályai</button></div>`;
      h+=grpBar(s);
      const ag=s.agents||[];
      const row=a=>`<tr><td>${scoreBadge(a.score)} ${esc(a.machineName)}${a.updatesAvailable>0?` <span class="badge upd">${a.updatesAvailable}</span>`:''}${a.pendingReboot?' <span class="badge upd">újraindításra vár</span>':''}</td>
          <td class="ver-old">${esc(a.operatingSystem||'')}</td>
          <td style="text-align:right">${a.updatesAvailable>0?(window.ROLE>=1?`<button class="btn small primary" onclick="upAll('${s.siteId}','${a.agentId}')">Összes</button>`:`<span class="badge upd">${a.updatesAvailable}</span>`):'<span style="color:var(--ok)">naprakész</span>'}</td>
          <td style="text-align:right"><button class="btn small" onclick="mach('${s.siteId}','${a.agentId}')">Kezelés</button></td></tr>`;
      // Csoportosítás (Szerver / kliens, Windows-verzió, Virtuális / fizikai, Csoport) - közös a telephelyi szerverrel (GroupsJs).
      if(ag.length){h+=`<div style="display:flex;justify-content:flex-end;margin-bottom:6px">${mgSel('mgHub','mgSet(this.value);renderDetail()')}</div>
        <table class="upd-table"><tr><th>Gép</th><th>OS</th><th style="text-align:right">Frissíthető</th><th></th></tr>`+
        (!_mgBy?ag.map(row).join(''):machGroups(ag,_mgBy).map(([k,l])=>{const key=_mgBy+':'+k,hd=mgHead(key,k,l);
          return `<tr><td colspan="4" class="agroup ${hd.closed?'closed':''}" onclick="mgToggle(${esc(JSON.stringify(key))});renderDetail()">${hd.html}</td></tr>`+(hd.closed?'':l.map(row).join(''));}).join(''))+`</table>`;
      }else h+='<div class="empty">Ez a telephely még nem küldött gépadatot.</div>';
      box.innerHTML=h;}
    // 1.5.5: a telephely internetkapcsolata (ha figyeli) és a riasztást adó SNMP-trapek – a telephely szerverén állítható be.
    function siteNetBox(s){const i=s.internet,ev=s.snmpEvents||[];if(!i&&!ev.length&&!s.snmpEnabled)return'';
      let h='<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin-bottom:14px">';
      if(i)h+=`<div style="font-weight:600;margin-bottom:6px">Internetkapcsolat ${i.online===false?`<span class="badge upd" style="color:var(--err)">KIESETT ${esc(i.since||'')} óta</span>`
          :i.online?'<span class="badge ok">rendben</span>':'<span class="badge">még nincs ellenőrzés</span>'}</div>
        <div style="font-size:12px;color:var(--muted)">${i.online?`Válaszolt: ${esc(i.target)} (${i.rtt} ms) · `:''}ellenőrzés ${i.minutes} percenként${i.lastCheck?' · utoljára '+esc(i.lastCheck):''}</div>
        ${(i.outages||[]).length?'<div style="font-size:12px;margin-top:6px">Legutóbbi kiesések: '+i.outages.map(o=>`${esc(o.start)} – ${o.end?esc(o.end):'még tart'} (${o.minutes} perc)`).join(' · ')+'</div>':''}`;
      if(s.snmpEnabled||ev.length)h+=`<div style="font-weight:600;margin:${i?'12px':'0'} 0 6px">SNMP-riasztások${s.snmpEnabled?'':' <span style="font-weight:400;color:var(--muted)">(a fogadás most ki van kapcsolva)</span>'}</div>`+
        (ev.length?'<table class="upd-table">'+ev.map(e=>`<tr><td style="white-space:nowrap">${esc(e.at)}</td><td class="mono">${esc(e.source)}</td><td>${esc(e.text)}</td></tr>`).join('')+'</table>'
          :'<div style="font-size:12px;color:var(--muted)">Nincs riasztást adó trap.</div>');
      return h+'</div>';}
    async function setInterval2(id){const m=parseFloat(document.getElementById('ival').value)||5;
      const r=await api('interval',{method:'POST',body:JSON.stringify({siteId:id,seconds:Math.round(m*60)})});
      if(r&&r.ok){toast('Gyakoriság beállítva','ok');refresh();}}
    async function upAll(sid,aid){if(!await uiConfirm('A gép összes frissíthető alkalmazása frissüljön?'))return;
      const r=await api('queue',{method:'POST',body:JSON.stringify({siteId:sid,agentId:aid,all:true})});
      if(r&&r.ok)toast('Feladat betéve – a telephely a következő jelentkezéskor átveszi','ok');}
    // ---- Gépkezelés a Központból: ugyanaz, mint a telephelyi szerveren; a feladat a következő betárcsázáskor megy le.
    // ---- Gépkezelés teljes oldalon (mint a Programtár): bal oldalon a részek, jobbra a tartalom.
    let curM=null,_mv='sum';
    async function mach(sid,aid,view){if(view)_mv=view;else if(!curM||curM.aid!==aid)_mv='sum';
      const d=await api('machine',{method:'POST',body:JSON.stringify({siteId:sid,agentId:aid})});const a=d&&d.agent;if(!a)return;
      curM={sid,aid,a,cve:d.cve,upd:{req:d.updateRequestedAt,last:d.lastUpdate}};const s=sites.find(x=>x.siteId===sid)||{};closeModal();
      pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>${esc(a.machineName)}</h2>
        <span class="sub">${esc(s.name||'')} · ${esc(a.operatingSystem||'')} · agent ${esc(a.agentVersion||'?')} · ${a.online?'online':'offline'}</span>
        <button class="btn small primary" onclick="showMon()">Monitorozás – grafikonok</button></div>
        <div class="page-body"><div class="page-menu" id="mcMenu"></div><div class="page-main" id="mcMain"></div></div>`);
      mcDraw();}
    function mcGo(v){_mv=v;mcDraw();document.getElementById('mcMain').scrollTop=0;}
    function mcDraw(){const m=curM;if(!m||!document.getElementById('mcMain'))return;const a=m.a,w=window.ROLE>=1,st=sites.find(x=>x.siteId===m.sid)||{};
      const up=a.updates||[],wu=a.windowsUpdates||[],ins=a.installed||[];
      const B=(f,l,c)=>w?`<button class="btn small ${c||''}" onclick="${f}">${l}</button>`:'';
      const cv=m.cve,cvN=cv?(cv.findings||[]).length:0;
      const V=[['sum','Áttekintés',''],['upd','Frissíthető programok',up.length],['wu','Windows Update',wu.length],['cve','Sérülékenységek',cvN||''],['inst','Program telepítése',''],['apps','Telepített programok',ins.length],['reboot','Újraindítás','']];
      document.getElementById('mcMenu').innerHTML=V.map(v=>`<button class="${_mv===v[0]?'sel':''}" onclick="mcGo('${v[0]}')"><span>${v[1]}</span><span class="cnt">${v[2]}</span></button>`).join('');
      const old=vNewer(st.agentPackageVersion,a.agentVersion),u=m.upd||{};let h='';
      if(_mv==='sum'){
        h=(a.score?scoreBox(a.score):'<div style="font-size:12px;color:var(--muted);margin-bottom:12px">Eszköz-összegzés: a telephely szervere 1.5.6-tól küldi.</div>')+
          `<div class="tiles"><div class="tile"><div class="v">${a.online?'online':'offline'}</div><div class="l">Állapot</div></div>
          <div class="tile"><div class="v">${esc(a.agentVersion||'?')}</div><div class="l">Agent (beépített: ${esc(st.agentPackageVersion||'?')})</div></div>
          <div class="tile"><div class="v">${up.length}</div><div class="l">Frissíthető program</div></div><div class="tile"><div class="v">${wu.length}</div><div class="l">Windows-frissítés</div></div>
          <div class="tile"><div class="v">${ins.length}</div><div class="l">Telepített program</div></div><div class="tile"><div class="v">${a.pendingReboot?'igen':'nem'}</div><div class="l">Újraindításra vár</div></div></div>
          ${u.last?`<div style="background:rgba(49,192,122,.1);border:1px solid rgba(49,192,122,.35);border-radius:9px;padding:10px 12px;margin-bottom:12px;font-size:13px">✓ Az agent frissült: <b>${esc(u.last.from)}</b> → <b>${esc(u.last.to)}</b> (${esc(u.last.at)})</div>`:''}
          ${u.req&&old?`<div style="font-size:13px;color:var(--warn);margin-bottom:12px">Agent-frissítés kérve: ${esc(u.req)} – a telephely ${st.nextDial?'kb. '+esc(st.nextDial)+'-kor ':''}tárcsáz be, a gép az azutáni jelentkezésekor frissül.</div>`:''}
          <div style="font-size:13px;margin-bottom:12px">Csoportok: ${(a.groups||[]).length?a.groups.map(g=>`<span class="badge ok" style="margin-right:4px">${esc(g)}</span>`).join(''):'<span style="color:var(--muted)">nincs (a Csoportok menüben adható)</span>'}</div>`;
        if(old)h+=`<div style="background:rgba(224,165,43,.1);border:1px solid rgba(224,165,43,.35);border-radius:9px;padding:10px 12px;margin-bottom:12px;font-size:13px">
          Az agent <b>${esc(a.agentVersion||'?')}</b>, a telephely szerverébe épített <b>${esc(st.agentPackageVersion)}</b>. Az 1.5-ös bővített mérések (SMART-részletek, hardver, ping, biztonság…) csak a frissítés után jönnek.
          ${!st.agentAutoUpdate?'<br><span style="color:var(--warn)">A telephelyen az automatikus agent-frissítés KI van kapcsolva.</span>':''}
          ${w&&st.supportsHubUpdates?`<div style="margin-top:8px">${B("task('updateagent','','Az agent a következő jelentkezéskor frissüljön a szerverbe épített verzióra?')",`Agent frissítése: ${esc(a.agentVersion||'?')} → ${esc(st.agentPackageVersion)}`,'primary')}</div>`:''}
          <div style="font-size:12px;color:var(--muted);margin-top:6px">Ha ezután sem frissül, a gépen a C:\\ProgramData\\DevToolbox-Agent\\agent.log mutatja az okát.</div></div>`;
      } else if(_mv==='cve'){
        h=(cv?cveSum(cv)+cveTable(cv.findings):'<div class="empty">Még nincs ellenőrzés erre a gépre (Sérülékenységek menü → Ellenőrzés most).</div>')+
          `<div style="font-size:12px;color:var(--muted);margin-top:10px">${esc(CVE_NOTE)}</div>`;
      } else if(_mv==='upd'){
        h=up.length?`<table class="upd-table"><tr><th>Program</th><th>Telepítve</th><th>Elérhető</th></tr>${up.map(u=>`<tr><td>${esc(u.name)}</td><td class="ver-old">${esc(u.installed||'')}</td><td class="ver-new">${esc(u.available||'')}</td></tr>`).join('')}</table>
          <div style="margin-top:10px">${B("task('upgradeall','','Minden program frissüljön ezen a gépen?')",'Összes frissítése','primary')}</div>`:'<div class="empty">✓ Minden program naprakész.</div>';
      } else if(_mv==='wu'){
        h=(wu.length?`<table class="upd-table"><tr><th>KB</th><th>Frissítés</th><th>Súlyosság</th><th></th></tr>${wu.map((u,i)=>`<tr><td class="mono">${esc(u.kb||'')}</td><td>${esc(u.title||'')}${u.rebootRequired?' <span class="badge upd">újraindítás</span>':''}</td><td>${esc(u.severity||'')}</td><td style="text-align:right">${B(`wuOne(${i})`,'Telepítés')}</td></tr>`).join('')}</table>`
          :'<div class="empty">Nincs függő Windows-frissítés (vagy még nem keresett).</div>')+
          `<div style="display:flex;gap:8px;margin-top:10px">${wu.length?B("task('wuinstall','all','Minden elérhető Windows-frissítés települjön?')",'Összes telepítése','primary'):''}${B("task('wuscan','')",'Keresés most')}</div>
          <div style="font-size:12px;color:var(--muted);margin-top:6px">Újraindítás csak külön, a te döntésedre történik.</div>`;
      } else if(_mv==='inst'){
        h=`<p style="font-size:13px;color:var(--muted);margin:0 0 12px">Alkalmazáscsomag vagy program a programtárból – gépszintű, csendes telepítés wingettel, a következő betárcsázás után.</p>${B("machInstall()",'Alkalmazáscsomag vagy program a programtárból…','primary')||'<div class="empty">Telepítéshez kezelői jog kell.</div>'}`;
      } else if(_mv==='apps'){
        h=ins.length?`<input id="mcF" class="search" style="width:320px;max-width:100%;margin-bottom:10px" placeholder="Szűrés…" oninput="fltRows('mcF','mcrow')">
          <table class="upd-table"><tr><th>Program</th><th>Verzió</th><th></th></tr>${ins.map((p,i)=>`<tr class="mcrow" data-n="${esc((p.name+' '+(p.id||'')).toLowerCase())}"><td>${esc(p.name)}<div class="ver-old mono" style="font-size:11px">${esc(p.id||'')}</div></td><td class="ver-old">${esc(p.version||'')}</td><td style="text-align:right">${p.id?B(`unOne(${i})`,'Eltávolítás','danger'):''}</td></tr>`).join('')}</table>`
          :'<div class="empty">A telephely még nem küldött programlistát.</div>';
      } else {
        h=w?`<div style="display:flex;gap:8px;align-items:center"><input id="rbMin" type="number" value="5" style="width:80px"> perc múlva ${B("rebootM()",'Újraindítás ütemezése','danger')}</div>
          <div style="font-size:12px;color:var(--muted);margin-top:8px">A gép felhasználója figyelmeztetést kap. Soha nem automatikus – csak erre a gombra.</div>`:'<div class="empty">Újraindításhoz kezelői jog kell.</div>';
      }
      document.getElementById('mcMain').innerHTML=`<div class="view">${h}</div>`;}
    // Telepítés erre a gépre: csomag vagy program a programtárból (WebUi.CatalogJs installPicker).
    function machInstall(){const m=curM;installPicker(`Program telepítése – ${esc(m.a.machineName)}`,(k,t,l)=>{curM=m;
      task(k,t,`${k==='installbundle'?'Csomag':'Program'}: ${l} – telepítés erre a gépre: ${m.a.machineName}?`);});}
    // ---- Frissítés a Központból: a telephely szervere a Központ verziójára, az agentek a szerverbe épített agentre.
    function vNewer(a,b){const p=x=>String(x||'0').split('.').map(n=>parseInt(n)||0),x=p(a),y=p(b);
      for(let i=0;i<4;i++){if((x[i]||0)!==(y[i]||0))return (x[i]||0)>(y[i]||0);}return false;}
    function updBox(s){if(window.ROLE<1||s.revoked)return'';const old=vNewer(s.hubVersion,s.serverVersion);
      return `<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin:10px 0 14px">
        <div style="font-weight:600;margin-bottom:8px">Frissítés a Központból</div>
        <div style="font-size:13px;margin-bottom:8px">Telephely szervere: <b>${esc(s.serverVersion||'?')}</b> · Központ: <b>${esc(s.hubVersion)}</b> · beépített agent: <b>${esc(s.agentPackageVersion||'?')}</b></div>
        ${!s.supportsHubUpdates?'<div style="font-size:12px;color:var(--warn)">Ez a telephely még nem tudja a Központból frissíteni magát – egyszer töltsd fel rá kézzel a Fleet.exe-t (a szerver webfelületén: Beállítások → Fleet Server frissítése).</div>'
          :(old?(s.updateRequested?`<div style="font-size:12px;color:var(--warn)">Frissítés kérve – a következő betárcsázáskor letölti és lecseréli magát.</div><button class="btn small" style="margin-top:6px" onclick="siteUpd('${s.siteId}',false)">Kérés visszavonása</button>`
               :`<button class="btn small primary" onclick="siteUpd('${s.siteId}',true)">Szerver frissítése: ${esc(s.serverVersion||'?')} → ${esc(s.hubVersion)}</button>`)
              :'<div style="font-size:12px;color:var(--ok)">A szerver naprakész.</div>')+
          (s.serverUpdate?`<div style="font-size:12px;color:var(--ok);margin-top:6px">✓ A szerver frissült: ${esc(s.serverUpdate.from)} → ${esc(s.serverUpdate.to)} (${esc(s.serverUpdate.at)})</div>`:'')+
          (s.updateRequested&&s.updateRequestedAt?`<div style="font-size:12px;color:var(--muted)">Kérve: ${esc(s.updateRequestedAt)}</div>`:'')+
          (s.agentUpdatePending?`<div style="font-size:12px;color:var(--warn);margin-top:6px">${s.agentUpdatePending} gép agent-frissítése kérve – vár</div>`:'')+
          ((s.agentUpdates||[]).length?`<div style="font-size:12px;margin-top:6px"><b>Legutóbbi agent-frissülések:</b> ${s.agentUpdates.map(u=>`<span style="color:var(--ok)">${esc(u.machine)} ${esc(u.from)}→${esc(u.to)}</span> <span style="color:var(--muted)">(${esc(u.at)})</span>`).join(' · ')}</div>`:'')+
          `<label style="display:flex;align-items:center;gap:8px;margin-top:10px"><input type="checkbox" ${s.agentAutoUpdate?'checked':''} onchange="siteAU('${s.siteId}',this.checked)"> Agentek automatikus frissítése a beépített agentre${s.agentAutoUpdatePending?' <span style="color:var(--warn)">(betárcsázáskor megy le)</span>':''}</label>`}
      </div>`;}
    async function siteFast(sid){const r=await api('site-fast',{method:'POST',body:JSON.stringify({siteId:sid})});
      if(r&&r.ok){toast('Gyors betárcsázás: a következő jelentkezéstől 10 percig 30 mp-enként','ok');refresh();}else toast((r&&r.error)||'Hiba','err');}
    async function siteUpd(sid,on){if(on&&!await uiConfirm('A telephely szervere a következő betárcsázáskor letölti a Központ verzióját és lecseréli magát (ha nem indul el, visszaáll a régire). Mehet?'))return;
      const r=await api('site-update',{method:'POST',body:JSON.stringify({siteId:sid,target:on?'on':'off'})});
      if(r&&r.ok){toast(on?'Frissítés kérve':'Kérés visszavonva','ok');refresh();}else toast((r&&r.error)||'Hiba','err');}
    async function siteAU(sid,on){const r=await api('site-agent-autoupdate',{method:'POST',body:JSON.stringify({siteId:sid,target:on?'on':'off'})});
      if(r&&r.ok){toast('Beállítva – a következő betárcsázáskor megy le','ok');refresh();}else toast((r&&r.error)||'Hiba','err');}
    // ---- Új agent a Központból: a beléptetőt a telephely készíti el a következő betárcsázáskor.
    async function showAgentTok(sid){const s=sites.find(x=>x.siteId===sid);if(!s)return;
      const d=(await api('agent-tokens',{method:'POST',body:JSON.stringify({siteId:sid})}))||{jobs:[]};
      const rows=(d.jobs||[]).map(j=>`<tr><td>${esc(j.label)}</td><td>${esc(j.created)}</td><td>${esc(j.state)}</td>
        <td style="text-align:right">${j.ready?`<a class="btn small primary" href="/web/agent-zip?req=${encodeURIComponent(j.id)}">Letöltés (ZIP)</a>`:''}</td></tr>`).join('');
      openModal(`<h3>Új agent – ${esc(s.name)}</h3>
        <p style="font-size:12px;color:var(--muted)">A beléptetőt a telephely szervere készíti: a kérés a következő betárcsázáskor megy le, a kész csomag kb. fél perccel utána tölthető le.
          A csomagban a Központba épített agent van, előre kitöltve a telephely belső címével és ujjlenyomatával.</p>
        <div style="display:flex;gap:8px"><input id="atLabel" placeholder="címke, pl. Könyvelés-PC" style="flex:1"><button class="btn small primary" onclick="reqAgentTok('${sid}')">Kérés</button>
          <button class="btn small" onclick="showAgentTok('${sid}')">Frissítés</button></div>
        <table class="upd-table" style="margin-top:12px"><tr><th>Címke</th><th>Kérve</th><th>Állapot</th><th></th></tr>${rows||'<tr><td colspan="4" style="color:var(--muted)">Nincs kérés.</td></tr>'}</table>`);}
    async function reqAgentTok(sid){const r=await api('agent-token-request',{method:'POST',body:JSON.stringify({siteId:sid,target:document.getElementById('atLabel').value})});
      if(r&&r.ok){toast('Kérés elküldve – a következő betárcsázáskor készül el','ok');showAgentTok(sid);}else toast((r&&r.error)||'Hiba','err');}
    // ---- Csoportos műveletek egy telephelyen (a gépek csoportjai alapján)
    function grpBar(s){const ag=s.agents||[];if(window.ROLE<1||!ag.length)return'';
      const gs=[...new Set(ag.flatMap(a=>a.groups||[]))].sort((x,y)=>x.localeCompare(y,'hu'));window._gs=gs;
      return `<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin-bottom:14px">
        <div style="font-weight:600;margin-bottom:8px">Csoportos művelet</div><div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center">
        <select id="grpSel" style="padding:7px;background:var(--panel);border:1px solid var(--line);border-radius:7px;color:var(--ink)"><option value="">Minden gép (${ag.length})</option>
          ${gs.map((g,i)=>`<option value="${i}">${esc(g)} (${ag.filter(a=>(a.groups||[]).includes(g)).length})</option>`).join('')}</select>
        <button class="btn small" onclick="grpTask('${s.siteId}','upgradeall','')">Programok frissítése</button>
        <button class="btn small" onclick="grpTask('${s.siteId}','wuinstall','all')">Windows-frissítések</button>
        <button class="btn small" onclick="grpInstall('${s.siteId}')">Csomag / program telepítése…</button>
        <button class="btn small danger" onclick="grpTask('${s.siteId}','reboot','300')">Újraindítás (5 perc)</button></div></div>`;}
    // A csoportválasztást a kattintáskor rögzítjük (a választó ablak közben bezárja a telephely nézetét).
    function grpInstall(sid){const v=document.getElementById('grpSel').value;
      installPicker('Telepítés csoportra',(k,t,l)=>grpTask(sid,k,t,v,l));}
    async function grpTask(sid,k,t,sel,label){const s=sites.find(x=>x.siteId===sid);if(!s)return;const v=sel!==undefined?sel:document.getElementById('grpSel').value;
      const g=v===''?null:window._gs[+v];const ids=(s.agents||[]).filter(a=>!g||(a.groups||[]).includes(g)).map(a=>a.agentId);
      const what={upgradeall:'programfrissítés',wuinstall:'Windows-frissítések telepítése',installbase:'alapcsomag telepítése',reboot:'újraindítás 5 perc múlva',
        installbundle:'csomag telepítése: '+(label||''),install:'program telepítése: '+(label||'')}[k];
      if(!ids.length||!await uiConfirm(`${ids.length} gépre (${g?esc(g):'minden gép'}): ${what}?`))return;
      const r=await api('task',{method:'POST',body:JSON.stringify({siteId:sid,agentIds:ids,kind:k,target:t})});
      if(r&&r.ok)toast(`Feladat betéve – ${r.machines} gép`,'ok');else if(r)toast(r.error||'hiba','err');}
    // ---- Programkatalógus és automatikus szabályok a Központban (módosítás: admin)
    // (programtár és alkalmazáscsomagok: WebUi.CatalogJs - közös a FleetServerrel)
    const DAYS=['V','H','K','Sze','Cs','P','Szo'];
    async function showPolicies(){const d=(await api('policies',{method:'POST',body:'{}'}))||{policies:[],groups:[],sites:[]};
      window._pol=d.policies||[];window._polGroups=d.groups||[];window._polSites=d.sites||[];const a=window.ROLE>=2;
      const sn=id=>(window._polSites.find(s=>s.siteId===id)||{name:'?'}).name;
      const rows=window._pol.map((p,k)=>`<tr><td><b>${esc(p.name)}</b>${p.enabled?'':' <span style="color:var(--muted)">(kikapcsolva)</span>'}<br>
        <span style="font-size:12px;color:var(--muted)">${(p.sites||[]).length?p.sites.map(x=>esc(sn(x))).join(', '):'minden telephely'} · ${p.group?esc(p.group):'minden gép'} ·
        ${[p.apps?'programok':'',p.windows?'Windows':'',p.reboot?'újraindítás':''].filter(Boolean).join(', ')} · ${[1,2,3,4,5,6,0].filter(x=>p.days.includes(x)).map(x=>DAYS[x]).join(' ')} ${p.startHour}:00–${p.endHour}:00</span></td>
        <td style="text-align:right">${a?`<button class="btn small" onclick="editPolicy(${k})">Szerkesztés</button> <button class="btn small danger" onclick="delPolicy(${k})">Törlés</button>`:''}</td></tr>`).join('');
      openView(`<h3>Automatikus szabályok (Központ)</h3>
        <p style="font-size:12px;color:var(--muted)">Alapból semmi nem automatikus. A szabály betárcsázáskor lemegy a kiválasztott telephelyekre, és ott a helyi szabályok mellett fut, a telephely helyi ideje szerint. Újraindítás csak ha bekapcsolod ÉS a gép jelzi, hogy arra vár.</p>
        <table class="upd-table">${rows||'<tr><td style="color:var(--muted)">Még nincs szabály.</td></tr>'}</table>
        ${a?'<button class="btn small primary" style="margin-top:10px" onclick="editPolicy(-1)">+ Új szabály</button>':''}`);}
    function editPolicy(k){const p=k>=0?window._pol[k]:{id:'',name:'',group:'',apps:true,windows:false,reboot:false,days:[1,2,3,4,5],startHour:22,endHour:4,enabled:true,sites:[]};
      window._pEdit=p;const sel='width:100%;padding:8px;background:var(--panel2);border:1px solid var(--line);border-radius:8px;color:var(--ink)';
      const opts=['<option value="">Minden gép</option>'].concat(window._polGroups.map((g,i)=>`<option value="${i}" ${g===p.group?'selected':''}>${esc(g)}</option>`)).join('');
      const hours=h=>Array.from({length:24},(_,i)=>`<option ${i===h?'selected':''}>${i}</option>`).join('');
      const cb=(id,l,on)=>`<label style="display:flex;align-items:center;gap:8px;margin:6px 0"><input type="checkbox" id="${id}" ${on?'checked':''}> ${l}</label>`;
      openModal(`<h3>${k>=0?'Szabály szerkesztése':'Új szabály'}</h3>
        <label>Név</label><input id="pName" value="${esc(p.name)}" placeholder="pl. Éjszakai frissítés">
        <label style="display:block;margin-top:8px">Telephelyek (ha egyik sincs bejelölve: mind)</label>
        <div style="display:flex;gap:10px;flex-wrap:wrap">${window._polSites.map(s=>`<label style="display:flex;gap:4px;align-items:center"><input type="checkbox" class="pSite" value="${esc(s.siteId)}" ${(p.sites||[]).includes(s.siteId)?'checked':''}>${esc(s.name)}</label>`).join('')}</div>
        <label style="display:block;margin-top:8px">Gépcsoport</label><select id="pGroup" style="${sel}">${opts}</select>
        ${cb('pApps','Programok frissítése (winget)',p.apps)}${cb('pWin','Windows-frissítések telepítése',p.windows)}${cb('pReb','Újraindítás, ha a gép arra vár',p.reboot)}
        <label>Napok</label><div style="display:flex;gap:10px;flex-wrap:wrap">${[1,2,3,4,5,6,0].map(x=>`<label style="display:flex;gap:4px;align-items:center"><input type="checkbox" class="pDay" value="${x}" ${p.days.includes(x)?'checked':''}>${DAYS[x]}</label>`).join('')}</div>
        <div style="display:flex;gap:8px;margin-top:8px"><div style="flex:1"><label>Kezdete (óra)</label><select id="pStart" style="${sel}">${hours(p.startHour)}</select></div>
          <div style="flex:1"><label>Vége (óra)</label><select id="pEnd" style="${sel}">${hours(p.endHour)}</select></div></div>
        ${cb('pOn','Bekapcsolva',p.enabled)}<button class="btn primary small" style="margin-top:10px" onclick="savePolicy()">Mentés</button>`);}
    async function savePolicy(){const g=document.getElementById('pGroup').value,v=id=>document.getElementById(id);
      const p=Object.assign({},window._pEdit,{name:v('pName').value,group:g===''?'':window._polGroups[+g],apps:v('pApps').checked,windows:v('pWin').checked,reboot:v('pReb').checked,
        days:[...document.querySelectorAll('.pDay:checked')].map(c=>+c.value),sites:[...document.querySelectorAll('.pSite:checked')].map(c=>c.value),
        startHour:+v('pStart').value,endHour:+v('pEnd').value,enabled:v('pOn').checked});
      const r=await api('policy-save',{method:'POST',body:JSON.stringify(p)});
      if(r&&r.ok){toast('Szabály mentve – a telephelyek a következő betárcsázáskor átveszik','ok');showPolicies();}else toast((r&&r.error)||'Nem sikerült','err');}
    async function delPolicy(k){if(!await uiConfirm('Törlöd a szabályt?'))return;
      const r=await api('policy-delete',{method:'POST',body:JSON.stringify(window._pol[k])});if(r&&r.ok){toast('Törölve','ok');showPolicies();}}
    async function task(k,t,msg){if(!curM)return;if(msg&&!await uiConfirm(msg))return;
      const r=await api('task',{method:'POST',body:JSON.stringify({siteId:curM.sid,agentIds:[curM.aid],kind:k,target:t||''})});
      if(r&&r.ok)toast('Feladat betéve – a telephely a következő betárcsázáskor átveszi','ok');else if(r)toast(r.error||'hiba','err');}
    function wuOne(i){const u=curM.a.windowsUpdates[i];task('wuinstall',u.updateId,`Telepíted: ${u.kb||u.title}?`);}
    function unOne(i){const p=curM.a.installed[i];task('uninstall',p.id,`Eltávolítod: ${p.name}?`);}
    function rebootM(){const m=parseInt(document.getElementById('rbMin').value)||5;task('reboot',String(m*60),`A gép ${m} perc múlva újraindul. Biztos?`);}
    // ---- Monitorozás: a telephelyről betárcsázáskor érkező 5 perces átlagok, teljes oldalon (WebUi.MonitorJs),
    //      a Központ saját küszöbeivel. A „Vissza” a gép kezelő ablakához tér vissza.
    function showMon(){if(!curM)return;const m=curM,s=sites.find(x=>x.siteId===m.sid)||{};
      openMonitorPage({title:'Monitorozás – '+m.a.machineName,sub:(s.name?s.name+' · ':'')+'5 perces átlagok a telephelyről',
        warn:vNewer('1.5.0',m.a.agentVersion)?`Az agent ${m.a.agentVersion||'?'} – a bővített mérések (SMART-részletek, hardver-érzékelők, ping, biztonság…) az agent 1.5.0-ra frissülése után jelennek meg`:'',
        req:{siteId:m.sid,agentId:m.aid},ranges:[[6,'6 óra'],[24,'24 óra'],[168,'7 nap'],[720,'30 nap'],[2160,'90 nap']],back:()=>mach(m.sid,m.aid)});}
    // A „+ küszöb” a közös szabály-szerkesztőt nyitja (WebUi.RulesJs), erre a gépre kitöltve.
    function monAlert(key,label){ruEdit(null,{key,label,agentId:_mp.req.agentId});}
    // ---- Csoportok oldal (WebUi.GroupsJs): a csoportra kiadható műveletek a kiválasztott telephelyen.
    let _gl=[];
    function grActions(i){return `<button class="btn small" onclick="hgTask(${i},'upgradeall','')">Programok</button>
      <button class="btn small" onclick="hgTask(${i},'wuinstall','all')">Windows</button>
      <button class="btn small" onclick="hgInstall(${i})">Telepítés…</button>
      <button class="btn small danger" onclick="hgTask(${i},'reboot','300')">Újraindítás (5 perc)</button>`;}
    async function hgTask(i,k,t,label){const s=grSite(),g=s&&s.groups[i];if(!g)return;
      const what={upgradeall:'programfrissítés',wuinstall:'Windows-frissítések telepítése',reboot:'újraindítás 5 perc múlva',
        installbundle:'csoport telepítése: '+(label||''),install:'program telepítése: '+(label||'')}[k];
      if(!await uiConfirm(`${g.members.length} gépre (${g.name}, ${s.name}): ${what}?`))return;
      const r=await api('task',{method:'POST',body:JSON.stringify({siteId:s.siteId,agentIds:g.members,kind:k,target:t})});
      if(r&&r.ok)toast(`Feladat betéve – ${r.machines} gép`,'ok');else toast((r&&r.error)||'hiba','err');}
    function hgInstall(i){installPicker('Telepítés a csoportra',(k,t,l)=>hgTask(i,k,t,l));}
    async function showAlerts(){
      const d=await api('alerts',{method:'POST',body:'{}'}),lg=(await api('alert-log',{method:'POST',body:'{}'}))||{log:''};
      const ms=(await api('monitor-settings',{method:'POST',body:'{}'}))||{};const rules=(d&&d.rules)||[],act=(d&&d.active)||[];
      window._offGroups=(d&&d.groups)||[];const og=(d&&d.offlineGroups)||[],adm=window.ROLE>=2,dis=adm?'':'disabled';
      openView(`<h3>Riasztások</h3>
        ${act.length?`<div style="font-weight:600;margin:6px 0">Aktív most</div><table class="upd-table">${act.map(a=>`<tr><td>${esc(a.machine)}</td><td>${esc(a.name||'')}${a.metricKey?` <span class="mono" style="font-size:11px">${esc(a.metricKey)}</span>`:''}</td><td class="mono">${a.offline?Math.round(a.lastValue)+' perc':(+a.lastValue).toFixed(1)}</td><td>${esc(a.since||'')}</td></tr>`).join('')}</table>`:'<div class="empty" style="padding:16px">Most nincs aktív riasztás.</div>'}
        <div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin:12px 0">
          <div style="font-weight:600;margin-bottom:6px">Kiesés-riasztás ${adm?'':'<span style="font-weight:400;color:var(--muted)">(csak admin módosíthatja)</span>'}</div>
          <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Riaszt, ha egy telephely ennyi perce nem tárcsázott be (de legalább a betárcsázási időköz háromszorosa), illetve ha a telephely egy gépe ennyi perce nem jelentkezett. 0 = kikapcsolva, legalább 10.</div>
          <div style="display:flex;gap:8px;align-items:center"><input id="offMin" type="number" value="${d?d.offlineMinutes:15}" style="width:90px" ${dis}> perc</div>
          <label style="display:flex;align-items:center;gap:8px;margin-top:8px"><input type="checkbox" id="offMach" ${d&&d.offlineMachines?'checked':''} ${dis}> A gépek kiesése is (nem csak a telephelyé)</label>
          ${window._offGroups.length?`<div style="font-size:12px;color:var(--muted);margin-top:6px">Csak ezeknek a gépcsoportoknak a gépei (ha egyik sincs bejelölve: minden gép) – pl. az esténként kikapcsolt asztali gépek kihagyhatók:</div>
            <div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:4px">${window._offGroups.map((g,i)=>`<label style="display:flex;gap:4px;align-items:center;margin:0"><input type="checkbox" class="offGrp" value="${i}" ${og.includes(g)?'checked':''} ${dis}>${esc(g)}</label>`).join('')}</div>`:''}
          ${adm?'<button class="btn small primary" style="margin-top:10px" onclick="saveOffline()">Mentés</button>':''}
          <div style="font-size:12px;color:var(--muted);margin-top:6px">E-mail értesítés: Beállítások → E-mail értesítés.</div>
        </div>
        <div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin:12px 0;display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap">
          <div><div style="font-weight:600">Küszöb-szabályok</div><div style="font-size:12px;color:var(--muted)">${rules.length} szabály · ${rules.filter(r=>r.activeCount>0).length} riaszt most ·
            ${rules.filter(r=>r.pushDown).length} lemegy a telephelyekre. A szabályok a Monitorozás menüpontban (telephely / csoport / gép hatókörrel, „Mit mérjen?”).</div></div>
          <button class="btn small primary" onclick="showRules('rules')">Szabályok kezelése →</button></div>
        <div style="font-weight:600;margin:12px 0 6px">Riasztásnapló</div>
        <pre style="background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:10px;font-size:11px;max-height:180px;overflow:auto;white-space:pre-wrap">${esc(lg.log||'(még nincs riasztás)')}</pre>
        <div style="font-size:12px;color:var(--muted);margin-top:10px">Megőrzés: 5 perces átlag ${ms.detailDays||7} napig, a Központban ${ms.keepDays||90} napig olvasható (a telephelyek régebbi napi fájljaival együtt), utána archívum – a Beállításokban módosítható.</div>`);}
    async function saveOffline(){const groups=[...document.querySelectorAll('.offGrp:checked')].map(c=>window._offGroups[+c.value]);
      const r=await api('offline-save',{method:'POST',body:JSON.stringify({minutes:parseInt(document.getElementById('offMin').value)||0,machines:document.getElementById('offMach').checked,groups})});
      if(r&&r.ok){toast(r.minutes>0?`Kiesés-riasztás: ${r.minutes} perc`:'Kiesés-riasztás kikapcsolva','ok');showAlerts();}else toast((r&&r.error)||'Hiba','err');}
    async function revoke(id){if(!await uiConfirm('Visszavonod ezt a telephelyet?'))return;
      const r=await api('revoke',{method:'POST',body:JSON.stringify({siteId:id})});if(r&&r.ok){toast('Visszavonva','ok');refresh();}}
    function showToken(){openView(`<h3>Új telephely felvétele</h3>
      <label>Telephely neve / címke</label><input id="tl" placeholder="pl. Ügyfél Kft.">
      <button class="btn primary small" style="margin-top:14px" onclick="mkToken()">Token készítése</button>`);}
    async function mkToken(){const label=document.getElementById('tl').value.trim()||'telephely';
      const r=await api('token',{method:'POST',body:JSON.stringify({label})});if(!r||!r.token){toast('Nem sikerült','err');return}
      openView(`<h3>Telephely-token</h3>
        <p class="muted">Ezt és az ujjlenyomatot add meg a telephelyi Fleet Server → Beállítások → Központ kapcsolat résznél.
          A token ${r.lifetimeMinutes>0?r.lifetimeMinutes+' percig':'lejárat nélkül'} érvényes.</p>
        <label>Központ címe</label><div class="token">https://${esc(r.domain||'a-domained')}</div>
        <label>Token</label><div class="token">${r.token}</div>
        <label>Központ ujjlenyomat</label><div class="token" style="color:var(--muted)">${r.fingerprint}</div>`);}
    // ---- Központi napló (1.5.7): a telephely naplói (audit, riasztás, SNMP, szerver) – az USB-napló a telephelyen marad.
    let _slT=null;
    async function showSiteLogs(sid,log,q){q=q||'';const d=await api('site-logs',{method:'POST',body:JSON.stringify({siteId:sid,log,q})});if(!d)return;
      if(!d.ok){toast(d.error||'Nem érhető el','err');return;}
      const sz=b=>b>=1048576?(b/1048576).toFixed(1)+' MB':b>=1024?Math.round(b/1024)+' KB':b+' B';
      const first=!document.getElementById('slBody');
      const body=`<div style="font-size:12px;color:var(--muted);margin-bottom:6px">${d.lines.length?`${d.lines.length} sor (legújabb elöl)${q?' a szűrésre':''} · ${d.scanned} sor átnézve`:'Nincs sor'+(q?' a szűrésre.':' – a telephely az 1.5.7-es szerverrel küldi a naplóit.')}</div>
        <div class="mono" style="font-size:12px;white-space:pre-wrap;word-break:break-all;background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:10px;max-height:70vh;overflow:auto">${d.lines.map(esc).join('\n')||'–'}</div>`;
      if(!first){document.getElementById('slBody').innerHTML=body;return;}
      pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Naplók – ${esc(d.name)}</h2>
        <span class="sub">központi napló: a telephely szervere betárcsázáskor küldi (az USB-napló személyes adat, a telephelyen marad)</span></div>
        <div class="page-body"><div class="page-menu">${d.logs.map(l=>`<button class="${l.log===d.log?'sel':''}" onclick="pageClose();showSiteLogs('${sid}','${l.log}')"><span>${esc(l.label)}</span><span class="cnt">${sz(d.sizes[l.log]||0)}</span></button>`).join('')}</div>
        <div class="page-main"><div class="view" style="max-width:1300px">
          <input id="slQ" class="search" style="width:360px;max-width:100%;margin-bottom:10px" placeholder="Szűrés (minden szónak benne kell lennie)…" value="${esc(q)}"
            oninput="clearTimeout(_slT);_slT=setTimeout(()=>showSiteLogs('${sid}','${d.log}',document.getElementById('slQ').value.trim()),350)">
          <div id="slBody">${body}</div></div></div></div>`);}
    // ---- Sérülékenységek (1.5.6): NVD + CISA KEV + EPSS a telephelyek gépeire; naponta, vagy „Ellenőrzés most”.
    let _cveT=null;
    async function showCves(){const d=await api('cve-status',{method:'POST',body:'{}'});if(!d)return;closeModal();
      const t=d.totals||{};
      pageOpen(`<div class="page-head"><button class="btn small" onclick="pageClose()">← Vissza</button><h2>Sérülékenységek</h2>
        <span class="sub">a telephelyek gépeinek programjai, driverei, BIOS-a · ${d.lastRun?'utolsó teljes ellenőrzés: '+esc(d.lastRun):'még nem volt ellenőrzés'}</span>
        ${window.ROLE>=1?`<button class="btn small primary" id="cveRun" onclick="cveRun()" ${d.running?'disabled':''}>Ellenőrzés most</button>`:''}</div>
        <div class="page-body"><div class="page-main"><div class="view" style="max-width:1300px">
          <div id="cveProg"></div>
          ${cveSum(t)}
          <div style="font-size:13px;margin-bottom:12px">Érintett gépek: <b>${t.machines||0}</b></div>
          ${(d.machines||[]).length?`<h3 style="margin:6px 0 8px">Gépek</h3><table class="upd-table"><tr><th>Telephely</th><th>Gép</th><th style="text-align:right">KEV</th><th style="text-align:right">Kritikus</th><th style="text-align:right">Magas</th><th style="text-align:right">Közepes</th><th></th></tr>
            ${d.machines.map(m=>`<tr><td>${esc(m.site)}</td><td>${esc(m.machine)}</td><td style="text-align:right;color:${m.exploited?'var(--err)':'inherit'}">${m.exploited}</td>
              <td style="text-align:right;color:${m.critical?'var(--err)':'inherit'}">${m.critical}</td><td style="text-align:right">${m.high}</td><td style="text-align:right">${m.medium}</td>
              <td style="text-align:right"><button class="btn small" onclick="pageClose();mach('${m.siteId}','${m.agentId}','cve')">Részletek</button></td></tr>`).join('')}</table>`:''}
          <h3 style="margin:16px 0 8px">Leggyakoribb / legsúlyosabb CVE-k</h3>${cveTable(d.top,{machines:true})}
          <div style="font-size:12px;color:var(--muted);margin-top:10px">${esc(CVE_NOTE)}</div>
          ${window.ROLE>=2?`<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin-top:16px;max-width:760px">
            <div style="font-weight:600;margin-bottom:8px">Beállítás</div>
            <label style="display:flex;gap:8px;align-items:center;font-size:13px"><input type="checkbox" id="cveOn" ${d.enabled?'checked':''}> Napi ellenőrzés bekapcsolva,
              <input type="number" id="cveHour" min="0" max="23" value="${d.hour}" style="width:60px;background:var(--panel);border:1px solid var(--line);border-radius:7px;padding:5px;color:var(--ink)"> órától</label>
            <div style="font-size:13px;margin-top:10px">NVD API-kulcs ${d.hasKey?'<span class="badge ok">megadva</span>':'<span class="badge">nincs – kulcs nélkül lassabb (5 kérés / 30 mp)</span>'}</div>
            <input type="password" id="cveKey" autocomplete="off" placeholder="${d.hasKey?'új kulcs (üresen hagyva marad a mostani)':'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'}" style="width:100%;margin-top:6px;background:var(--panel);border:1px solid var(--line);border-radius:7px;padding:7px;color:var(--ink)">
            <div style="font-size:11px;color:var(--muted);margin-top:4px">Ingyenes kulcs: nvd.nist.gov/developers/request-an-api-key. Titkosítva tároljuk, a felület nem kapja vissza.</div>
            <div style="display:flex;gap:8px;margin-top:10px"><button class="btn small primary" onclick="cveSave(false)">Mentés</button>${d.hasKey?'<button class="btn small" onclick="cveSave(true)">Kulcs törlése</button>':''}</div></div>`:''}
        </div></div></div>`);
      cveProgDraw(d);}
    function cveProgDraw(d){const box=document.getElementById('cveProg');if(!box)return;
      clearTimeout(_cveT);
      if(d.running){const pct=d.total?Math.round(d.done*100/d.total):0;
        box.innerHTML=`<div style="background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:12px;margin-bottom:12px">
          <div style="font-size:13px"><b>${esc(d.phase||'Fut…')}</b> ${d.total?`${d.done} / ${d.total}`:''} ${d.current?`<span class="mono" style="color:var(--muted)">${esc(d.current)}</span>`:''}</div>
          <div class="pbar ${d.total?'':'ind'}"><div style="width:${pct}%"></div></div>
          <div style="font-size:11px;color:var(--muted);margin-top:4px">${d.requests} NVD-kérés eddig. Kulcs nélkül az NVD 30 másodpercenként 5 kérést enged – az első kör ezért akár fél óra is lehet.</div></div>`;
        _cveT=setTimeout(async()=>{const n=await api('cve-status',{method:'POST',body:'{}'});if(!n||!document.getElementById('cveProg'))return;
          if(n.running)cveProgDraw(n);else showCves();},3000);}
      else box.innerHTML=d.lastError?`<div style="font-size:13px;color:var(--warn);margin-bottom:12px">Utolsó hiba: ${esc(d.lastError)}</div>`:'';
      const b=document.getElementById('cveRun');if(b)b.disabled=!!d.running;}
    async function cveRun(){const r=await api('cve-run',{method:'POST',body:'{}'});
      if(r&&r.ok){toast('Ellenőrzés elindítva','ok');const d=await api('cve-status',{method:'POST',body:'{}'});if(d)cveProgDraw(d);}else toast((r&&r.error)||'Nem sikerült','err');}
    async function cveSave(clear){const g=id=>document.getElementById(id);
      if(clear&&!await uiConfirm('Törlöd az NVD API-kulcsot? Utána kulcs nélkül, lassabban kérdez.'))return;
      const r=await api('cve-save',{method:'POST',body:JSON.stringify({enabled:g('cveOn').checked,hour:parseInt(g('cveHour').value)||0,nvdPassword:clear?'':g('cveKey').value.trim(),clearKey:!!clear})});
      if(r&&r.ok){toast('Mentve','ok');showCves();}else toast((r&&r.error)||'Nem sikerült','err');}
    async function showSettings(){const i=await api('hub-info',{method:'POST',body:'{}'});if(!i)return;
      openView(`<h3>Beállítások</h3>
        <div style="font-size:13px;color:var(--muted)">Verzió <b style="color:var(--ink)">${i.version}</b> · domain <b style="color:var(--ink)">${esc(i.domain||'–')}</b> · cím <b style="color:var(--ink)">${esc(i.localIp||'')}:${i.webPort}</b></div>
        <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
          <div style="font-weight:600;margin-bottom:8px">NGINX Proxy Manager beállítás</div>
          <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Az útmutató (proxy host + admin-korlátozás) itt van a szerveren:</div>
          <div class="token" style="font-size:12px">${esc(i.caddyDir||'')}\NPM-beallitas.txt</div>
          <div style="font-size:12px;color:var(--muted);margin-top:8px">Egy proxy host a domainhez → ${esc(i.localIp||'')}:${i.webPort}, SSL fülön Let's Encrypt. Cloudflare: DNS only.</div>
        </div>

        <div style="border-top:1px solid var(--line);margin-top:16px;padding-top:14px">
          <div style="font-weight:600;margin-bottom:8px">Központ frissítése</div>
          <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Töltsd fel az új FleetHub.exe-t. A csere a szolgáltatás újraindításakor történik.</div>
          <input id="hubUpd" type="file" accept=".exe" style="font-size:12px">
          <button class="btn primary small" style="margin-top:12px" onclick="uploadHub()">Feltöltés</button>
          <div id="hubUpdP" style="font-size:12px;color:var(--muted);margin-top:8px">${i.pendingUpdate?'<span style=\'color:var(--warn)\'>Feltöltött frissítés vár – újraindításkor lép életbe.</span>':''}</div>
        </div>
        <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
          <div style="font-weight:600;margin-bottom:8px">Telephely token alapértéke</div>
          <div class="row"><div class="col"><label style="margin-top:0">Élettartam (perc, 0=nincs)</label><input id="tll" type="number" value="${i.tokenLifetimeMinutes}"></div>
            <div class="col"><label style="margin-top:0">Max. telephely (0=korlátlan)</label><input id="tlu" type="number" value="${i.tokenMaxUses}"></div></div>
          <button class="btn primary small" style="margin-top:12px" onclick="saveSet()">Mentés</button>
        </div>
        <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
          <div style="font-weight:600;margin-bottom:8px">Adatmegőrzés ${window.ROLE>=2?'':'<span style="font-weight:400;color:var(--muted)">(csak admin módosíthatja)</span>'}</div>
          <div class="row"><div class="col"><label style="margin-top:0">A telephelyek szerverein, nap</label><input id="sSite" type="number" value="${i.siteKeepDays||30}" ${window.ROLE>=2?'':'disabled'}></div>
            <div class="col"><label style="margin-top:0">A Központban olvasható, nap</label><input id="sKeep" type="number" value="${i.keepDays}" ${window.ROLE>=2?'':'disabled'}></div>
            <div class="col"><label style="margin-top:0">Archívum összesen, nap</label><input id="sArch" type="number" value="${i.archiveDays||180}" ${window.ROLE>=2?'':'disabled'}></div>
            <div class="col"><label style="margin-top:0">5 perces átlag, nap</label><input id="sDet" type="number" value="${i.detailDays}" ${window.ROLE>=2?'':'disabled'}></div></div>
          <div style="font-size:12px;color:var(--muted);margin-top:6px">A telephely szervere a mérési adatait naponta külön fájlban tárolja; a <b>lezárt</b> (48 óránál régebbi) nap fájlja a beállított idő
            után feltöltődik ide, és a szerverről törlődik – utána csak itt látszik (egy példány marad: a Központ saját 5 perces másolata arra a napra ilyenkor törlődik).
            A Központban az „olvasható” idő után ZIP-archívumba kerül, az „összesen” idő után törlődik. A naplók helye és mérete: lent, a Tárolás résznél.</div>
          ${window.ROLE>=2?'<button class="btn primary small" style="margin-top:12px" onclick="saveRetention()">Mentés</button>':''}
        </div>
        ${window.ROLE>=2?'<div id="storageBox" style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px"></div>':''}
        <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
          <div style="font-weight:600;margin-bottom:8px">Automatikus frissítési szabályok (${i.policyCount})</div>
          <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Karbantartási ablakban programok / Windows frissítése, telephely- és csoport-hatókörrel. A szabály lemegy a telephelyre, és ott fut, annak helyi idejében. Alapból nincs szabály.</div>
          <button class="btn small" onclick="showPolicies()">Szabályok kezelése</button>
        </div>
        ${window.ROLE>=2?'<div id="wgBox" style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px"></div><div id="mailBox" style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px"></div><div id="janusBox" style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px"></div>':''}`);
      if(window.ROLE>=2){loadWg();loadMail();loadJanus();loadStorage();}}
    // ---- WireGuard (admin, 1.5.9): a Központ és a telephelyek közötti alagút. Alapból be; UDP 51820; a telephelyek csak
    //      ezen tárcsázhatnak be (szigorú mód). A privát kulcs soha nem jön vissza a felületre.
    async function loadWg(){const w=await api('wg-settings',{method:'POST',body:'{}'});const box=document.getElementById('wgBox');if(!w||!box||!w.ok)return;
      const sz=b=>b>=1048576?(b/1048576).toFixed(1)+' MB':b>=1024?Math.round(b/1024)+' KB':b+' B';
      const rows=(w.sites||[]).map(s=>`<tr><td>${esc(s.name)}</td><td class="mono" style="font-size:11px">${esc(s.address||'–')}</td>
        <td>${s.hasKey?(s.handshake?'<span class="badge ok">él</span> '+esc(s.handshake):'<span class="badge upd" style="color:var(--warn)">nincs kézfogás</span>'):'<span class="badge">nincs kulcs</span>'}</td>
        <td style="text-align:right;font-size:11px">${sz(s.rx||0)} / ${sz(s.tx||0)}</td>
        <td style="text-align:right">${s.active?`<button class="btn small" onclick="wgSite('${s.siteId}',false)" title="A szigorú mód kikapcsolása: a telephely újra tárcsázhat HTTPS-en is">szigorú: be</button>`:'<span style="font-size:11px;color:var(--muted)">HTTPS is</span>'}</td></tr>`).join('');
      box.innerHTML=`<div style="font-weight:600;margin-bottom:8px">WireGuard-alagút ${w.running?'<span class="badge ok">fut</span>':w.enabled?'<span class="badge upd" style="color:var(--err)">nem fut</span>':'<span class="badge">kikapcsolva</span>'}</div>
        <div style="font-size:12px;color:var(--muted);margin-bottom:8px">A telephelyek a Fleet Serverrel ezen az alagúton tárcsáznak be. Kívülről a megadott UDP-portot a routeren / NPM-ben (Streams) a Központ gépére kell továbbítani.${w.lastError?`<br><span style="color:var(--err)">${esc(w.lastError)}</span>`:''}</div>
        <label style="display:flex;align-items:center;gap:8px;font-size:13px"><input type="checkbox" id="wgOn" ${w.enabled?'checked':''}> Bekapcsolva</label>
        <div class="row" style="margin-top:8px"><div class="col"><label style="margin-top:0">UDP-port</label><input id="wgPort" type="number" value="${w.port}"></div>
          <div class="col"><label style="margin-top:0">Címtartomány (magáncím /16–/29)</label><input id="wgNet" value="${esc(w.subnet)}"></div></div>
        <label style="margin-top:8px">Végpont (gépnév:port, amit a telephelyek hívnak; üresen: ${esc(w.effectiveEndpoint||'')})</label>
        <input id="wgEp" placeholder="fleet.poltoli.hu:${w.port}" value="${esc(w.endpoint||'')}">
        <div class="row" style="margin-top:8px"><div class="col"><label style="margin-top:0">Keepalive (mp)</label><input id="wgKa" type="number" value="${w.keepalive}"></div>
          <div class="col" style="display:flex;align-items:flex-end"><label style="display:flex;align-items:center;gap:8px;margin:0 0 8px"><input type="checkbox" id="wgStrict" ${w.strictAll?'checked':''}> Minden telephely csak alagúton</label></div></div>
        <div style="font-size:11px;color:var(--muted);margin-top:4px">Központ címe az alagútban: <span class="mono">${esc(w.hubAddress||'–')}</span> · nyilvános kulcs: <span class="mono">${esc((w.publicKey||'').slice(0,16))}…</span></div>
        <div style="display:flex;gap:8px;margin-top:10px;flex-wrap:wrap"><button class="btn small primary" onclick="wgSave()">Mentés</button>
          <button class="btn small danger" onclick="wgRekey()">Új Központ-kulcs</button></div>
        ${rows?`<div style="font-weight:600;margin:12px 0 6px;font-size:13px">Telephelyek az alagútban</div><table class="upd-table"><tr><th>Telephely</th><th>Cím</th><th>Állapot</th><th style="text-align:right">le / fel</th><th></th></tr>${rows}</table>`:''}
        <div id="wgMsg" style="font-size:12px;color:var(--muted);margin-top:6px"></div>`;}
    async function wgSave(){const g=id=>document.getElementById(id),m=g('wgMsg');m.textContent='Mentés…';
      const r=await api('wg-save',{method:'POST',body:JSON.stringify({enabled:g('wgOn').checked,port:parseInt(g('wgPort').value)||51820,subnet:g('wgNet').value.trim(),endpoint:g('wgEp').value.trim(),keepalive:parseInt(g('wgKa').value)||25,strictAll:g('wgStrict').checked})});
      if(r&&r.ok){toast('WireGuard mentve','ok');loadWg();}else{m.textContent='';toast((r&&r.error)||'Nem sikerült','err');}}
    async function wgSite(siteId,active){const r=await api('wg-site',{method:'POST',body:JSON.stringify({siteId,active})});if(r&&r.ok){toast('Mentve','ok');loadWg();}else toast((r&&r.error)||'Hiba','err');}
    async function wgRekey(){if(!await uiConfirm('Új Központ-kulcsot készítesz. A telephelyek az aláírt csatornán kapják meg az új kulcsot, addig az alagutuk nem él. Folytatod?'))return;
      const r=await api('wg-rekey',{method:'POST',body:'{}'});if(r&&r.ok){toast('Új kulcs kész','ok');setTimeout(loadWg,1000);}else toast((r&&r.error)||'Hiba','err');}
    // ---- Janus-kapcsolat (admin): a Központ a telephelyek géplistáját és riasztásait a Janusba küldi (ott hibajegy lesz).
    //      A titok nem jön vissza: üresen hagyva marad a tárolt.
    async function loadJanus(){const j=await api('janus-settings',{method:'POST',body:'{}'});const box=document.getElementById('janusBox');if(!j||!box||j.error)return;window._js=j.sites||[];
      box.innerHTML=`<div style="font-weight:600;margin-bottom:8px">Janus-kapcsolat</div>
        <div style="font-size:12px;color:var(--muted);margin-bottom:8px">A Központ a telephelyek géplistáját (15 percenként vagy változáskor) és a riasztásait (küszöb, kiesés; a megszűnést is) a Janusba küldi –
          a Janus a figyelmeztető / kritikus riasztásból hibajegyet nyit az ügyfélnek. A Janusban: Rendszer → Fleetserver-kapcsolat → ügyfelenként „Hozzáférés létrehozása”;
          a kapott kulcsazonosítót és titkot itt, az ügyfél telephelyénél add meg.</div>
        <label style="margin-top:0">A Janus végpontja</label><input id="jUrl" value="${esc(j.url||'')}" placeholder="https://janus.example.hu/api/fleet.php">
        <label style="display:flex;align-items:center;gap:8px"><input type="checkbox" id="jOn" ${j.enabled?'checked':''}> Küldés bekapcsolva</label>
        <button class="btn primary small" style="margin-top:8px" onclick="saveJanus()">Mentés</button>
        <table class="upd-table" style="margin-top:12px"><tr><th>Telephely</th><th>Kulcsazonosító</th><th>Titok</th><th>Állapot</th><th></th></tr>
        ${window._js.map((s,i)=>`<tr><td>${esc(s.name)}</td><td><input id="jKey${i}" value="${esc(s.keyId||'')}" placeholder="16 hexa" style="width:150px"></td>
          <td><input id="jSec${i}" type="password" autocomplete="new-password" placeholder="${s.hasSecret?'tárolva – üresen marad':'nincs'}" style="width:170px"></td>
          <td style="font-size:12px">${s.lastError?`<span style="color:var(--err)">${esc(s.lastError)}</span>`:s.lastOk?`<span style="color:var(--ok)">rendben (${esc(s.lastOk)})</span>`:(s.hasSecret?'még nem küldött':'<span style="color:var(--muted)">nincs beállítva</span>')}</td>
          <td style="text-align:right;white-space:nowrap"><button class="btn small primary" onclick="janusSite(${i},false)">Mentés</button> <button class="btn small" onclick="janusTest(${i})">Próba</button>
            ${s.hasSecret?`<button class="btn small danger" onclick="janusSite(${i},true)">Törlés</button>`:''}</td></tr>`).join('')||'<tr><td colspan="5" style="color:var(--muted)">Még nincs telephely.</td></tr>'}</table>
        <div id="jRes" style="font-size:12px;margin-top:8px"></div>`;}
    async function saveJanus(){const r=await api('janus-save',{method:'POST',body:JSON.stringify({url:document.getElementById('jUrl').value,enabled:document.getElementById('jOn').checked})});
      if(r&&r.ok){toast('Janus-kapcsolat mentve','ok');loadJanus();}else toast((r&&r.error)||'Hiba','err');}
    async function janusSite(i,clear){const s=window._js[i];if(clear&&!await uiConfirm('Törlöd a(z) '+s.name+' Janus-kulcsát?'))return;
      const r=await api('janus-site',{method:'POST',body:JSON.stringify({siteId:s.siteId,keyId:clear?'':document.getElementById('jKey'+i).value,janusPassword:clear?'':document.getElementById('jSec'+i).value,clear:!!clear})});
      if(r&&r.ok){toast(clear?'Törölve':'Mentve','ok');loadJanus();}else toast((r&&r.error)||'Hiba','err');}
    async function janusTest(i){const s=window._js[i],res=document.getElementById('jRes');uiBusy(res,'Próba…');
      const r=await api('janus-test',{method:'POST',body:JSON.stringify({siteId:s.siteId})});
      res.innerHTML=r&&r.ok?`<span style="color:var(--ok)">${esc(s.name)}: ${esc(r.message||'rendben')} A géplista is elment.</span>`:`<span style="color:var(--err)">${esc(s.name)}: ${esc((r&&r.error)||'Nem sikerült')}</span>`;
      setTimeout(loadJanus,1500);}
    // ---- E-mail értesítés (csak a Központ küld; admin). A jelszó nem jön vissza: üresen hagyva marad a régi.
    async function loadMail(){const m=await api('mail-settings',{method:'POST',body:'{}'});const box=document.getElementById('mailBox');if(!m||!box)return;
      const sel='width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)';
      box.innerHTML=`<div style="font-weight:600;margin-bottom:8px">E-mail értesítés</div>
        <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Riasztás keletkezésekor és megszűnésekor, valamint kiesésnél (telephely / gép nem jelentkezik) levelet küld.
          A percen belüli események egy levélbe kerülnek. Csak a Központ küld, a telephelyeken nincs ilyen beállítás.</div>
        <label style="display:flex;align-items:center;gap:8px;margin-top:0"><input type="checkbox" id="mOn" ${m.enabled?'checked':''}> Értesítés bekapcsolva</label>
        <div class="row"><div class="col" style="flex:2"><label>SMTP-szerver</label><input id="mHost" value="${esc(m.host||'')}" placeholder="pl. smtp.office365.com"></div>
          <div class="col"><label>Port</label><input id="mPort" type="number" value="${m.port||587}"></div></div>
        <label>Titkosítás</label><select id="mSec" style="${sel}" onchange="const p=document.getElementById('mPort');p.value={starttls:587,ssl:465,none:25}[this.value]">
          <option value="starttls" ${m.security==='starttls'?'selected':''}>STARTTLS (587)</option><option value="ssl" ${m.security==='ssl'?'selected':''}>SSL/TLS (465)</option>
          <option value="none" ${m.security==='none'?'selected':''}>Nincs (25 – csak belső relay, jelszó nélkül)</option></select>
        <div class="row"><div class="col"><label>Felhasználónév</label><input id="mUser" value="${esc(m.user||'')}" autocomplete="off"></div>
          <div class="col"><label>Jelszó</label><input id="mPw" type="password" autocomplete="new-password" placeholder="${m.hasPassword?'tárolva – üresen hagyva marad':'nincs megadva'}"></div></div>
        ${m.hasPassword?'<label style="display:flex;align-items:center;gap:8px"><input type="checkbox" id="mPwClr"> Tárolt jelszó törlése</label>':''}
        <label>Feladó címe</label><input id="mFrom" value="${esc(m.from||'')}" placeholder="pl. fleet@poltoli.hu">
        <label>Címzettek (vesszővel)</label><input id="mTo" value="${esc(m.to||'')}" placeholder="pl. rendszergazda@poltoli.hu, ugyelet@poltoli.hu">
        <div style="display:flex;gap:8px;margin-top:12px"><button class="btn primary small" onclick="saveMail(false)">Mentés</button>
          <button class="btn small" onclick="saveMail(true)">Mentés és teszt e-mail</button></div>
        <div id="mStat" style="font-size:12px;color:var(--muted);margin-top:8px">${m.lastSent?'Utolsó sikeres küldés: '+esc(m.lastSent):''}${m.lastError?` <span style="color:var(--err)">Hiba: ${esc(m.lastError)}</span>`:''}${m.queued?' · sorban: '+m.queued:''}</div>`;}
    async function saveMail(test){const v=id=>document.getElementById(id);const st=v('mStat');
      const body={enabled:v('mOn').checked,host:v('mHost').value,port:parseInt(v('mPort').value)||587,security:v('mSec').value,user:v('mUser').value,
        smtpPassword:v('mPw').value,clearPassword:!!(v('mPwClr')&&v('mPwClr').checked),from:v('mFrom').value,to:v('mTo').value};
      if(test)st.textContent='Küldés…';
      const r=await api(test?'mail-test':'mail-save',{method:'POST',body:JSON.stringify(body)});
      if(r&&r.ok){toast(test?'Teszt e-mail elküldve':'E-mail beállítás mentve','ok');await loadMail();if(test)document.getElementById('mStat').innerHTML='<span style="color:var(--ok)">'+esc(r.message||'Elküldve')+'</span>';}
      else{const e=(r&&r.error)||'Hiba';toast(e,'err');st.innerHTML='<span style="color:var(--err)">'+esc(e)+'</span>';}}
    async function saveRetention(){const v=id=>parseInt(document.getElementById(id).value);
      const r=await api('settings',{method:'POST',body:JSON.stringify({tokenLifetimeMinutes:parseInt(document.getElementById('tll').value)||0,tokenMaxUses:parseInt(document.getElementById('tlu').value)||0,
        detailDays:v('sDet')||7,keepDays:v('sKeep')||90,siteKeepDays:v('sSite')||30,archiveDays:v('sArch')||180})});
      if(r&&r.ok)toast('Adatmegőrzés mentve','ok');else toast((r&&r.error)||'Hiba','err');}
    async function uploadHub(){const f=document.getElementById('hubUpd').files[0];if(!f){toast('Válassz exe fájlt','err');return}
      const p=document.getElementById('hubUpdP');
      const j=await uiUpload('/web/api/upload-hub',f,p);   // haladásjelzővel (WebUi.DialogJs)
      if(j.ok){p.innerHTML=uiBar(100)+'<span style="color:var(--ok)">'+esc(j.message||'Feltöltve')+'</span>';toast('Feltöltve','ok');}
      else p.innerHTML='<span style="color:var(--err)">'+esc(j.error||'hiba')+'</span>';}
    async function saveSet(){const r=await api('settings',{method:'POST',body:JSON.stringify({
      tokenLifetimeMinutes:parseInt(document.getElementById('tll').value)||0,tokenMaxUses:parseInt(document.getElementById('tlu').value)||0})});
      if(r&&r.ok)toast('Mentve','ok');}
    function openModal(h,wide){document.getElementById('modalBody').innerHTML=h;const m=document.querySelector('#modal .modal');if(m)m.classList.toggle('wide',!!wide);
      document.getElementById('modal').classList.add('show');}
    function closeModal(){document.getElementById('modal').classList.remove('show');}
    let tt;function toast(m,k){const t=document.getElementById('toast');t.textContent=m;t.className='toast show '+(k||'');
      clearTimeout(tt);tt=setTimeout(()=>t.className='toast',2600);}
    let _allSites=[];
    async function showUsers(){const d=await api('users',{method:'POST',body:'{}'});if(!d||!d.users){toast('Nincs jog','err');return}
      _allSites=d.allSites||[];
      const rows=d.users.map(u=>`<tr><td>${esc(u.username)}${u.disabled?' <span style="color:var(--warn)">(tiltva)</span>':''}</td><td>${esc(u.roleName)}</td>
        <td style="font-size:12px;color:var(--muted)">${u.allSites?'minden telephely':(u.siteScope||[]).length+' telephely'}</td>
        <td style="text-align:right"><button class="btn small" onclick='editUser(${JSON.stringify(u.username)},${u.role},${u.disabled},${JSON.stringify(u.siteScope||[])},${u.allSites})'>Módosít</button></td></tr>`).join('');
      openView(`<h3>Felhasználók</h3><table class="upd-table"><tr><th>Név</th><th>Szerep</th><th>Hatókör</th><th></th></tr>${rows}</table>
        <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px"><div style="font-weight:600;margin-bottom:8px">Új felhasználó</div>
        <input id="nu" placeholder="név"><input id="np" type="password" placeholder="jelszó (min.12, 3 típus)" style="margin-top:8px">
        <select id="nr" style="margin-top:8px;width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">
        <option value="0">Megfigyelő</option><option value="1">Kezelő</option><option value="2">Adminisztrátor</option></select>
        <button class="btn primary small" style="margin-top:12px" onclick="createUser()">Létrehozás</button></div>`);}
    async function createUser(){const r=await api('user-create',{method:'POST',body:JSON.stringify({username:document.getElementById('nu').value,password:document.getElementById('np').value,role:parseInt(document.getElementById('nr').value)})});if(r&&r.ok){toast('Kész','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
    function editUser(n,role,dis,scope,allSites){window._editScope={n,scope:scope||[],all:allSites};
      const sc=_allSites.length? `<div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px">
        <label>Hatókör – mely telephelyeket látja</label>
        <label style="display:flex;align-items:center;gap:8px;margin:8px 0"><input type="checkbox" id="scAll" ${allSites?'checked':''} onchange="document.getElementById('scList').style.display=this.checked?'none':'block'"> Minden telephely</label>
        <div id="scList" style="display:${allSites?'none':'block'};max-height:160px;overflow:auto">
        ${_allSites.map(s=>`<label style="display:flex;align-items:center;gap:8px;margin:4px 0"><input type="checkbox" class="scItem" value="${esc(s.id)}" ${(scope||[]).includes(s.id)?'checked':''}> ${esc(s.name)}</label>`).join('')}</div>
        <button class="btn small" style="margin-top:8px" onclick="setScope('${esc(n)}')">Hatókör mentése</button></div>` : '';
      openModal(`<h3>${esc(n)}</h3><label>Szerepkör</label>
      <select id="er" style="width:100%;padding:10px;background:var(--panel2);border:1px solid var(--line);border-radius:9px;color:var(--ink)">
      <option value="0"${role==0?' selected':''}>Megfigyelő</option><option value="1"${role==1?' selected':''}>Kezelő</option><option value="2"${role==2?' selected':''}>Adminisztrátor</option></select>
      <button class="btn small" style="margin-top:10px" onclick="setRole('${esc(n)}')">Szerepkör mentése</button>
      <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px"><label>Új jelszó</label><input id="ep" type="password">
      <button class="btn small" style="margin-top:10px" onclick="setPw('${esc(n)}')">Jelszó mentése</button></div>
      <div style="border-top:1px solid var(--line);margin-top:14px;padding-top:14px;display:flex;gap:8px">
      <button class="btn small" onclick="disU('${esc(n)}',${!dis})">${dis?'Engedélyezés':'Letiltás'}</button>
      <button class="btn small danger" onclick="delU('${esc(n)}')">Törlés</button></div>${sc}`);}
    async function setScope(n){const all=document.getElementById('scAll').checked;
      const scope=all?[]:[...document.querySelectorAll('.scItem:checked')].map(c=>c.value);
      const r=await api('user-scope',{method:'POST',body:JSON.stringify({username:n,siteScope:scope})});
      if(r&&r.ok){toast('Hatókör mentve','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
    async function setRole(n){const r=await api('user-role',{method:'POST',body:JSON.stringify({username:n,role:parseInt(document.getElementById('er').value)})});if(r&&r.ok){toast('Mentve','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
    async function setPw(n){const r=await api('user-password',{method:'POST',body:JSON.stringify({username:n,password:document.getElementById('ep').value})});if(r&&r.ok)toast('Jelszó módosítva','ok');else toast((r&&r.error)||'Hiba','err');}
    async function disU(n,d){const r=await api('user-disable',{method:'POST',body:JSON.stringify({username:n,disabled:d})});if(r&&r.ok){toast('Kész','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
    async function delU(n){if(!await uiConfirm(n+' törlése?'))return;const r=await api('user-delete',{method:'POST',body:JSON.stringify({username:n})});if(r&&r.ok){toast('Törölve','ok');showUsers();}else toast((r&&r.error)||'Hiba','err');}
    function esc(s){return String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}
    // Biztonság – kulcsok, telephely-tokenek, műveleti napló: teljes oldalon (WebUi.KeysJs).
    async function rotateKeys(){
      if(!await uiConfirm('Biztosan cseréled most a Központ kulcsát? A telephelyek a következő betárcsázáskor ellenőrzötten átállnak (előtte mentés készül).'))return;
      const r=await api('rotate-keys',{method:'POST',body:'{}'});
      if(r&&r.ok){toast('Kulcs cserélve – új ujjlenyomat: '+r.fingerprint.slice(0,12)+'…','ok');showSecurity('own');}
      else toast((r&&r.error)||'Nem sikerült','err');
    }
    refresh();setInterval(refresh,15000);
    """;
}
