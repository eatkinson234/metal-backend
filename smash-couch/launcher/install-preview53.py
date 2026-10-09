#!/usr/bin/env python3
"""Exact new-preview-launcher install/retained rollback. Never launches an app or writes Data."""
from pathlib import Path
import argparse, datetime, fcntl, hashlib, json, os, shutil, stat, subprocess, uuid

OWN = Path(__file__).resolve().parent
SELECTION_SHA = 'bd820c973347018a52d558213e43848d774ae2ef4cb0a0039f16bcefd2afa890'
HOME_ROOT = Path.home()
LAB = HOME_ROOT/'Library/Application Support/Smash Couch/Lab'
OLD_ROOT = LAB/'native-metal-controller-lobby36-1'
NEW_ROOT = LAB/'native-metal-controller-lobby53-preview-r1'
TARGET = HOME_ROOT/'Applications/Smash Couch Native Metal 53 Preview.app'
OLD_APP = HOME_ROOT/'Applications/Smash Couch Native Metal.app'
DESKTOP = HOME_ROOT/'Desktop/Games/Play Smash.app'
BACKUPS = HOME_ROOT/'Library/Application Support/Smash Couch/Backups'


def need(ok, text):
    if not ok: raise RuntimeError(text)


def sha(path):
    h=hashlib.sha256()
    with path.open('rb') as f:
        for b in iter(lambda:f.read(1024*1024), b''): h.update(b)
    return h.hexdigest()


def regular(path):
    need(path.is_file() and not path.is_symlink() and path.resolve(strict=True)==path, 'Unsafe file: '+str(path))


def tree(path):
    need(path.is_dir() and not path.is_symlink() and path.resolve(strict=True)==path, 'Unsafe tree: '+str(path))
    files={}
    for p in sorted(path.rglob('*')):
        need(not p.is_symlink(), 'Symlink refused: '+str(p))
        if p.is_file(): files[str(p.relative_to(path))]=sha(p)
        else: need(p.is_dir(), 'Special file refused: '+str(p))
    return files


def signature(path):
    subprocess.run(['/usr/bin/codesign','--verify','--deep','--strict',str(path)],check=True,capture_output=True)


def protected():
    # A fresh inventory protects both progress sets; historical save hashes are not restored.
    out={'old_data':tree(OLD_ROOT/'Data'),'preview_data':tree(NEW_ROOT/'Data'),
         'old_launcher':tree(OLD_APP),'desktop_link':os.readlink(DESKTOP)}
    need(DESKTOP.is_symlink() and out['desktop_link']==str(OLD_APP), 'Old Desktop shortcut changed; preserve')
    for name,root in [('old',OLD_ROOT),('preview',NEW_ROOT)]:
        for leaf in ['preview-gate.json','lobby-preferences.json']:
            p=root/leaf;regular(p);out[name+'_'+leaf]=sha(p)
    return out


def locked_roots():
    fds=[]
    try:
        for root in [OLD_ROOT,NEW_ROOT]:
            need(root.resolve(strict=True)==root and not root.is_symlink(), 'Unsafe lobby root')
            lock=root/'launcher.lock';regular(lock)
            fd=os.open(lock,os.O_RDWR|os.O_NOFOLLOW);fds.append(fd)
            fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
            lease=root/'native-metal-preview-active-session.json'
            need(not lease.exists() and not lease.is_symlink(), 'Active lease remains; close normally')
        p=subprocess.run(['/usr/bin/pgrep','-x','Ryujinx'],capture_output=True)
        need(p.returncode==1, 'Emulator exists or absence check failed; close normally')
        return fds
    except Exception:
        for fd in reversed(fds): os.close(fd)
        raise


def save(path,value):
    with path.open('x') as f: json.dump(value,f,indent=2);f.write('\n')


