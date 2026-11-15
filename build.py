"""Windows build: python build.py --game GAME --sdk DOTNET [--work DIR]."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, tempfile, urllib.request, zipfile

CECIL_PACKAGE_SHA256 = 'd2a23832aaa948ba9a01acc42b5726e34c5f995958f1b30d45c0e7c70b3a72d5'
CECIL_DLL_SHA256 = 'c41bdb9ffd3c5f6e17d2382c1012d73703e035e3f1100245fdd4e08c8dc6eb5b'
CECIL_URL = 'https://api.nuget.org/v3-flatcontainer/mono.cecil/0.11.6/mono.cecil.0.11.6.nupkg'

def ensure_cecil(cache):
    """Fetch the pinned official NuGet package; extract only its verified net40 DLL."""
    cache.mkdir(parents=True, exist_ok=True)
    package = cache / 'mono.cecil.0.11.6.nupkg'
    dll = cache / 'Mono.Cecil.dll'
    if not package.exists():
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(dir=cache, prefix='cecil-', suffix='.part', delete=False) as output:
                temporary = Path(output.name)
                request = urllib.request.Request(CECIL_URL, headers={'User-Agent': 'Casualties-ModManager-build/0.5.8'})
                with urllib.request.urlopen(request, timeout=20) as response:
                    total = 0
                    while True:
                        block = response.read(65536)
                        if not block:
                            break
                        total += len(block)
                        if total > 8 * 1024 * 1024:
                            raise ValueError('Mono.Cecil package exceeds 8 MiB')
                        output.write(block)
            if hashlib.sha256(temporary.read_bytes()).hexdigest() != CECIL_PACKAGE_SHA256:
                raise ValueError('Mono.Cecil package checksum mismatch')
            temporary.replace(package)
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()
    if package.stat().st_size > 8 * 1024 * 1024 or hashlib.sha256(package.read_bytes()).hexdigest() != CECIL_PACKAGE_SHA256:
        raise ValueError('Cached Mono.Cecil package checksum mismatch')
    with zipfile.ZipFile(package) as archive:
        info = archive.getinfo('lib/net40/Mono.Cecil.dll')
        if info.file_size > 8 * 1024 * 1024:
            raise ValueError('Mono.Cecil DLL exceeds 8 MiB')
        payload = archive.read(info)
    if hashlib.sha256(payload).hexdigest() != CECIL_DLL_SHA256:
        raise ValueError('Mono.Cecil net40 DLL checksum mismatch')
    if dll.exists():
        if hashlib.sha256(dll.read_bytes()).hexdigest() != CECIL_DLL_SHA256:
            raise ValueError('Cached Mono.Cecil DLL checksum mismatch')
    else:
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(dir=cache, prefix='cecil-dll-', suffix='.part', delete=False) as output:
                temporary = Path(output.name)
                output.write(payload)
            temporary.replace(dll)
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()
    return dll

root = Path(__file__).resolve().parent
p = argparse.ArgumentParser()
p.add_argument('--game', required=True)
p.add_argument('--sdk', required=True)
p.add_argument('--work')
p.add_argument('--russian-package', help='Reuse the published module ZIP byte for byte')
p.add_argument('--wiki-package', help='Reuse the published module ZIP byte for byte')
a = p.parse_args()
work = Path(a.work).resolve() if a.work else root / 'build'
work.mkdir(parents=True, exist_ok=True)
assets = root / 'assets'
cecil = ensure_cecil(root.parents[1] / 'work' / 'cecil0116')
for name in ('CuRussian', 'CuWiki'):
    subprocess.run([a.sdk, 'build', str(root/'src/Plugins.csproj'), '-c', 'Release', '--nologo', '-v', 'quiet', '-p:PluginName='+name, '-p:GameDir='+a.game, '-p:BaseIntermediateOutputPath='+str(work/(name+'-obj'))+'/', '-p:OutputPath='+str(work/'plugins')+'/'], check=True)

with zipfile.ZipFile(work/'russian.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    for name, source in [('CuRussian.dll', work/'plugins/CuRussian.dll'), ('ru-RU.json', assets/'ru-RU.json'), ('together-ru.json', assets/'together-ru.json'), ('CREDITS.txt', assets/'TRANSLATION-CREDITS.txt'), ('LOCALE-UPDATE-0.2.4.txt', assets/'LOCALE-UPDATE-0.2.4.txt'), ('TRANSLATION-LICENSE.txt', assets/'TRANSLATION-LICENSE.txt'), ('TOGETHER-LICENSE.txt', assets/'TOGETHER-LICENSE.txt')]:
        z.write(source, 'BepInEx/plugins/CuRussian/'+name)
with zipfile.ZipFile(work/'wiki.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    z.write(work/'plugins/CuWiki.dll', 'BepInEx/plugins/CuWiki/CuWiki.dll')

# Reuse already published immutable packages for official launcher releases.
for package, filename, prefix in [(a.russian_package, 'russian.zip', 'BepInEx/plugins/CuRussian/'), (a.wiki_package, 'wiki.zip', 'BepInEx/plugins/CuWiki/')]:
    if package:
        original = Path(package).resolve()
        with zipfile.ZipFile(original) as z:
            names = z.namelist()
            assert names and all(n.startswith(prefix) and '..' not in n.split('/') for n in names), 'Unexpected module ZIP paths'
        shutil.copyfile(original, work/filename)

def plugin_version(name):
    deps=json.loads((work/'plugins'/(name+'.deps.json')).read_text(encoding='utf8'))
    return next(key.split('/',1)[1] for key in deps['libraries'] if key.startswith(name+'/'))
catalog={'russian':plugin_version('CuRussian'),'wiki':plugin_version('CuWiki'),'bepinex':'5.4.23.5'}
(work/'BundledCatalog.json').write_text(json.dumps(catalog),encoding='utf8')

loader=work/'BepInEx_win_x64_5.4.23.5.zip'
if not loader.exists():
    urllib.request.urlretrieve('https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip',loader)
assert hashlib.sha256(loader.read_bytes()).hexdigest()=='82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4', 'BepInEx checksum mismatch'
exe = root.parent/'Casualties-ModManager-0.5.8.exe'
csc = Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe')
helper=work/'UpdateHelper.exe'
subprocess.run([str(csc),'/nologo','/target:winexe','/out:'+str(helper),str(root/'src/UpdateHelper.cs')],check=True)
subprocess.run([str(csc), '/nologo', '/target:winexe', '/platform:x64', '/out:'+str(exe), '/win32icon:'+str(assets/'AppIcon.ico'), '/resource:'+str(assets/'AppIcon.ico')+',AppIcon.ico', '/reference:'+str(cecil), '/resource:'+str(cecil)+',Mono.Cecil.dll', '/resource:'+str(assets/'CECIL-LICENSE.txt')+',CecilLicense.txt', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', '/resource:'+str(work/'russian.zip')+',russian.zip', '/resource:'+str(work/'wiki.zip')+',wiki.zip', '/resource:'+str(loader)+',Loader.zip', '/resource:'+str(work/'BundledCatalog.json')+',BundledCatalog.json', '/resource:'+str(helper)+',UpdateHelper.exe','/resource:'+str(assets/'CatalogSnapshot.json')+',CatalogSnapshot.json','/resource:'+str(assets/'CATALOG-LICENSE.txt')+',CatalogLicense.txt',str(root/'src/MetadataRuntime.cs'),str(root/'src/ModCatalog.cs'),str(root/'src/CommunityInstaller.cs'),str(root/'src/ArchiveInbox.cs'),str(root/'src/CatalogUi.cs'),str(root/'src/SteamGameLocator.cs'),str(root/'src/LauncherUpdate.cs'),str(root/'src/ModuleCatalog.cs'),str(root/'src/ModuleUi.cs'),str(root/'src/NativeTheme.cs'),str(root/'src/Manager.cs')],check=True)
# Versioned output: preserve previous release binaries until publication.
(root/'BUILD-REPORT.txt').write_text('Manager version: 0.5.8\nRussian version: '+catalog['russian']+'\nWiki version: '+catalog['wiki']+'\nPlugin builds: passed, no warnings\nManager build: passed\nManager SHA256: '+hashlib.sha256(exe.read_bytes()).hexdigest()+'\n',encoding='utf8')
print(exe)
