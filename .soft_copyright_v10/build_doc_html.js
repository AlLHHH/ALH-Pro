// 把 V1.0 说明书 Markdown 转成带页眉页码的 HTML（再经 Chrome 转 PDF）
const fs = require('fs');
const path = require('path');
const md = fs.readFileSync(path.join(__dirname, 'ALHPro_软件功能说明书_v1.0.md'), 'utf8').replace(/^\uFEFF/, '');

const lines = md.split(/\r?\n/);
const html = [];
let inCode = false;
let codeBuf = [];

function esc(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
function inline(s) {
  // 处理 **bold** 与 *italic*
  return esc(s)
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*])\*([^*]+)\*(?!\*)/g, '$1<em>$2</em>');
}

function flushCode() {
  if (codeBuf.length) {
    html.push('<pre class="code">' + codeBuf.map(inline).join('\n') + '</pre>');
    codeBuf = [];
  }
}

for (const raw of lines) {
  const line = raw.trimEnd();

  if (/^```/.test(line)) {
    if (inCode) { flushCode(); inCode = false; }
    else { inCode = true; codeBuf = []; }
    continue;
  }
  if (inCode) { codeBuf.push(line); continue; }

  if (/^#\s+/.test(line)) { const t = line.replace(/^#\s+/, ''); html.push(`<h1>${inline(t)}</h1>`); continue; }
  if (/^##\s+/.test(line)) { const t = line.replace(/^##\s+/, ''); html.push(`<h2>${inline(t)}</h2>`); continue; }
  if (/^###\s+/.test(line)) { const t = line.replace(/^###\s+/, ''); html.push(`<h3>${inline(t)}</h3>`); continue; }
  if (/^-\s+/.test(line)) { html.push(`<li>${inline(line.replace(/^-\s+/, ''))}</li>`); continue; }
  if (/^\*\s+/.test(line)) { html.push(`<li>${inline(line.replace(/^\*\s+/, ''))}</li>`); continue; }
  if (/^\d+\.\s+/.test(line)) { html.push(`<li>${inline(line.replace(/^\d+\.\s+/, ''))}</li>`); continue; }
  if (/^---/.test(line)) { html.push('<hr>'); continue; }
  if (/^>/.test(line)) { html.push(`<blockquote>${inline(line.replace(/^>\s*/, ''))}</blockquote>`); continue; }
  if (line.trim() === '') { continue; }
  html.push(`<p>${inline(line)}</p>`);
}
flushCode();

const SOFT_NAME = 'ALH Pro';
const VER = 'V1.0';
const full = `<!DOCTYPE html><html lang="zh"><head><meta charset="utf-8">
<style>
  @page { size: A4; margin: 0; }
  * { box-sizing: border-box; }
  body { margin: 0; font-family: "Microsoft YaHei",sans-serif; color: #1a1a1a; }
  .page { width: 210mm; min-height: 297mm; padding: 14mm 16mm; page-break-after: always; position: relative; }
  .header { display: flex; justify-content: space-between; font-size: 12px; color: #333; border-bottom: 1px solid #999; padding-bottom: 2mm; margin-bottom: 4mm; }
  h1 { font-size: 20px; text-align: center; margin: 6mm 0 8mm; }
  h2 { font-size: 16px; margin: 6mm 0 3mm; border-left: 4px solid #2b6cb0; padding-left: 3mm; }
  h3 { font-size: 14px; margin: 4mm 0 2mm; }
  p, li { font-size: 12px; line-height: 1.7; margin: 1mm 0; }
  blockquote { font-size: 12px; color: #444; border-left: 3px solid #ccc; padding-left: 3mm; margin: 2mm 0; }
  hr { border: none; border-top: 1px solid #ccc; margin: 4mm 0; }
  pre.code { font-family: Consolas, monospace; font-size: 11px; line-height: 1.4; background: #f7f7f7; border: 1px solid #ddd; padding: 3mm; white-space: pre; overflow-x: hidden; }
  .footer-note { position: absolute; bottom: 6mm; left: 16mm; right: 16mm; text-align: center; font-size: 10px; color: #888; }
</style></head><body>
${html.join('\n')}
<div class="footer-note">${SOFT_NAME} ${VER} 软件功能说明书</div>
</body></html>`;

fs.writeFileSync(path.join(__dirname, 'ALHPro_软件功能说明书_v1.0.html'), full, 'utf8');
console.log('HTML 生成完成（已修正 bold/code 渲染）');
