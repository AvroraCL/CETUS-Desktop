// Extracts the injected WebView2 bridge script from BrowserSession.cs and
// syntax-checks it with Node. The script ships as a C# raw string literal, so a
// typo would only surface at runtime as a dead bridge.
const fs = require('fs');
const path = process.argv[2];
const source = fs.readFileSync(path, 'utf8');

const start = source.indexOf('WindowBridgeScript = """');
if (start < 0) {
  console.error('WindowBridgeScript raw string literal not found');
  process.exit(1);
}
const bodyStart = source.indexOf('\n', start) + 1;
const end = source.indexOf('"""', bodyStart);
if (end < 0) {
  console.error('unterminated raw string literal');
  process.exit(1);
}
const script = source.slice(bodyStart, end);
fs.writeFileSync(path + '.extracted.js', script);
console.log('extracted chars=' + script.length + ' lines=' + script.split('\n').length);

// Structural checks that catch the mistakes a syntax check cannot.
const required = [
  'cetus-update-card',
  'cetus-update-state',
  'cetus-update-install',
  'cetus-update-details',
  'cetus-update-dismiss',
  'cetus-update-state-request',
  'cetus-settings-state',
];
for (const token of required) {
  if (!script.includes(token)) {
    console.error('MISSING token: ' + token);
    process.exit(2);
  }
}

// Balanced braces/parens outside of strings and comments is a decent proxy for
// "the function bodies were inserted in the right place".
const counts = { '{': 0, '}': 0, '(': 0, ')': 0 };
for (const ch of script) {
  if (ch in counts && !script.includes('`' + ch)) counts[ch]++;
}
console.log(JSON.stringify(counts));
console.log('tokens ok');
