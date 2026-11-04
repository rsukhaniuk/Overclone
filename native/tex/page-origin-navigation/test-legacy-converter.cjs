'use strict';
// Retain all accepted graphics unsafe-state controls except MediaBox, which is
// now covered by genuine origin-positive AND origin-negative native fixtures.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto'),Module=require('node:module');
const file=path.join(__dirname,'../graphics-navigation/test-converter-states.cjs');let text=fs.readFileSync(file,'utf8');assert.equal(crypto.createHash('sha256').update(text).digest('hex'),'b82a973696b363e16ec28125e636b5c6053827c4691a2c6c63156ada1417ff55');
const omit=" 'media-origin':'pdf:put @thispage << /MediaBox [-20 -30 592 762] >>',\n";assert.equal(text.split(omit).length,2);text=text.replace(omit,'').replace('all9 unsafe','all8 retained unsafe');
const child=new Module(file,module);child.filename=file;child.paths=Module._nodeModulePaths(path.dirname(file));child._compile(text,file);
