// A RenderCheck kimenetének (fleet_admin.html, hub.html …) kiszolgálása localhoston - a felület megnézéséhez
// böngészőben, szerver és rendszergazdai jog nélkül. A /web/api/... hívások 404-et kapnak: a tesztadatot a
// böngészőben kell megadni (window.api felülírásával). Használat: node serve_render.js <mappa> [port]
const http = require('http'), fs = require('fs'), path = require('path');
const dir = path.resolve(process.argv[2] || '.'), port = +(process.argv[3] || 18555);
http.createServer((req, res) => {
  const name = decodeURIComponent((req.url || '/').split('?')[0]).replace(/^\/+/, '') || 'fleet_admin.html';
  const file = path.join(dir, name);
  if (!file.startsWith(dir) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end('{}'); return; }
  res.writeHead(200, { 'Content-Type': file.endsWith('.html') ? 'text/html; charset=utf-8' : 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}).listen(port, '127.0.0.1', () => console.log(`http://localhost:${port}/  <-  ${dir}`));
