"""Központ webfelület: oldalsáv, Biztonság. Adat: az e2e/HubDial után."""
import sys
from common import Run, browser
H=sys.argv[1] if len(sys.argv)>1 else "http://localhost:18090"
r=Run(); p,b=browser()
pg=r.page(b); r.login(pg,H)
bb=pg.locator(".topbar").bounding_box()
r.chk("Központ: oldalsáv bal oldalon", bb and bb["x"]==0 and bb["width"]<260 and bb["height"]>800, bb)
for name,text in [("Biztonság","Kulcscsere indítása"),("Beállítások","Központ frissítése"),("Új telephely","Új telephely felvétele")]:
    pg.locator(".topbar .ghost", has_text=name).first.click(); r.chk(f"Központ: „{name}”", r.opens(pg,text)); r.close(pg)
b.close(); p.stop(); r.finish()
