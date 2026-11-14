"""Publish versioned module ZIPs plus a mutable manifest, independently of launcher releases."""
import argparse, hashlib, json, re, subprocess, tempfile
from pathlib import Path

p=argparse.ArgumentParser()
p.add_argument('--repo',default='AzriAnDr/casualties-mod-manager')
for name in ('russian','wiki'):
    p.add_argument('--'+name,required=True)
    p.add_argument('--'+name+'-version',required=True)
p.add_argument('--game-version',required=True)
p.add_argument('--bepinex-version',default='5.4.23.5')
p.add_argument('--bepinex-url',default='https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip')
p.add_argument('--bepinex-sha256',default='82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4')
a=p.parse_args()
if a.repo!='AzriAnDr/casualties-mod-manager':raise ValueError('This module client trusts the official repository only')
for version in (a.russian_version,a.wiki_version,a.game_version):
    if not re.fullmatch(r'\d+(?:\.\d+){1,3}',version):raise ValueError('Numeric version required')
if not re.fullmatch(r'5(?:\.\d+){1,3}',a.bepinex_version):raise ValueError('Only BepInEx5 is supported')
if not re.fullmatch(r'[a-fA-F0-9]{64}',a.bepinex_sha256):raise ValueError('Full BepInEx SHA256 required')
if not a.bepinex_url.startswith('https://github.com/BepInEx/BepInEx/releases/download/v'+a.bepinex_version+'/') or not a.bepinex_url.endswith('.zip') or '?' in a.bepinex_url or '#' in a.bepinex_url:raise ValueError('Official BepInEx release ZIP required')
packages=[]
for identity,path,version,label in [('russian',a.russian,a.russian_version,'Russian'),('wiki',a.wiki,a.wiki_version,'Wiki')]:
    f=Path(path).resolve()
    if not f.is_file() or f.stat().st_size>128*1024*1024:raise ValueError('Expected a module ZIP under128MiB')
    data=f.read_bytes()
    if data[:4]!=b'PK\x03\x04':raise ValueError('Expected ZIP')
    name=label+'-'+version+'.zip'
    packages.append((f,name,{'id':identity,'version':version,'url':'https://github.com/'+a.repo+'/releases/download/modules-stable/'+name,'sha256':hashlib.sha256(data).hexdigest(),'gameVersions':[a.game_version]}))
view=subprocess.run(['gh','release','view','modules-stable','--repo',a.repo,'--json','assets'],capture_output=True,text=True)
if view.returncode:
    with tempfile.TemporaryDirectory(prefix='cu-modules-') as temporary:
        notes=Path(temporary)/'notes.txt'
        notes.write_text('Независимый канал пакетов русификатора, справочника и BepInEx. Обновляется без замены лаунчера. Автоустановки нет: пользователь проверяет файлы. Это служебный prerelease, не новый релиз лаунчера.',encoding='utf8')
        subprocess.run(['gh','release','create','modules-stable','--repo',a.repo,'--title','Пакеты модулей · независимое обновление','--prerelease','--target','main','--notes-file',str(notes)],check=True)
    existing={}
else:existing={item['name']:item for item in json.loads(view.stdout)['assets']}
with tempfile.TemporaryDirectory(prefix='cu-modules-') as temporary:
    directory=Path(temporary)
    for source,name,item in packages:
        if name in existing:
            if existing[name].get('digest')!='sha256:'+item['sha256']:raise ValueError('Published module version is immutable; increment version: '+name)
            continue
        target=directory/name
        target.write_bytes(source.read_bytes())
        subprocess.run(['gh','release','upload','modules-stable',str(target),'--repo',a.repo],check=True)
    loader={'id':'bepinex','version':a.bepinex_version,'url':a.bepinex_url,'sha256':a.bepinex_sha256.lower(),'gameVersions':[]}
    manifest={'schema':1,'modules':[item for _,_,item in packages]+[loader]}
    feed=directory/'modules.json'
    feed.write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
    subprocess.run(['gh','release','upload','modules-stable',str(feed),'--repo',a.repo,'--clobber'],check=True)
print('Published module feed; launcher release was not created or changed.')
