// A RenderCheck által előállított oldalak ellenőrzése node-dal (Python nem kell):
//  - minden <script> szintaktikailag helyes (vm.Script, futtatás nélkül);
//  - minden onclick egy létező függvényt hív;
//  - a szerepkör számként kerül a szkriptbe (window.ROLE=<szám>) - a Központban ez korábban kimaradt.
const fs = require('fs'), path = require('path'), vm = require('vm');
const dir = process.argv[2] || '.';
let ok = true;
for (const f of ['fleet_admin.html', 'fleet_nezo.html', 'hub.html']) {
  const html = fs.readFileSync(path.join(dir, f), 'utf8');
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1]);
  const js = scripts.join('\n');
  let syntax = 'jó';
  scripts.forEach((s, i) => { try { new vm.Script(s, { filename: `${f}#${i + 1}` }); } catch (e) { syntax = `#${i + 1}: ${e.message}`; } });
  // Az oldal szkriptjei közös globális térben futnak: ugyanaz a let/const név két szkriptben futáskori hiba.
  // Egyben értelmezve ez szintaktikai hibaként jön ki.
  if (syntax === 'jó') { try { new vm.Script(js, { filename: `${f} (együtt)` }); } catch (e) { syntax = `együtt: ${e.message}`; } }
  const defined = new Set([...js.matchAll(/(?:async\s+)?function\s+([A-Za-z_]\w*)/g)].map(m => m[1]));
  const called = new Set([...html.matchAll(/onclick=\\?["']([A-Za-z_]\w*)\(/g)].map(m => m[1]));
  const missing = [...called].filter(c => !defined.has(c) && c !== 'if' && c !== 'confirm').sort();
  const role = /window\.ROLE\s*=\s*\d+/.test(js);
  const good = syntax === 'jó' && missing.length === 0 && role;
  ok = ok && good;
  console.log(`  ${good ? 'OK  ' : 'HIBA'} ${f.padEnd(18)} szintaxis: ${syntax}, gombok: ${called.size}, hiányzó függvény: ${missing.join(', ') || 'nincs'}, szerepkör: ${role ? 'megvan' : 'HIÁNYZIK'}`);
}
console.log('\n' + (ok ? 'MINDEN TESZT ATMENT' : 'BUKOTT'));
process.exit(ok ? 0 : 1);
