import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const source = fs.readFileSync(new URL('../Core/InjectedScripts.cs', import.meta.url), 'utf8');
const template = source.split('private const string Js = """')[1].split('""";')[0];
function page(enabled = true, whitelist = []) {
  const originalSheet = {};
  const listeners = {};
  const document = { adoptedStyleSheets:[originalSheet], readyState:'loading', addEventListener:(name,fn)=>listeners[name]=fn };
  const context = { document, location:{hostname:'site.test',href:'https://site.test/',origin:'https://site.test'}, navigator:{userActivation:{isActive:true}}, URL, Date,
    CSSStyleSheet:class { replaceSync() {} }, setTimeout:()=>0 };
  context.window = context;
  context.top = context;
  context.open = (...args)=>args;
  vm.createContext(context);
  vm.runInContext(template.replace('__CONFIG__',JSON.stringify({enabled,anti:false,whitelist})).replace('__BAIT__','""').replace('__CSS__','".ad{display:none}"'),context);
  return {context,listeners,originalSheet};
}
const {context:c,listeners:l,originalSheet:s} = page();
assert.equal(c.open('https://ads.test/'),null,'cross-origin click hijack blocked');
assert.equal(c.open('javascript:alert(1)'),null,'script URL rejected');
l.click({isTrusted:true,composedPath:()=>[{tagName:'A',href:'https://other.test/'}]});
assert.ok(c.open('https://other.test/'),'deliberate external link allowed');
assert.equal(c.open('https://other.test/'),null,'link intent consumed once');
assert.ok(c.open('/account'),'same-origin user workflow allowed');
c.navigator.userActivation.isActive=false;
assert.equal(c.open('/account'),null,'unsolicited same-origin popup blocked');
c.navigator.userActivation.isActive=true;
l.click({isTrusted:true,composedPath:()=>[{tagName:'BUTTON',getAttribute:()=>'button'}]});
assert.ok(c.open('https://accounts.google.com/oauth'),'button oauth popup allowed');
assert.equal(c.open('https://ads.test/popup'),null,'button generic ad popup blocked');
assert.ok(c.document.adoptedStyleSheets.includes(s),'site styles preserved');
c.__r1configure(false,false); c.__r1apply('');
assert.equal(c.document.adoptedStyleSheets.length,1,'only blocker sheets removed');
assert.equal(c.document.adoptedStyleSheets[0],s);
assert.ok(page(false).context.open('https://other.test/'),'disabled protection respected');
assert.ok(page(true,['site.test']).context.open('https://other.test/'),'site exception respected');
console.log('Passed 13 protection checks.');
