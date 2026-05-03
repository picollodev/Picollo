gcc -std=c11 -O3 -fPIC -shared -march=x86-64 -mtune=generic \
  -Wall -Wextra -Wpedantic -Werror \
  -Wconversion -Wsign-conversion -Wshadow -Wundef \
  -Wcast-qual -Wformat=2 -Wstrict-overflow=5 \
  -Wnull-dereference -Wimplicit-fallthrough \
  -Wl,-soname,perf_helpers.so \
  -o picollo_native.so picollo_native.c