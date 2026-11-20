"""Input identity shared by the local avatar capture and conversion commands."""
from pathlib import Path


def input_path(registry, case):
    return (Path(registry).parent / case["inputPath"]).resolve()


def selected_avatar(arguments):
    if not isinstance(arguments, list) or any(not isinstance(arg, str) for arg in arguments):
        raise ValueError("Invalid conversion arguments")
    if "--avatar" not in arguments:
        return None
    index = arguments.index("--avatar") + 1
    if arguments.count("--avatar") != 1 or index >= len(arguments) or not arguments[index]:
        raise ValueError("Invalid avatar selection arguments")
    return arguments[index]
