"""Közös segédek a böngészős tesztekhez (Playwright, fej nélküli Chromium)."""
import sys
from playwright.sync_api import sync_playwright

class Run:
    def __init__(self): self.res=[]; self.errs=[]
    def chk(self,name,ok,info=""):
        self.res.append(bool(ok)); print(("  OK   " if ok else "  HIBA ")+name+(("  -> "+str(info)) if info else ""))
    def page(self,browser,w=1440,h=1000):
        pg=browser.new_page(viewport={"width":w,"height":h})
        pg.on("pageerror", lambda e: self.errs.append(str(e)))
        pg.on("console", lambda m: self.errs.append(m.text) if m.type=="error" else None)
        pg.on("dialog", lambda d: d.accept())
        return pg
    def login(self,pg,base,user="admin",pw="SzuperEros2024xy",wait=".topbar"):
        pg.goto(base+"/web"); pg.fill("input[name=user]",user); pg.fill("input[name=password]",pw)
        pg.click("button[type=submit]"); pg.wait_for_selector(wait, timeout=10000)
    def opens(self,pg,text,timeout=4000):
        try: pg.wait_for_selector(f"#modalBody >> text={text}", timeout=timeout); return True
        except Exception: return False
    def close(self,pg): pg.click("#modal", position={"x":1400,"y":20}); pg.wait_for_timeout(150)
    def finish(self):
        self.chk("Nincs JavaScript-hiba", len(self.errs)==0, "; ".join(self.errs[:3]))
        print("\n"+("MINDEN TESZT ATMENT" if all(self.res) else f"{self.res.count(False)} BUKOTT"))
        sys.exit(0 if all(self.res) else 1)

def browser():
    p=sync_playwright().start(); return p, p.chromium.launch()
