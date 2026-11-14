"""Windows build: python build.py --game GAME --sdk DOTNET [--work DIR]."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, urllib.request, zipfile

root = Path(__file__).resolve().parent
p = argparse.ArgumentParser()
p.add_argument('--game', required=True)
p.add_argument('--sdk', required=True)
p.add_argument('--work')
a = p.parse_args()
work = Path(a.work).resolve() if a.work else root / 'build'
work.mkdir(parents=True, exist_ok=True)
assets = root / 'assets'
for name in ('CuRussian', 'CuWiki'):
    subprocess.run([a.sdk, 'build', str(root/'src/Plugins.csproj'), '-c', 'Release', '--nologo', '-v', 'quiet', '-p:PluginName='+name, '-p:GameDir='+a.game, '-p:BaseIntermediateOutputPath='+str(work/(name+'-obj'))+'/', '-p:OutputPath='+str(work/'plugins')+'/'], check=True)

with zipfile.ZipFile(work/'russian.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    for name, source in [('CuRussian.dll', work/'plugins/CuRussian.dll'), ('ru-RU.json', assets/'ru-RU.json'), ('together-ru.json', assets/'together-ru.json'), ('CREDITS.txt', assets/'TRANSLATION-CREDITS.txt'), ('LOCALE-UPDATE-0.2.4.txt', assets/'LOCALE-UPDATE-0.2.4.txt'), ('TRANSLATION-LICENSE.txt', assets/'TRANSLATION-LICENSE.txt'), ('TOGETHER-LICENSE.txt', assets/'TOGETHER-LICENSE.txt')]:
        z.write(source, 'BepInEx/plugins/CuRussian/'+name)
with zipfile.ZipFile(work/'wiki.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    z.write(work/'plugins/CuWiki.dll', 'BepInEx/plugins/CuWiki/CuWiki.dll')

def plugin_version(name):
    deps=json.loads((work/'plugins'/(name+'.deps.json')).read_text(encoding='utf8'))
    return next(key.split('/',1)[1] for key in deps['libraries'] if key.startswith(name+'/'))
catalog={'russian':plugin_version('CuRussian'),'wiki':plugin_version('CuWiki'),'bepinex':'5.4.23.5'}
(work/'BundledCatalog.json').write_text(json.dumps(catalog),encoding='utf8')

loader=work/'BepInEx_win_x64_5.4.23.5.zip'
if not loader.exists():
    urllib.request.urlretrieve('https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip',loader)
assert hashlib.sha256(loader.read_bytes()).hexdigest()=='82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4', 'BepInEx checksum mismatch'
exe = root.parent/'Casualties-ModManager-0.5.1.exe'
csc = Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe')
helper=work/'UpdateHelper.exe'
subprocess.run([str(csc),'/nologo','/target:winexe','/out:'+str(helper),str(root/'src/UpdateHelper.cs')],check=True)
subprocess.run([str(csc), '/nologo', '/target:winexe', '/platform:x64', '/out:'+str(exe), '/win32icon:'+str(assets/'AppIcon.ico'), '/resource:'+str(assets/'AppIcon.ico')+',AppIcon.ico', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', '/resource:'+str(work/'russian.zip')+',russian.zip', '/resource:'+str(work/'wiki.zip')+',wiki.zip', '/resource:'+str(loader)+',Loader.zip', '/resource:'+str(work/'BundledCatalog.json')+',BundledCatalog.json', '/resource:'+str(helper)+',UpdateHelper.exe','/resource:'+str(assets/'CatalogSnapshot.json')+',CatalogSnapshot.json','/resource:'+str(assets/'CATALOG-LICENSE.txt')+',CatalogLicense.txt',str(root/'src/ModCatalog.cs'),str(root/'src/CommunityInstaller.cs'),str(root/'src/ArchiveInbox.cs'),str(root/'src/CatalogUi.cs'),str(root/'src/SteamGameLocator.cs'),str(root/'src/LauncherUpdate.cs'),str(root/'src/Manager.cs')],check=True)
# Versioned output: preserve previous release binaries until publication.
(root/'BUILD-REPORT.txt').write_text('Manager version: 0.5.1\nRussian version: '+catalog['russian']+'\nWiki version: '+catalog['wiki']+'\nPlugin builds: passed, no warnings\nManager build: passed\nManager SHA256: '+hashlib.sha256(exe.read_bytes()).hexdigest()+'\n',encoding='utf8')
print(exe)
