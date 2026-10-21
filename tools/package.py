#!/usr/bin/env python3
"""Package a built plugin with the Jellyfin 12.2 local manifest and artwork."""
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
    'category':'General','changelog':'Cover index progress, phase counters, periodic diagnostic logs and reporting while waiting for an active background build.',
    'description':'Per-user completion rules and independent resume positions for Lowfin. Requires Jellyfin 12.2.x.',
    'guid':'68abfd24-882d-4937-a104-44d55181b41e','name':'Lowfin Companion',
    'overview':'Smart Completion, Persistent Resume, Incremental Cache & Cover Tags','owner':'Lowfin','targetAbi':'12.2.0.0',
    'timestamp':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'version':assembly_version,'status':'Active','autoUpdate':True,
    'imagePath':'lowfin-companion.png','assemblies':['Lowfin.Companion.dll']
}
(folder/'meta.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
shutil.copy2(root/'README.md',destination/'INSTALLATION.md')
shutil.copy2(root/'README.de.md',destination/'INSTALLATION.de.md')
archive=destination/('Lowfin-Companion-'+version+'.zip')
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as out:
    for source in sorted(folder.iterdir()): out.write(source,source.relative_to(destination))
    out.write(destination/'INSTALLATION.md','INSTALLATION.md')
    out.write(destination/'INSTALLATION.de.md','INSTALLATION.de.md')
files=[archive,*sorted(folder.iterdir())]
(destination/'SHA256SUMS.txt').write_text(''.join(hashlib.sha256(path.read_bytes()).hexdigest()+'  '+str(path.relative_to(destination))+'\n' for path in files))
print(str(destination))
