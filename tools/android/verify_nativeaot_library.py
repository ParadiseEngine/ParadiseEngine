#!/usr/bin/env python3
"""Verify a compile-only NativeAOT smoke library without claiming Android execution."""
import argparse
import json
from pathlib import Path
from nativeaot_elf import inspect_linkage
from verify_android import inspect_elf

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('library', type=Path)
args = parser.parse_args()
data = args.library.read_bytes()
result = inspect_elf(data)
linkage = inspect_linkage(data)
required = {'SDL_main', 'DotNetRuntimeDebugHeader'}
if not required <= set(linkage['exports']):
    raise SystemExit('Missing NativeAOT runtime marker or SDL_main entry point')
if linkage['soname'] != 'libparadise_android.so':
    raise SystemExit('Unexpected NativeAOT SONAME')
result.update(soname=linkage['soname'], needed=linkage['needed'], verifiedExports=sorted(required), deviceExecutionVerified=False)
print(json.dumps(result, indent=2))
