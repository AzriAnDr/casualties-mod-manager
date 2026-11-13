"""Create a draft GitHub release; --publish additionally makes it public."""
import argparse,hashlib,json,shutil,subprocess,tempfile
from pathlib import Path

p=argparse.ArgumentParser()
p.add_argument('--version',required=True)
p.add_argument('--repo',default='AzriAnDr/casualties-mod-manager')
p.add_argument('--exe',required=True)
p.add_argument('--notes',required=True)
p.add_argument('--asset',action='append',default=[])
p.add_argument('--publish',action='store_true')
a=p.parse_args()
if not all(part.isdigit() for part in a.version.split('.')):raise ValueError('Numeric release version required')
if len(a.repo.split('/'))!=2:raise ValueError('Expected owner/repository')
exe=Path(a.exe).resolve()
if exe.read_bytes()[:2]!=b'MZ':raise ValueError('Expected Windows EXE')
tag='v'+a.version
with tempfile.TemporaryDirectory(prefix='casualties-release-') as temp:
    stage=Path(temp)
    binary=stage/'Casualties-ModManager.exe'
    shutil.copy2(exe,binary)
    feed={'version':a.version,'url':'https://github.com/'+a.repo+'/releases/download/'+tag+'/Casualties-ModManager.exe','sha256':hashlib.sha256(binary.read_bytes()).hexdigest(),'notes':Path(a.notes).read_text(encoding='utf-8-sig')}
    (stage/'update.json').write_text(json.dumps(feed,ensure_ascii=False,indent=2),encoding='utf8')
    subprocess.run(['gh','release','create',tag,'--repo',a.repo,'--title','Casualties Mod Manager '+a.version,'--draft','--target','main','--notes-file',str(Path(a.notes).resolve())],check=True)
    subprocess.run(['gh','release','upload',tag,str(binary),str(stage/'update.json'),*map(str,map(Path,a.asset)),'--repo',a.repo],check=True)
    if a.publish:subprocess.run(['gh','release','edit',tag,'--repo',a.repo,'--draft=false'],check=True)
