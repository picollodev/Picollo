#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

gcc -std=c11 -O3 -fPIC -march=x86-64 -mtune=generic \
  -Wall -Wextra -Wpedantic -Werror \
  -Wconversion -Wsign-conversion -Wshadow -Wundef \
  -Wcast-qual -Wformat=2 -Wstrict-overflow=5 \
  -Wnull-dereference -Wimplicit-fallthrough \
  -c ../Picollo/picollo_native.c \
  -o picollo_native.o

ar rcs libpicollo_native.a picollo_native.o
