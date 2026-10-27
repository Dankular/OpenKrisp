"""Resolve installed CUDA DLLs for Tract without adding another GPU framework."""
import os
from pathlib import Path


def cuda_environment():
    global _dll_directories
    if '_dll_directories' in globals() or os.name != 'nt': return
    import ctypes
    ctypes.windll.kernel32.SetErrorMode(0x8007)
    root = Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'NVIDIA GPU Computing Toolkit' / 'CUDA'
    directories = [p for version in sorted(root.glob('v*'), reverse=True)
                   for p in (version/'bin'/'x64', version/'bin') if p.exists()]
    os.environ['PATH'] = os.pathsep.join(map(str, directories)) + os.pathsep + os.environ.get('PATH', '')
    _dll_directories = [os.add_dll_directory(str(p)) for p in directories]
