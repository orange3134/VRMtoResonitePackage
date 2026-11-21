"""Input identity shared by the local avatar capture and conversion commands."""
import hashlib
from pathlib import Path


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class RecaptureRequired(ValueError):
    pass


def verify_project_files(case):
    prefab = Path(case["inputPath"])
    files = {Path(name): digest for name, digest in case.get("dependencyFiles", {}).items()}
    if prefab.suffix.lower() == ".prefab":
        if not files.get(prefab) or not files.get(Path(str(prefab) + ".meta")):
            raise RecaptureRequired("Recapture prefab with dependency checksums")
    for path, digest in files.items():
        if (sha256(path) if path.is_file() else "") != digest:
            raise RecaptureRequired("Unity dependency changed; recapture prefab: " + str(path))
    for name, digest in case.get("projectFiles", {}).items():
        path = Path(name)
        if (sha256(path) if path.exists() else None) != digest:
            raise RecaptureRequired("Prefab metadata or project configuration changed; recapture: " + name)


def input_path(registry, case):
    return (Path(registry).parent / case["inputPath"]).resolve()


def selected_avatar(arguments):
    if not isinstance(arguments, list) or any(not isinstance(arg, str) for arg in arguments):
        raise ValueError("Invalid conversion arguments")
    # CliOptions.Parse consumes the next token verbatim for each of these options.
    tokens = iter(arguments)
    avatar = None
    for token in tokens:
        option = token.lower()
        if option in ("--avatar", "-o", "--output", "--resonite-path", "--view-forward", "--view-up",
                      "--near-clip", "--import-timeout", "--transparent-cutout", "--mtoon-transparent-cutout"):
            value = next(tokens, None)
            if value is None:
                raise ValueError(f"Missing value for {token}")
            if option == "--avatar":
                avatar = value or None
    return avatar
