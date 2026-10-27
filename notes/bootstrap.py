"""One-time installation. Only setup downloads files; inference is offline."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.request
import zipfile

LLAMA_TAG = "b11913"
TITLE_REPO = "SupraLabs/supra-title-50M-pre-gguf"
TITLE_REVISION = "375f819a5c485afd7ab01c056dc9378e470ab923"
TITLE_FILE = "SupraTitle-50M-Q8_0.gguf"
NOTES_REPO = "bartowski/Qwen2.5-7B-Instruct-1M-GGUF"
NOTES_REVISION = "381ac2097f05bff2f77e7e3864239c11e521c3d3"
NOTES_FILE = "Qwen2.5-7B-Instruct-1M-Q4_K_M.gguf"
WHISTLE_ONNX_REVISION = "14fc92cb09ad5c271fe9a4ec4be70274790050d2"
TURN_REVISION = "dbd03130e87854011fa8c2f203077f2cbfc3adad"


def install_notes_runtime(home):
    binary_dir=home/'qwen-runtime'
    if all((binary_dir/name).exists() for name in ['llama-server.exe','llama-server-impl.dll','mtmd.dll','ggml-cuda.dll','llama-common.dll']):
        return binary_dir/'llama-server.exe'
    binary_dir.mkdir(exist_ok=True)
    archive_path=home/'qwen-cuda-runtime.zip'
    if not archive_path.exists() or not zipfile.is_zipfile(archive_path):
        print('Downloading the native Qwen CUDA runtime…',flush=True)
        temporary=home/'qwen-cuda-runtime.zip.part'
        urllib.request.urlretrieve(f'https://github.com/ggml-org/llama.cpp/releases/download/{LLAMA_TAG}/llama-{LLAMA_TAG}-bin-win-cuda-12.4-x64.zip',temporary)
        temporary.replace(archive_path)
    required={'llama-server.exe','llama-server-impl.dll','mtmd.dll','llama.dll','llama-common.dll','ggml.dll','ggml-base.dll','ggml-cuda.dll','ggml-cpu.dll','libomp.dll'}
    with zipfile.ZipFile(archive_path) as archive:
        for member in archive.infolist():
            name=Path(member.filename).name
            if name in required or (name.startswith('ggml-cpu-') and name.endswith('.dll')) or name.startswith('LICENSE'):
                (binary_dir/name).write_bytes(archive.read(member))
    # Reuse installed CUDA 12 libraries; download the runtime DLLs only if CUDA 12 is absent.
    cuda_root=Path(os.environ.get('ProgramFiles',r'C:\Program Files'))/'NVIDIA GPU Computing Toolkit'/'CUDA'
    available=any(list(cuda_root.glob('v12*/bin/cublas64_12.dll'))+list(cuda_root.glob('v12*/bin/x64/cublas64_12.dll')))
    if not available:
        deps=home/'qwen-cudart.zip'
        urllib.request.urlretrieve(f'https://github.com/ggml-org/llama.cpp/releases/download/{LLAMA_TAG}/cudart-llama-bin-win-cuda-12.4-x64.zip',deps)
        with zipfile.ZipFile(deps) as archive:
            for member in archive.infolist():
                name=Path(member.filename).name
                if name.endswith('.dll'): (binary_dir/name).write_bytes(archive.read(member))
        deps.unlink()
    archive_path.unlink()
    return binary_dir/'llama-server.exe'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--home", required=True)
    parser.add_argument("--prepare-only", action="store_true", help="Install and stage config without changing an active app configuration")
    parser.add_argument("--redownload-asr", action="store_true")
    args = parser.parse_args()
    home = Path(args.home).resolve()
    home.mkdir(parents=True, exist_ok=True)
    python = home / "venv" / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    if Path(sys.executable).resolve() != python.resolve():
        if not python.exists():
            subprocess.run([sys.executable, "-m", "venv", str(home / "venv")], check=True)
        subprocess.run([str(python), "-m", "pip", "install", "-r", str(Path(__file__).with_name("requirements.txt"))], check=True)
        subprocess.run([str(python), str(Path(__file__).resolve()), "--home", str(home)] + (["--prepare-only"] if args.prepare_only else []) + (["--redownload-asr"] if args.redownload_asr else []), check=True)
        return
    os.environ.update(HF_HOME=str(home / "cache"), NEEDLE_TELEMETRY="0", DO_NOT_TRACK="1", HF_HUB_DISABLE_TELEMETRY="1")
    from huggingface_hub import hf_hub_download
    model_dir = home / "models"
    model_dir.mkdir(exist_ok=True)
    asr_dir = model_dir / "whistle-onnx"
    for filename in ["whistle.pack", "onnx/encoder.onnx", "onnx/encoder.onnx.data", "onnx/decoder.onnx", "onnx/decoder.onnx.data"]:
        print("Downloading Whistle GPU model: " + filename, flush=True)
        hf_hub_download("mrfakename/whistle-ONNX", filename, revision=WHISTLE_ONNX_REVISION, local_dir=asr_dir, force_download=args.redownload_asr)
    turn_dir = model_dir / "smart-turn"
    for filename in ["config.json", "preprocessor_config.json", "onnx/model.onnx"]:
        print("Downloading Smart Turn: " + filename, flush=True)
        hf_hub_download("onnx-community/smart-turn-v3-ONNX", filename, revision=TURN_REVISION, local_dir=turn_dir)
    print("Downloading speaker diarization models...", flush=True)
    base = "https://github.com/k2-fsa/sherpa-onnx/releases/download/"
    files = {
        "segmentation.tar.bz2": base + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
        "embedding.onnx": base + "speaker-recongition-models/nemo_en_titanet_small.onnx",
    }
    for name, url in files.items():
        dest = model_dir / name
        if not dest.exists():
            urllib.request.urlretrieve(url, str(dest) + ".part")
            Path(str(dest) + ".part").replace(dest)
    import tarfile
    with tarfile.open(model_dir / "segmentation.tar.bz2") as archive:
        member = next(m for m in archive.getmembers() if m.name.endswith("/model.onnx"))
        (model_dir / "segmentation.onnx").write_bytes(archive.extractfile(member).read())
    print("Downloading Supra 50M title model (56 MB)...", flush=True)
    title_model = hf_hub_download(TITLE_REPO, TITLE_FILE, revision=TITLE_REVISION, local_dir=model_dir / "supra-title")
    print("Downloading Qwen2.5-7B-Instruct-1M Q4_K_M notes model (4.68 GB)...", flush=True)
    notes_model=hf_hub_download(NOTES_REPO,NOTES_FILE,revision=NOTES_REVISION,local_dir=model_dir/'qwen-notes')
    notes_server=install_notes_runtime(home)
    binary_dir = home / "title-runtime"
    binary_dir.mkdir(exist_ok=True)
    if not (binary_dir / "llama-completion.exe").exists():
        # Keep only CPU completion libraries, rather than a GPU/server toolkit.
        required = {"llama-completion.exe", "llama-completion-impl.dll", "llama-common.dll", "llama.dll", "ggml.dll", "ggml-base.dll", "libomp.dll"}
        archive_path = home / "title-runtime.zip"
        urllib.request.urlretrieve(f"https://github.com/ggml-org/llama.cpp/releases/download/{LLAMA_TAG}/llama-{LLAMA_TAG}-bin-win-cpu-x64.zip", archive_path)
        with zipfile.ZipFile(archive_path) as archive:
            for member in archive.infolist():
                name = Path(member.filename).name
                if name in required or (name.startswith("ggml-cpu-") and name.endswith(".dll")) or name.startswith("LICENSE"):
                    (binary_dir / name).write_bytes(archive.read(member))
        archive_path.unlink()
    config = {"notesBackend": "qwen2.5-1m", "asrBackend": "tract-cuda", "python": str(python),
              "whistleOnnxDirectory": str(asr_dir), "smartTurnDirectory": str(turn_dir),
              "segmentation": str(model_dir / "segmentation.onnx"), "embedding": str(model_dir / "embedding.onnx"),
              "titleModel": str(title_model), "titleCli": str(binary_dir / "llama-completion.exe"),
              "notesModel":str(notes_model),"notesServer":str(notes_server),"notesContext":16384}
    destination = home / ("config.pending.json" if args.prepare_only else "config.json")
    temporary = destination.with_suffix(".tmp")
    temporary.write_text(json.dumps(config, indent=2), encoding="utf-8")
    temporary.replace(destination)
    print("Local note-taking models ready.", flush=True)


if __name__ == "__main__":
    main()
