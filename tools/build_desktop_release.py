"""Build a clean, verified desktop ZIP without copying user data or credentials."""
import argparse
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import stat
import shutil
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit
import zipfile

ROOT_FILES = {'YoMi_Desktop_Pet.exe', 'LICENSE.electron.txt', 'LICENSES.chromium.html',
    'chrome_100_percent.pak', 'chrome_200_percent.pak', 'resources.pak', 'icudtl.dat',
    'snapshot_blob.bin', 'v8_context_snapshot.bin', 'vk_swiftshader_icd.json',
    'd3dcompiler_47.dll', 'dxcompiler.dll', 'dxil.dll', 'ffmpeg.dll', 'libEGL.dll',
    'libGLESv2.dll', 'vk_swiftshader.dll', 'vulkan-1.dll'}
TREES = ['locales', 'resources/app/assets', 'resources/app/electron',
    'resources/app/renderer', 'resources/app/server', 'resources/app/node_modules',
    'resources/app/vendor/google-auth']
REQUIRED = sorted(ROOT_FILES) + ['resources/bin/yt-dlp.exe', 'resources/bin/YoMiUpdater.exe', 'resources/bin/YoMiUpdater.exe.config', 'resources/app/package.json',
    'resources/app/electron/main.js', 'resources/app/electron/preload.cjs',
    'resources/app/electron/input-hook.cjs', 'resources/app/server/room-server.js',
    'resources/app/renderer/index.html', 'resources/app/renderer/pet.html',
    'resources/app/renderer/player.html', 'resources/app/renderer/friends.html',
    'resources/app/vendor/google-auth/node_modules/google-auth-library/package.json']

def checked_files(path, root):
    if not path.exists(): return
    if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise ValueError('Reparse points are not allowed in a release')
    if not path.resolve().is_relative_to(root): raise ValueError('File escapes source directory')
    if path.is_dir():
        for child in sorted(path.iterdir()): yield from checked_files(child, root)
    else:
        name = path.name.lower()
        if name.startswith(('client_secret', '.env')) or name in {'google-client.json', 'active-account.bin'} or name.endswith(('.pem', '.key', '.pfx', '.log')):
            raise ValueError('Private material found in runtime; review before packaging')
        yield path

def collect_files(source):
    source = Path(source).resolve()
    for name in REQUIRED:
        if not (source/name).is_file(): raise ValueError('Required desktop runtime missing: '+name)
    for name in TREES:
        if not (source/name).is_dir() or not any((source/name).iterdir()):
            raise ValueError('Required runtime directory missing or empty: '+name)
    package = json.loads((source/'resources/app/package.json').read_text(encoding='utf-8-sig'))
    for name in package.get('dependencies', {}):
        if not re.fullmatch(r'(?:@[\w.-]+/)?[\w.-]+', name) or name in ('.', '..'):
            raise ValueError('Invalid dependency name')
        if not (source/'resources/app/node_modules'/name/'package.json').is_file():
            raise ValueError('Required dependency missing: '+name)
    files = []
    for name in sorted(ROOT_FILES) + TREES + ['resources/bin/yt-dlp.exe', 'resources/bin/YoMiUpdater.exe', 'resources/bin/YoMiUpdater.exe.config', 'resources/app/package.json']:
        files.extend(checked_files(source/name, source))
    return sorted(set(files))

