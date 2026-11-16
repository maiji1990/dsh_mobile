#!/usr/bin/env node
// Auditable upstream upgrade proposer. Never runs upstream install scripts.
// Usage: node tooling/update-upstream.mjs --component runtime|dsh|assets [--check]
// CI runs with one component per PR; publishing always consumes exact locks.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, appendFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)));
const p = (...parts) => join(ROOT, ...parts);
const lockPath = p('runtime-bundle', 'runtime-build.lock');
const pkgPath = p('runtime-bundle', 'dsh', 'package.json');
const pkgLockPath = p('runtime-bundle', 'dsh', 'package-lock.json');
const policy = JSON.parse(readFileSync(p('runtime-bundle', 'upstream-policy.json'), 'utf8'));

export function parseVersions(a, b) {
  const f = (s) => {
    const m = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$/.exec(s);
    if (!m) throw new Error('invalid version: ' + s);
    return [Number(m[1]), Number(m[2]), Number(m[3]), m[4] ?? null];
  };
  const x = f(a), y = f(b);
  for (let i=0; i<3; i++) if (x[i] !== y[i]) return Math.sign(x[i]-y[i]);
  if (x[3] === null || y[3] === null) return x[3] === y[3] ? 0 : x[3] === null ? 1 : -1;
  const ax=x[3].split('.'), ay=y[3].split('.');
  for(let i=0;i<Math.max(ax.length,ay.length);i++){
    if(ax[i] === undefined || ay[i] === undefined) return ax[i] === undefined ? -1 : 1;
    const n=/^\d+$/.test(ax[i]), m=/^\d+$/.test(ay[i]);
    if(n && m && Number(ax[i]) !== Number(ay[i])) return Math.sign(Number(ax[i])-Number(ay[i]));
    if(n!==m) return n ? -1 : 1;
    if(ax[i]!==ay[i]) return ax[i]<ay[i] ? -1 : 1;
  }
  return 0;
}

export function updateKey(text, key, value) {
  const re = new RegExp('^' + key + '=.*$', 'm');
  if (!re.test(text)) throw new Error('missing lock key ' + key);
  return text.replace(re, key + '=' + value);
}
export function nextBundleVersion(current) {
  const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(current);
  if(!m) throw new Error('unsupported bundle version ' + current);
  return m[1]+'.'+m[2]+'.'+(Number(m[3])+1);
}
export const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
export function selectDshTag(tags, prefix, current) {
  return tags.filter(t=>t.startsWith(prefix))
    .map(t=>t.slice(prefix.length)).filter(t=>/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(t))
    .filter(v=>parseVersions(v,current)>0).sort((a,b)=>parseVersions(b,a));
}

