#!/usr/bin/env python3
"""Package a built plugin with local metadata, repository feed and installation archives."""
import datetime, hashlib, json, pathlib, shutil, sys, zipfile, xml.etree.ElementTree as ET

root=pathlib.Path(__file__).resolve().parents[1]
version=ET.parse(root/'src/Lowfin.Companion/Lowfin.Companion.csproj').findtext('./PropertyGroup/Version')
assembly_version=version+'.0'
destination=pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else root/'artifacts'
destination.mkdir(parents=True,exist_ok=True)
folder=destination/('Lowfin.Companion_'+assembly_version)
folder.mkdir(exist_ok=True)
shutil.copy2(root/'src/Lowfin.Companion/bin/Release/net10.0/Lowfin.Companion.dll',folder/'Lowfin.Companion.dll')
shutil.copy2(root/'assets/lowfin-companion.png',folder/'lowfin-companion.png')
manifest={
    'category':'General','changelog':'Dashboard sidebar with the original Lowfin L, reorganized settings, optional per-user Discord presence with compatible gateway discovery, large READY support, encrypted token storage, cover rendering, uploads and persistent caching, live TV programme timing and no standalone import, plus Jellyfin 12.3 compatibility; minimum server version remains 12.2.',
    'description':'Per-user completion rules and independent resume positions for Lowfin. Requires Jellyfin 12.2 or newer.',
    'guid':'68abfd24-882d-4937-a104-44d55181b41e','name':'Lowfin Companion',
    'overview':'Completion, resume, incremental cache, cover tags and Discord presence','owner':'Lowfin','targetAbi':'12.2.0.0',
    'timestamp':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'version':assembly_version,'status':'Active','autoUpdate':True,
    'imagePath':'lowfin-companion.png','assemblies':['Lowfin.Companion.dll']
}
(folder/'meta.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
shutil.copy2(root/'README.md',destination/'INSTALLATION.md')
shutil.copy2(root/'README.de.md',destination/'INSTALLATION.de.md')
archive=destination/('Lowfin-Companion-'+version+'.zip')
manual=destination/('Lowfin-Companion-'+version+'-manual.zip')
# Jellyfin extracts repository ZIPs directly into the plugin directory.
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as out:
    for source in sorted(folder.iterdir()): out.write(source,source.name)
with zipfile.ZipFile(manual,'w',zipfile.ZIP_DEFLATED) as out:
    for source in sorted(folder.iterdir()): out.write(source,source.relative_to(destination))
    for name in ('README.md','README.de.md','CHANGELOG.md','LICENSE','THIRD_PARTY.md'):
        out.write(root/name,name)
    for source in sorted((root/'third-party').rglob('*')):
        if source.is_file(): out.write(source,source.relative_to(root))
feed_path=root/'manifest.json'
feed=json.loads(feed_path.read_text(encoding='utf-8'))
plugin=feed[0]
plugin.update(overview=manifest['overview'],description=manifest['description'],imageUrl=f'https://github.com/303inmyheart/Lowfin-Companion/releases/download/v{version}/lowfin-companion.png')
entry={'version':assembly_version,'changelog':manifest['changelog'],'targetAbi':manifest['targetAbi'],
       'sourceUrl':f'https://github.com/303inmyheart/Lowfin-Companion/releases/download/v{version}/{archive.name}',
       'checksum':hashlib.md5(archive.read_bytes()).hexdigest(),'timestamp':manifest['timestamp']}
plugin['versions']=[entry,*[v for v in plugin['versions'] if v['version']!=assembly_version]]
feed_path.write_text(json.dumps(feed,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
shutil.copy2(feed_path,destination/'manifest.json')
shutil.copy2(root/'assets/lowfin-companion.png',destination/'lowfin-companion.png')
files=[archive,manual,*sorted(folder.iterdir())]
(destination/'SHA256SUMS.txt').write_text(''.join(hashlib.sha256(path.read_bytes()).hexdigest()+'  '+str(path.relative_to(destination))+'\n' for path in files))
print(str(destination))
