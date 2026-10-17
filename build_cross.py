from pathlib import Path
import re,subprocess,concurrent.futures,sys,os
root=Path(__file__).parent.resolve();tool=Path(os.environ.get('LLVM_MINGW_ROOT', str(root.parents[1]/'toolchains/llvm-mingw-20261006-ucrt-ubuntu-22.04-x86_64')))/'bin'
compat=root/'build-compat';compat.mkdir(exist_ok=True)
for n in ['Shlwapi','PowrProf','Psapi']:
 p=compat/('lib'+n+'.a')
 if not p.exists(): p.symlink_to((tool.parent/'x86_64-w64-mingw32/lib'/('lib'+n.lower()+'.a')).resolve())
cc=tool/'x86_64-w64-mingw32-clang';cxx=tool/'x86_64-w64-mingw32-clang++'
out=root/'build-cross';out.mkdir(exist_ok=True)
bat=(root/'build.bat').read_text()
lz=re.findall(r'%LZ%\\([\w]+\.c)',bat);ur=re.findall(r'%UR%\\([\w]+\.cpp)',bat)
sources=[('common/third_party/lzma/'+n,[]) for n in lz]+[('common/third_party/unrar/'+n,['-DRARDLL','-DUNRAR','-DSILENT','-DSF6_LOADER_NO_SMP','-DSF6_LOADER_NO_CRYPT32']) for n in ur]
sources += [(p,[]) for p in ['common/third_party/zstddeclib.c','common/third_party/miniz.c','common/pak.cpp','common/archive.cpp','costumes/loader/patch.cpp','costumes/loader/structural.cpp','costumes/loader/structural_scene.cpp','costumes/loader/structural_msg.cpp','costumes/loader/vanilla_inventory.cpp','costumes/loader/fighter_names.cpp','costumes/loader/loader_core.cpp','proxy/slots_proxy.cpp']]
flags=['-O2','-DSLOTS_NO_STAGES','-fms-extensions','-D_CRT_SECURE_NO_WARNINGS','-D_WIN32_WINNT=0x0601','-Ibuild-compat','-Icommon','-Icommon/third_party','-Icostumes/loader','-Istages/loader','-Iproxy']
def compile(item):
 name,extra=item;obj=out/(name.replace('/','_')+'.o')
 if obj.exists() and obj.stat().st_mtime>(root/name).stat().st_mtime:return str(obj)
 cmd=[str(cc if name.endswith('.c') else cxx),*flags,*extra]
 if name.endswith('.cpp'):cmd+=['-std=c++17']
 cmd+=['-c',name,'-o',str(obj)]
 r=subprocess.run(cmd,cwd=root,capture_output=True,text=True)
 (out/(obj.name+'.log')).write_text(r.stdout+r.stderr)
 if r.returncode:print('FAIL',name,r.stderr[-3000:],flush=True);raise RuntimeError(name)
 print('OK',name,flush=True);return str(obj)
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as ex:objects=list(ex.map(compile,sources))
cmd=[str(cxx),'-shared','-static','-Lbuild-compat',*objects,'-o',str(out/'amd_ags_x64.dll'),'-lbcrypt','-ladvapi32','-lshell32','-lole32','-luser32']
r=subprocess.run(cmd,cwd=root,capture_output=True,text=True);(out/'link.log').write_text(r.stdout+r.stderr);print(r.stdout+r.stderr)
if r.returncode:sys.exit(r.returncode)
cmd=[str(cxx),'-shared','-static','-O2','-fms-extensions','-std=c++20','-Iplugin/include','-Ibuild-compat','plugin/src/common.cpp','plugin/src/costumes.cpp','plugin/src/main.cpp','-o',str(out/'SF6_CostumeSlotsNative.dll')]
r=subprocess.run(cmd,cwd=root,capture_output=True,text=True);(out/'plugin-link.log').write_text(r.stdout+r.stderr);print(r.stdout+r.stderr);sys.exit(r.returncode)
