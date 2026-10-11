"""FleetServer webfelület: Monitorozás, Riasztások, Biztonság, oldalsáv. Adat: az e2e/Monitoring után."""
import sys
from common import Run, browser
W=sys.argv[1] if len(sys.argv)>1 else "http://localhost:19380"
r=Run(); p,b=browser()
pg=r.page(b); r.login(pg,W,wait=".agent")
bb=pg.locator(".topbar").bounding_box()
r.chk("Oldalsáv bal oldalon, teljes magasságban", bb and bb["x"]==0 and bb["width"]<260 and bb["height"]>800, bb)
r.chk("A tartalom az oldalsáv mellett kezdődik", (pg.locator(".stats").bounding_box() or {}).get("x",0)>=236)
pg.locator(".agent", has_text="TESZT-PC").first.click()
pg.locator("button", has_text="Monitorozás").first.click()
r.chk("Monitorozás ablak megnyílik", r.opens(pg,"Monitorozás – TESZT-PC")); pg.wait_for_timeout(1200)
r.chk("Grafikonok kirajzolva", pg.locator("#modalBody svg polyline").count()>=1, pg.locator("#modalBody svg polyline").count())
r.chk("Időtartomány-váltó", pg.locator("#modalBody button", has_text="7 nap").count()==1)
pg.locator("#modalBody button", has_text="+ küszöb").first.click()
r.chk("„+ küszöb” űrlap", pg.locator("#aVal").count()==1); r.close(pg)
for name,text in [("Riasztások","CPU magas"),("Biztonság","Kulcscsere indítása"),("Beállítások","Fleet Server frissítése"),
                  ("Felhasználók","Felhasználók"),("Új agent","Új agent hozzáadása"),("Csoportok","Gépcsoportok"),
                  ("Programok","Programkatalógus"),("Szabályok","Automatikus frissítési")]:
    pg.locator(".topbar .ghost", has_text=name).first.click(); r.chk(f"Oldalsáv: „{name}”", r.opens(pg,text)); r.close(pg)
pg2=r.page(b,800,900); r.login(pg2,W,wait=".agent")
r.chk("Keskeny ablakban a menü felül marad", (pg2.locator(".topbar").bounding_box() or {}).get("width",0)>=780)
b.close(); p.stop(); r.finish()
