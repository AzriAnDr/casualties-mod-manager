"""Build one module ZIP without compiling or replacing the launcher."""
import argparse, hashlib, io, json, subprocess, zipfile
from pathlib import Path

root = Path(__file__).resolve().parent
p = argparse.ArgumentParser()
p.add_argument('--module', choices=['russian', 'wiki'], required=True)
p.add_argument('--game', required=True)
p.add_argument('--sdk', required=True)
p.add_argument('--work', required=True)
p.add_argument('--output')
a = p.parse_args()
work = Path(a.work).resolve()
work.mkdir(parents=True, exist_ok=True)
plugin = {'russian': 'CuRussian', 'wiki': 'CuWiki'}[a.module]
subprocess.run([a.sdk, 'build', str(root/'src/Plugins.csproj'), '-c', 'Release', '--nologo', '-v', 'quiet',
                '-p:PluginName='+plugin, '-p:GameDir='+a.game,
                '-p:BaseIntermediateOutputPath='+str(work/(plugin+'-obj'))+'/',
                '-p:OutputPath='+str(work/'plugins')+'/'], check=True)
deps = json.loads((work/'plugins'/(plugin+'.deps.json')).read_text(encoding='utf8'))
version = next(key.split('/', 1)[1] for key in deps['libraries'] if key.startswith(plugin+'/'))
files = [(plugin+'.dll', work/'plugins'/(plugin+'.dll'))]
if a.module == 'russian':
    files += [(name, root/'assets'/source) for name, source in [
        ('ru-RU.json', 'ru-RU.json'), ('together-ru.json', 'together-ru.json'),
        ('CREDITS.txt', 'TRANSLATION-CREDITS.txt'), ('LOCALE-UPDATE-0.2.4.txt', 'LOCALE-UPDATE-0.2.4.txt'),
        ('TRANSLATION-LICENSE.txt', 'TRANSLATION-LICENSE.txt'), ('TOGETHER-LICENSE.txt', 'TOGETHER-LICENSE.txt')]]
memory = io.BytesIO()
with zipfile.ZipFile(memory, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for leaf, source in files:
        entry = zipfile.ZipInfo('BepInEx/plugins/'+plugin+'/'+leaf, date_time=(1980, 1, 1, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(entry, source.read_bytes())
data = memory.getvalue()
output = Path(a.output).resolve() if a.output else root/'packages'/({'russian':'Russian','wiki':'Wiki'}[a.module]+'-'+version+'.zip')
output.parent.mkdir(parents=True, exist_ok=True)
if output.exists() and output.read_bytes() != data:
    raise ValueError('This local package version already exists with other bytes; increment the module version.')
output.write_bytes(data)
report = {'module': a.module, 'version': version, 'archive': str(output), 'sha256': hashlib.sha256(data).hexdigest(), 'launcherBuilt': False}
(work/'module-build.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf8')
print(json.dumps(report, ensure_ascii=False))