def build_release(source, output, backend):
    source, output = Path(source).resolve(), Path(output).resolve()
    if output.is_relative_to(source): raise ValueError('Output must be outside the desktop source')
    uri = urlsplit(backend)
    if uri.scheme != 'https' or not uri.hostname or uri.username or uri.password or uri.path not in ('','/') or uri.query or uri.fragment:
        raise ValueError('Backend must be an HTTPS origin')
    backend = backend.rstrip('/')
    try: local = uri.hostname.lower() == 'localhost' or ipaddress.ip_address(uri.hostname).is_loopback
    except ValueError: local = False
    files = collect_files(source)
    version = json.loads((source/'resources/app/package.json').read_text(encoding='utf-8-sig'))['version']
    if not isinstance(version,str) or not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?',version): raise ValueError('Invalid desktop version')
    filename = f'YoMi_Desktop_Pet-{version}-win-x64.zip'
    output.mkdir(parents=True, exist_ok=True)
    target, temporary = output/filename, output/(filename+'.tmp')
    if target.exists() or temporary.exists(): raise FileExistsError('Release already exists; use a new version or empty output directory')
    try:
        with zipfile.ZipFile(temporary,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=4) as archive:
            for file in files:
                before = file.stat()
                archive.write(file, 'YoMi_Desktop_Pet/'+file.relative_to(source).as_posix())
                after = file.stat()
                if (before.st_size,before.st_mtime_ns) != (after.st_size,after.st_mtime_ns): raise ValueError('Source changed during packaging')
            archive.writestr('YoMi_Desktop_Pet/resources/app/backend.json',json.dumps({'url':backend}))
            archive.writestr('YoMi_Desktop_Pet/START_HERE.txt',
                'YoMi_Desktop_Pet\n\n1. 將 ZIP 完整解壓縮到可寫入的資料夾。\n2. 開啟 YoMi_Desktop_Pet.exe。\n'
                '3. 第一次啟動會建立自己的 Data；初始 120 幣是預設值。\n'
                '後續請在桌寵內按「檢查更新／立即更新」，程式會下載、安裝並自動重啟；帳號及進度保留。\n'
                '此包不含任何使用者帳號、進度、Google 用戶端設定或登入憑證。\n'
                '訪客可直接使用；按 Google 登入即可登入或建立會員，無須匯入設定檔。\n'
                f'會員同步服務：{backend}\n' +
                ('localhost 僅連到這台電腦，會員同步需啟動 YoMi_Frontend。\n' if local else
                 '會員同步使用正式 HTTPS 服務，需要網路；離線時仍保留本機進度。\n'))
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None: raise ValueError('ZIP integrity check failed')
            if any(not n.startswith('YoMi_Desktop_Pet/') or '..' in n.split('/') for n in archive.namelist()): raise ValueError('Invalid archive paths')
        with temporary.open('rb') as stream: digest = hashlib.file_digest(stream,'sha256').hexdigest()
        release = {'version':version,'fileName':filename,'sizeBytes':temporary.stat().st_size,
            'sha256':digest,'releasedAt':datetime.now(timezone.utc).isoformat(),
            'backendOrigin':backend,'isLocalBuild':local}
        os.replace(temporary,target)
        (output/(filename+'.sha256')).write_text(digest+'  '+filename+'\n',encoding='utf-8')
        manifest = output/'release.json.tmp'
        manifest.write_text(json.dumps(release,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        os.replace(manifest,output/'release.json')
        return release
    finally:
        if temporary.exists(): temporary.unlink()

def build_installer_release(source, output, backend, makensis=None):
    source, output = Path(source).resolve(), Path(output).resolve()
    makensis = makensis or os.environ.get('YOMI_MAKENSIS') or shutil.which('makensis')
    if not makensis: raise ValueError('Set --makensis or YOMI_MAKENSIS to the portable NSIS compiler')
    if output.is_relative_to(source): raise ValueError('Output must be outside the desktop source')
    helper = source/'packaging/installer/build_installer.py'
    if not helper.is_file(): raise ValueError('Installer builder missing')
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='YoMi-release-') as temporary:
        work = Path(temporary)
        release = build_release(source, work/'runtime', backend)
        update_name = f"YoMi_Desktop_Pet-{release['version']}-update-win-x64.zip"
        names = [release['fileName'], release['fileName']+'.sha256', update_name, update_name+'.sha256']
        if any((output/name).exists() for name in names):
            raise FileExistsError('Release already exists; use a new version or empty output directory')
        runtime_zip = work/'runtime'/release['fileName']
        with zipfile.ZipFile(runtime_zip) as archive:
            archive.extractall(work/'extracted')
        command = [sys.executable, str(helper), '--runtime', str(work/'extracted/YoMi_Desktop_Pet'),
                   '--version', release['version'], '--backend-origin', release['backendOrigin'],
                   '--output', str(work/'setup')]
        command += ['--makensis', str(makensis)]
        subprocess.run(command, check=True)
        setup = work/'setup/YoMi_Desktop_Pet_Setup.exe'
        with setup.open('rb') as stream:
            if stream.read(2) != b'MZ': raise ValueError('Installer is not a Windows executable')
        staged = work/'publish'
        staged.mkdir()
        shutil.move(runtime_zip, staged/update_name)
        installer_zip = staged/release['fileName']
        with zipfile.ZipFile(installer_zip, 'x', compression=zipfile.ZIP_STORED) as archive:
            archive.write(setup, 'YoMi_Desktop_Pet_Setup.exe')
        with zipfile.ZipFile(installer_zip) as archive:
            if archive.namelist() != ['YoMi_Desktop_Pet_Setup.exe'] or archive.testzip() is not None:
                raise ValueError('Installer archive validation failed')
        release['update'] = {key: release[key] for key in ['fileName', 'sizeBytes', 'sha256']}
        release['update']['fileName'] = update_name
        release['sizeBytes'] = installer_zip.stat().st_size
        with installer_zip.open('rb') as stream:
            release['sha256'] = hashlib.file_digest(stream, 'sha256').hexdigest()
        for payload in [release, release['update']]:
            (staged/(payload['fileName']+'.sha256')).write_text(payload['sha256']+'  '+payload['fileName']+'\n', encoding='utf-8')
        (staged/'release.json').write_text(json.dumps(release, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
        # Activate only after both verified packages exist; an older app still receives the runtime ZIP.
        for name in names: shutil.move(staged/name, output/name)
        manifest = output/'release.json.tmp'
        with manifest.open('x', encoding='utf-8') as stream:
            stream.write((staged/'release.json').read_text(encoding='utf-8'))
        os.replace(manifest, output/'release.json')
        return release

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',required=True,type=Path)
    parser.add_argument('--output',default=Path('D:/Sixdots/Project/YoMi/YoMi_Desktop_Pet_Release'),type=Path)
    parser.add_argument('--backend',default='https://yumiclub.duckdns.org:8082')
    parser.add_argument('--makensis',type=Path,help='Portable NSIS compiler path (or set YOMI_MAKENSIS)')
    args=parser.parse_args()
    print(json.dumps(build_installer_release(args.source,args.output,args.backend,args.makensis),ensure_ascii=False,indent=2))

