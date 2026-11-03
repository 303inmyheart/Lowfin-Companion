#!/usr/bin/env python3
"""Read-only publication guard. Optional private terms come from an environment secret."""
import argparse,io,json,os,re,subprocess,sys,zipfile
from pathlib import Path
IMAGE={'.png','.jpg','.jpeg','.webp','.gif','.bmp','.ico','.svg'}
TERMS=json.loads(os.environ.get('LOWFIN_PRIVATE_TERMS','[]'))
PATTERNS=[re.compile(rb'/home/(?!user(?:/|\b)|runner(?:/|\b)|example(?:/|\b)|YOUR_USER(?:/|\b))[A-Za-z0-9_.-]+/'),re.compile(rb'\b192\.168\.\d{1,3}\.\d{1,3}\b'),re.compile(rb'\b(?:ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|mfa\.[A-Za-z0-9_-]{40,})\b')]
failures=[]
def check(data,name,depth=0):
 if Path(name).name.lower()=='drawer-hd.png':failures.append(name);return
 if Path(name).suffix.lower() in IMAGE:return
 if zipfile.is_zipfile(io.BytesIO(data)) and depth<4:
  with zipfile.ZipFile(io.BytesIO(data)) as z:
   for n in z.infolist():
    if not n.is_dir():check(z.read(n),name+'!'+n.filename,depth+1)
  return
 if any(p.search(data) for p in PATTERNS) or any(t and re.search(rb'(?i)(?<![A-Za-z0-9_])'+re.escape(t.encode())+rb'(?![A-Za-z0-9_])',data) for t in TERMS):failures.append(name)
def main():
 parser=argparse.ArgumentParser();parser.add_argument('paths',nargs='*');parser.add_argument('--staged',action='store_true');args=parser.parse_args()
 if os.environ.get('LOWFIN_REQUIRE_PRIVACY_TERMS')=='true' and not TERMS:raise SystemExit('Publication blocked: configure private-term scanning secret.')
 if args.paths:
  for path in args.paths:
   p=Path(path)
   for q in ([p] if p.is_file() else p.rglob('*')):
    if q.is_file():check(q.read_bytes(),str(q))
 else:
  files=subprocess.check_output(['git','diff','--cached','--name-only','--diff-filter=ACMR'] if args.staged else ['git','ls-files'],text=True).splitlines()
  for name in files:
   if args.staged:check(subprocess.check_output(['git','show',':'+name]),name)
   elif Path(name).is_file():check(Path(name).read_bytes(),name)
 if failures:
  print('Publication blocked: privacy patterns in',len(set(failures)),'locations. Review locally; matched values are not printed.')
  # Do not emit private source text or filenames into public CI logs.
  return 1
 print('Privacy guard passed. Image pixels are excluded by owner policy.');return 0
if __name__=='__main__':sys.exit(main())
