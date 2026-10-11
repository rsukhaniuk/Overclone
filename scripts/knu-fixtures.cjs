// Synthetic test-only inputs shared by browser UI and published offline tests.
const { createHash } = require('node:crypto');
const negativeText = 'Привіт🙂\r\n\\setlength{\\parindent}{ 10 mm }\r\\section{Методи.}\n';
const negative = Buffer.concat([Buffer.from([239,187,191]), Buffer.from(negativeText)]);
const positive = Buffer.from('\\setlength{\\parindent}{1.27cm}\n\\section{Методи🙂}\n');
const uncertain = Buffer.from('\\input{missing}\n\\setlength{\\parindent}{1cm}\n\\section{Методи.}\n');
const unavailable = Buffer.from('Лише текст🙂\n');
const mixed = Buffer.concat([negative, Buffer.from('{')]);
const escape = Buffer.from('\\setlength{\\parindent}{1cm}\n\\section{<img src=x onerror=window.__knuXss=1>}\n');
const fixtures = {negative, positive, uncertain, unavailable, mixed, escape};
const hashes = Object.fromEntries(Object.entries(fixtures).map(([k,v])=>[k,createHash('sha256').update(v).digest('hex')]));
module.exports = { fixtures, hashes };
