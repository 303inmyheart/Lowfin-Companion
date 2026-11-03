#!/usr/bin/env python3
import importlib.util,os,tempfile,subprocess,json,zipfile,unittest
from pathlib import Path
SCRIPT=Path(__file__).with_name('check_privacy.py').resolve()
class PrivacyTests(unittest.TestCase):
 def check(self,name,data,expected):
  with tempfile.TemporaryDirectory() as d:
   p=Path(d)/name;p.write_bytes(data);r=subprocess.run(['python3',str(SCRIPT),str(p)],env=dict(os.environ,LOWFIN_PRIVATE_TERMS=json.dumps(['SyntheticPrivateName'])),capture_output=True);self.assertEqual(expected,r.returncode)
 def test_private_account_rejected(self):self.check('sample.txt',b'SyntheticPrivateName',1)
 def test_home_path_rejected(self):self.check('sample.txt',('/home/'+'sensitive_user/project').encode(),1)
 def test_documentation_placeholder_allowed(self):self.check('sample.txt',b'/home/user/project 192.0.2.10',0)
 def test_owner_allowed_image_payload(self):self.check('screen.png',b'SyntheticPrivateName',0)
 def test_retired_drawer_rejected(self):self.check('drawer-hd.png',b'image',1)
 def test_apk_members_scanned(self):
  import io
  b=io.BytesIO()
  with zipfile.ZipFile(b,'w') as z:z.writestr('classes.dex',b'SyntheticPrivateName')
  self.check('sample.apk',b.getvalue(),1)
if __name__=='__main__':unittest.main()
