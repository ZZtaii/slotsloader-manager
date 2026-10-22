#!/bin/sh
# Called by the extracted desktop shortcut; no terminal is needed.
set -eu
source_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
if ! command -v python3 >/dev/null 2>&1; then
    message='SlotsLoader Manager needs Python 3. Install it from your software centre, then run setup again.'
    if command -v kdialog >/dev/null 2>&1; then kdialog --error "$message"
    elif command -v zenity >/dev/null 2>&1; then zenity --error --text="$message"
    else printf '%s\n' "$message" >&2; fi
    exit 1
fi
exec python3 "$source_dir/linux/launcher.py" --install "$source_dir"