async function get(url) {
  const r=await fetch(url,{headers:{'Accept':'application/vnd.github+json','User-Agent':'dshmobile-upstream-watch','X-GitHub-Api-Version':'2022-11-28'}});
  if(!r.ok) throw new Error('upstream GET '+r.status+' '+url);
  return r;
}
function lock() {return readFileSync(lockPath,'utf8');}
function locked(t,k) {const m=new RegExp('^'+k+'=(.*)$','m').exec(t);if(!m)throw Error('missing '+k);return m[1];}
function put(path,content,check) {if(!check) writeFileSync(path,content);}
function summary(s) {process.stdout.write(s+'\n');if(process.env.GITHUB_STEP_SUMMARY)appendFileSync(process.env.GITHUB_STEP_SUMMARY,s+'\n');}
function command(cmd,args,opts={}) {return execFileSync(cmd,args,{encoding:'utf8',stdio:['ignore','pipe','inherit'],...opts}).trim();}
function appendUpstream(record,check){
  const path=p('docs','UPSTREAMS.md');
  const original=readFileSync(path,'utf8');
  put(path,original+'\n'+record+'\n',check);
}
async function runtime(check) {
  const text=lock(), old=locked(text,'NODE_VERSION');
  const major=policy.runtime.nodeMajor;
  if(!Number.isInteger(major)||major<20) throw Error('invalid runtime.nodeMajor');
  const manifest=await (await get('https://nodejs.org/dist/index.json')).json();
  const all=manifest.map(x=>x.version.slice(1)).filter(x=>x.startsWith(major+'.')).sort((a,b)=>parseVersions(b,a));
  if(!all.length)throw Error('no official Node major '+major+' releases');
  const version=all[0];
  if(parseVersions(version,old)<=0){summary('Runtime: pinned '+old+' is current or ahead of configured Node '+major);return;}
  const sums=await (await get('https://nodejs.org/dist/v'+version+'/SHASUMS256.txt')).text();
  const line=sums.split(/\r?\n/).find(x=>x.trim().endsWith('  node-v'+version+'-linux-arm64.tar.xz'));
  if(!line||!/^[a-f0-9]{64}  /.test(line))throw Error('official arm64 SHA256 not found');
  const newHash=line.slice(0,64);
  let out=updateKey(text,'NODE_VERSION',version);
  out=updateKey(out,'NODE_SHA256',newHash);
  out=updateKey(out,'BUNDLE_VERSION',nextBundleVersion(locked(text,'BUNDLE_VERSION')));
  out=out.replace(/^# Node layer:.*$/m,'# Node layer: official build, SHA-256 from https://nodejs.org/dist/v'+version+'/SHASUMS256.txt');
  put(lockPath,out,check);
  appendUpstream('<!-- Upstream review '+new Date().toISOString().slice(0,10)+': Node '+old+' → '+version+'; verify Debian/PRoot, native modules, DSH profiles and Android device before stable. Source: https://nodejs.org/dist/v'+version+'/SHASUMS256.txt -->',check);
  summary('Runtime upgrade: Node '+old+' => '+version+'; bundle '+locked(text,'BUNDLE_VERSION')+' => '+locked(out,'BUNDLE_VERSION')+'; SHA256='+newHash);
}

async function dsh(check) {
  const text=lock(), old=locked(text,'DSH_VERSION'), d=policy.dsh;
  const repo=d.sourceRepo, pref=d.tagPrefix;
  // Use official upstream tags (not npm latest: that can lag pre-releases).
  const names=[];
  for(let page=1;page<=5;page++){
    const tags=await (await get('https://api.github.com/repos/'+repo+'/tags?per_page=100&page='+page)).json();
    if(!Array.isArray(tags))throw Error('invalid upstream tags');
    names.push(...tags.map(x=>x.name));
    if(tags.length<100)break;
  }
  const candidates=selectDshTag(names,pref,old);
  let version;
  for(const v of candidates){
    try {const got=command('npm',['view',d.npmPackage+'@'+v,'version','--json','--registry=https://registry.npmjs.org/']);if(got.includes(v)){version=v;break;}}
    catch { /* tag may not yet be published to npm */ }
  }
  if(!version){summary('DSH: no newer tag with a published npm package than '+old);return;}
  // npm install --package-lock-only resolves integrity without running any external lifecycle scripts.
  const pkg={name:'dsh-layer',version,dependencies:{[d.npmPackage]:version}};
  put(pkgPath,JSON.stringify(pkg)+'\n',check);
  if(check){summary('DSH candidate '+version+' (check-only; no lock generated)');return;}
  command('npm',['install','--package-lock-only','--ignore-scripts','--no-audit','--no-fund','--save-exact','--os=linux','--cpu=arm64','--libc=glibc','--registry=https://registry.npmjs.org/'],{cwd:p('runtime-bundle','dsh')});
  const actual=JSON.parse(readFileSync(pkgLockPath,'utf8'));
  if(actual.packages?.['']?.dependencies?.[d.npmPackage]!==version ||
     actual.packages?.['node_modules/'+d.npmPackage]?.version!==version)throw Error('npm lock did not pin '+version);
  let out=updateKey(text,'DSH_VERSION',version);
  out=updateKey(out,'DSH_PACKAGE_JSON_SHA256',sha(readFileSync(pkgPath)));
  out=updateKey(out,'DSH_PACKAGE_LOCK_SHA256',sha(readFileSync(pkgLockPath)));
  put(lockPath,out,false);
  const refs=command('git',['ls-remote','https://github.com/'+repo+'.git','refs/tags/'+pref+version,'refs/tags/'+pref+version+'^{}']);
  const items=refs.split('\n');
  const peeled=items.find(x=>x.includes('^{}')) ?? items[0];
  const commit=peeled?.split(/\s+/)[0];
  if(!/^[0-9a-f]{40}$/.test(commit??''))throw Error('cannot verify upstream tag commit');
  appendUpstream('<!-- Upstream review '+new Date().toISOString().slice(0,10)+': DSH '+old+' → '+version+'; tag '+pref+version+' commit '+commit+'; npm-lock SHA256 '+sha(readFileSync(pkgLockPath))+'; test profiles, MCP/CLI, plugins and device before stable. -->',false);
  summary('DSH upgrade '+old+' => '+version+'; upstream commit '+commit);
}

async function assets(check) {
  let lockText = "# Pinned assets from this repository's immutable versioned Releases. Never use floating latest.\n# Format: file SHA256 url\n";
  const t=lock(), parts=[
    {kind:'dsh',version:locked(t,'DSH_VERSION'),file:locked(t,'DSH_VERSION')+'.tar.zst'},
    {kind:'runtime',version:locked(t,'BUNDLE_VERSION'),file:locked(t,'BUNDLE_NAME')+'-'+locked(t,'BUNDLE_VERSION')+'.zip'}
  ];
  let provenance;
  for(const part of parts){
    const tag=part.kind+'-'+part.version;
    const release=await (await get('https://api.github.com/repos/maiji1990/dsh_mobile/releases/tags/'+tag)).json();
    const a=release.assets?.find(a=>a.name===part.file);
    if(!a?.digest?.startsWith('sha256:') || !/^[0-9a-f]{64}$/.test(a.digest.slice(7)))
      throw Error('Release '+tag+' is absent or lacks a SHA256 digest for '+part.file);
    lockText+=part.file+' '+a.digest.slice(7)+' '+a.browser_download_url+'\n';
    if(part.kind==='dsh'){
      const prov=release.assets?.find(x=>x.name==='provenance.json');
      if(!prov?.browser_download_url)throw Error('missing DSH source provenance for '+tag);
      provenance=await (await get(prov.browser_download_url)).json();
      if(provenance.version!==part.version || !/^[0-9a-f]{40,64}$/.test(provenance.sourceCommit??''))
        throw Error('invalid DSH provenance for '+tag);
    }
  }
  const current=readFileSync(p('runtime-bundle','runtime-assets.lock'),'utf8');
  if(current!==lockText)put(p('runtime-bundle','runtime-assets.lock'),lockText,check);
  const appProps=readFileSync(p('src','DshMobile.Android','PackageIdentity.props'),'utf8');
  const appVersion=/<ApplicationDisplayVersion>([^<]+)<\/ApplicationDisplayVersion>/.exec(appProps)?.[1];
  if(!/^\d+\.\d+\.\d+$/.test(appVersion??''))throw Error('app version cannot be read from PackageIdentity.props');
  const spec={
    repository:'maiji1990/dsh_mobile',
    artifacts:[
      {kind:'runtime',version:parts[1].version,abi:'arm64',file:parts[1].file,
        releaseTag:'runtime-'+parts[1].version,minAppVersion:appVersion},
      {kind:'dsh',version:parts[0].version,abi:'arm64',file:parts[0].file,
        releaseTag:'dsh-'+parts[0].version,requiresNode:locked(t,'NODE_VERSION').split('.')[0]+'.x',
        sourceRepo:provenance.sourceRepo,sourceTag:provenance.sourceTag,sourceCommit:provenance.sourceCommit}
    ]
  };
  if(spec.artifacts[1].sourceRepo!=='deepseek-ai/deepseek-harness' ||
     spec.artifacts[1].sourceTag!=='dsh-v'+parts[0].version)
    throw Error('unexpected provenance source');
  const previewPath=p('release','preview.json'), next=JSON.stringify(spec,null,2)+'\n';
  let oldSpec='';try{oldSpec=readFileSync(previewPath,'utf8')}catch(e){if(e.code!=='ENOENT')throw e;}
  if(next!==oldSpec)put(previewPath,next,check);
  summary('Assets: reconciled release digests and reviewed preview spec for '+parts.map(x=>x.kind+'-'+x.version).join(', '));
}

const args=process.argv.slice(2);
if(args.includes('--component')){
  const component=args[args.indexOf('--component')+1], check=args.includes('--check');
  try {
    if(component==='runtime')await runtime(check);
    else if(component==='dsh')await dsh(check);
    else if(component==='assets')await assets(check);
    else throw Error('component must be runtime|dsh|assets');
  }catch(e){console.error(e);process.exitCode=1;}
}
