// 从 v1.0 源码生成软著「源程序鉴别材料」：50 行/页，严格 60 页（前30 + 后30）
// 省略说明合并进第30页末尾，不单独占页 → 总页数恰好 60
const fs = require('fs');
const path = require('path');

const UI = path.join(__dirname, 'ImgUpscalerUI');
const LINES_PER_PAGE = 50;
const FRONT_PAGES = 30;
const BACK_PAGES = 30;
const TOTAL_PAGES = FRONT_PAGES + BACK_PAGES; // 60

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (e.name !== 'bin' && e.name !== 'obj') walk(p, out); }
    else if (e.name.endsWith('.cs')) out.push(p);
  }
  return out;
}

const csFiles = walk(UI);
function rank(f) {
  const rel = f.substring(UI.length + 1).replace(/\\/g, '/');
  if (rel === 'App.xaml.cs') return 0;
  if (rel.startsWith('Views/')) return 1;
  return 2;
}
csFiles.sort((a, b) => rank(a) - rank(b) || a.toLowerCase().localeCompare(b.toLowerCase()));

// 拼接全部源码行（每文件前加注释头）
const allLines = [];
for (const f of csFiles) {
  const rel = f.substring(UI.length + 1).replace(/\\/g, '/');
  const body = fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, '').split(/\r?\n/);
  allLines.push(`// ========== ${rel} ==========`);
  for (const ln of body) allLines.push(ln);
  allLines.push('');
}

const totalLines = allLines.length;
const totalPages = Math.ceil(totalLines / LINES_PER_PAGE);
const omittedPagesCount = Math.max(0, totalPages - TOTAL_PAGES);
console.log('源码总行数:', totalLines, ' 总页数(50行/页):', totalPages, ' 省略约:', omittedPagesCount, '页');

function pageContent(pageIdx) {
  const slice = allLines.slice(pageIdx * LINES_PER_PAGE, (pageIdx + 1) * LINES_PER_PAGE);
  for (let i = slice.length; i < LINES_PER_PAGE; i++) slice.push('');
  return slice;
}

const SOFT_NAME = 'ALH Pro';
const VER = 'V1.0';
function esc(s){return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}

function pageHTML(pageNo, lines, isLastFront) {
  let body = lines.map(esc);
  if (isLastFront) {
    const omitLines = [
      '',
      `// ---- 本材料依《计算机软件著作权登记办法》提交源程序前、后各连续30页（共60页）。 ----`,
      `// ---- 中间源码约 ${omittedPagesCount} 页（约 ${totalLines - LINES_PER_PAGE*TOTAL_PAGES} 行）因篇幅省略。 ----`,
      `// ---- 以下续接源程序最后 ${BACK_PAGES} 页。 ----`,
      ''
    ];
    body = body.slice(0, LINES_PER_PAGE - omitLines.length);
    body = body.concat(omitLines);
  }
  return `<div class="page">
  <div class="header"><span>${SOFT_NAME} ${VER}</span><span class="pn">第 ${pageNo} 页 / 共 ${TOTAL_PAGES} 页</span></div>
  <pre>${body.join('\n')}</pre>
</div>`;
}

const pages = [];
for (let i = 0; i < FRONT_PAGES; i++) {
  const pn = i + 1;
  pages.push(pageHTML(pn, pageContent(i), pn === FRONT_PAGES));
}
for (let i = 0; i < BACK_PAGES; i++) {
  const pageIdx = totalPages - BACK_PAGES + i;
  pages.push(pageHTML(FRONT_PAGES + 1 + i, pageContent(pageIdx), false));
}

const html = `<!DOCTYPE html><html lang="zh"><head><meta charset="utf-8">
<style>
  @page { size: A4; margin: 0; }
  * { box-sizing: border-box; }
  body { margin: 0; font-family: "Consolas","Microsoft YaHei",monospace; }
  .page { width: 210mm; height: 297mm; padding: 12mm 14mm; page-break-after: always; overflow: hidden; position: relative; }
  .header { display: flex; justify-content: space-between; font-size: 11px; color: #333; border-bottom: 1px solid #999; padding-bottom: 2mm; margin-bottom: 3mm; font-family: "Microsoft YaHei",sans-serif; }
  .pn { color: #555; }
  pre { margin: 0; font-family: "Consolas","Microsoft YaHei",monospace; font-size: 7.2px; line-height: 1.32; white-space: pre-wrap; word-break: break-all; }
</style></head><body>${pages.join('\n')}</body></html>`;

const htmlPath = path.join(__dirname, 'ALHPro_source_v1.0.html');
fs.writeFileSync(htmlPath, html, 'utf8');
console.log('HTML 生成:', htmlPath);
console.log('策略: 前', FRONT_PAGES, '页 + 后', BACK_PAGES, '页 = 严格', TOTAL_PAGES, '页, 省略说明并入第', FRONT_PAGES, '页末尾');
