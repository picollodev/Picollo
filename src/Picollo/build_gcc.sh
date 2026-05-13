#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

gcc -std=c11 -O3 -fPIC -shared -march=x86-64 -mtune=generic \
  -Wall -Wextra -Wpedantic -Werror \
  -Wconversion -Wsign-conversion -Wshadow -Wundef \
  -Wcast-qual -Wformat=2 -Wstrict-overflow=5 \
  -Wnull-dereference -Wimplicit-fallthrough \
  -Wl,-soname,picollo_native.so \
  -o picollo_native.so picollo_native.c