def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('action',choices=['plan','install','rollback'])
    ap.add_argument('--manifest-sha256',required=True)
    ap.add_argument('--receipt',type=Path)
    ap.add_argument('--apply-preview53-r1',action='store_true')
    a=ap.parse_args()
    manifest=OWN/'RELEASE-MANIFEST.json';regular(manifest)
    need(len(a.manifest_sha256)==64 and sha(manifest)==a.manifest_sha256, 'Release manifest hash mismatch')
    m=json.loads(manifest.read_text())
    need(m['schema']=='native53-normal-preview-launcher-release-v1' and m['selection_sha256']==SELECTION_SHA,
         'Wrong selected release')
    need(m['installed_launcher']==str(TARGET) and m['private_root']==str(NEW_ROOT) and m['current36_root']==str(OLD_ROOT),
         'Release identity mismatch')
    need(m['runtime_admitted'] is False and m['data_writes_allowed'] is False and m['desktop_changes_allowed'] is False,
         'Unsupported release actions')
    need(a.action=='plan' or a.apply_preview53_r1, 'Explicit preview apply flag required')
    fds=locked_roots()
    try:
        gate=NEW_ROOT/'preview-gate.json'
        gate_hash=sha(gate)
        need(gate_hash in [m['preparation_gate_sha256'],m['approved_gate_sha256']], 'Preview gate changed; preserve')
        gate_value=json.loads(gate.read_text())
        if a.action=='install':
            need(gate_value['approved'] is True and gate_hash==m['approved_gate_sha256'], 'Root has not approved this exact preview gate; no install')
        before=protected()
        stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'-'+uuid.uuid4().hex[:8]
        evidence=OWN/'install-evidence'
        if a.action=='rollback':
            need(a.receipt is not None, 'Exact preview install receipt required')
            regular(a.receipt);r=json.loads(a.receipt.read_text())
            need(r['schema']=='native53-preview-launcher-install-v1' and r['installed_launcher']==str(TARGET) and
                 r['release_manifest_sha256']==a.manifest_sha256, 'Foreign receipt')
            need(tree(TARGET)==r['installed_files']==m['launcher_files'], 'Installed preview changed; preserve')
            signature(TARGET)
            retained=BACKUPS/('native53-preview-retained-'+stamp)
            need(not retained.exists() and BACKUPS.resolve(strict=True)==BACKUPS, 'Unsafe backup destination')
            retained.mkdir();TARGET.rename(retained/TARGET.name)
            need(protected()==before and not TARGET.exists(), 'Protected state changed during rollback; preserve evidence')
            evidence.mkdir(exist_ok=True)
            p=evidence/('rollback-'+stamp+'.json')
            save(p,{'schema':'native53-preview-launcher-retained-rollback-v1','release_manifest_sha256':a.manifest_sha256,
                    'retained_preview_launcher':str(retained/TARGET.name),'both_Data_sets_preserved':True,
                    'old_launcher_and_Desktop_preserved':True,'data_restore_performed':False})
            print(json.dumps({'rollback_complete':True,'receipt':str(p),'retained':str(retained/TARGET.name)}))
            return
        source=Path(m['source_launcher']);regular(source/'Contents/Info.plist')
        need(source==OWN/'build/launcher-output'/TARGET.name and tree(source)==m['launcher_files'], 'Wrong source launcher')
        signature(source)
        need(not TARGET.exists() and not TARGET.is_symlink(), 'Preview destination already exists; preserve')
        plan={'schema':'native53-preview-launcher-install-v1','release_manifest_sha256':a.manifest_sha256,
              'source_launcher':str(source),'installed_launcher':str(TARGET),'installed_files':m['launcher_files'],
              'private_root':str(NEW_ROOT),'gate_sha256':gate_hash,'both_Data_sets_preserved':True,
              'old_launcher_and_Desktop_preserved':True,'data_restore_performed':False}
        if a.action=='plan':
            print(json.dumps({'plan':plan,'applied':False,'root_gate_approved':gate_value['approved']},indent=2));return
        staging=TARGET.parent/('.native53-preview-stage-'+stamp+'.app')
        need(TARGET.parent.resolve(strict=True)==TARGET.parent and not staging.exists(), 'Unsafe staging destination')
        subprocess.run(['/bin/cp','-cR',str(source),str(staging)],check=True)
        need(tree(staging)==m['launcher_files'] and protected()==before, 'Staged bytes/protected data changed; preserve')
        signature(staging)
        evidence.mkdir(exist_ok=True);receipt=evidence/('install-'+stamp+'.json')
        save(evidence/('preaction-'+stamp+'.json'),plan)
        staging.rename(TARGET)
        need(tree(TARGET)==m['launcher_files'] and protected()==before, 'Installed bytes/protected data changed; preserve')
        signature(TARGET);save(receipt,plan)
        print(json.dumps({'installed':True,'receipt':str(receipt),'Desktop_changed':False,
                         'rollback':['python3',str(Path(__file__).resolve()),'rollback','--manifest-sha256',a.manifest_sha256,
                                     '--receipt',str(receipt),'--apply-preview53-r1']},indent=2))
    finally:
        for fd in reversed(fds):os.close(fd)

if __name__=='__main__':main()
