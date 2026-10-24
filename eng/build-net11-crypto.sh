#!/bin/sh
# Build the matching .NET 11 shim with direct OpenSSL calls. The original native
# archives contain a .NET 10 shim; it cannot satisfy .NET 11's new entry points.
set -eu
runtime_source=$1
output_dir=$2
cmake -S "$runtime_source/src/native/libs/System.Security.Cryptography.Native" \
  -B "$output_dir/build" -G Ninja \
  -DLOCAL_BUILD=ON -DGEN_SHARED_LIB=OFF -DFEATURE_DISTRO_AGNOSTIC_SSL=OFF \
  -DSTATIC_LIB_DESTINATION=lib -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_COMPILER=clang -DCMAKE_C_FLAGS=-D_GNU_SOURCE
cmake --build "$output_dir/build" --target System.Security.Cryptography.Native.OpenSsl-Static --parallel 4
cp "$output_dir/build/libSystem.Security.Cryptography.Native.OpenSsl.a" "$output_dir/"
