#!/bin/sh
# Builds declarad for several operating systems / CPUs and zips every build for distribution.
# Works from macOS, Linux and Windows (Git Bash / WSL); .NET can cross-compile all targets from any of them.
#
#   build/build.sh                                   everything: 6 platforms x 2 variants
#   build/build.sh --rids "linux-x64 osx-arm64"      only these platforms
#   build/build.sh --variants portable               only the self-contained build
#   build/build.sh --version 0.4.0                   override the version (default: <Version> in Declarad.csproj)
#   build/build.sh --clean                           delete build/out and build/dist first
#
# Variants (the zip name says which one):
#   declarad-<version>-<rid>-portable.zip   runs on its own, the .NET runtime is included (bigger)
#   declarad-<version>-<rid>.zip            small, needs the .NET 10 runtime installed on the machine
#
# Results: build/dist/*.zip and build/dist/SHA256SUMS (the publish folders stay in build/out/).
set -e

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
PROJECT="$ROOT/src/Declarad/Declarad.csproj"
OUT="$HERE/out"
DIST="$HERE/dist"

RIDS="win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64"
VARIANTS="portable framework"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
CLEAN=0

while [ $# -gt 0 ]; do
  case "$1" in
    --rids) RIDS="$2"; shift 2 ;;
    --variants) VARIANTS="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --clean) CLEAN=1; shift ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown option: $1 (try --help)" >&2; exit 1 ;;
  esac
done

command -v dotnet >/dev/null 2>&1 || { echo "dotnet SDK 10 was not found on PATH" >&2; exit 1; }
command -v zip >/dev/null 2>&1 || command -v ditto >/dev/null 2>&1 || { echo "'zip' was not found on PATH" >&2; exit 1; }

[ "$CLEAN" = 1 ] && rm -rf "$OUT" "$DIST"
mkdir -p "$OUT" "$DIST"

make_zip() { # make_zip <parent-dir> <entry-name> <zip-path>
  rm -f "$3"
  if command -v zip >/dev/null 2>&1; then
    (cd "$1" && zip -q -r -y -X "$3" "$2")             # Unix permissions are stored; no macOS "._" files
  else
    (cd "$1" && ditto -c -k --norsrc --noextattr --keepParent "$2" "$3")
  fi
}

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

HOST="$(uname -s)"
echo "declarad $VERSION  |  platforms: $RIDS  |  variants: $VARIANTS"

for rid in $RIDS; do
  for variant in $VARIANTS; do
    case "$variant" in
      portable)  sc=true;  suffix="-portable" ;;
      framework) sc=false; suffix="" ;;
      *) echo "unknown variant '$variant' (portable | framework)" >&2; exit 1 ;;
    esac

    echo ""
    echo "== $rid ($variant)"
    publish="$OUT/$rid-$variant"
    rm -rf "$publish"
    # One executable per platform; native libraries are unpacked at run time when needed.
    dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained "$sc" \
      -p:Version="$VERSION" -p:DebugType=None -p:DebugSymbols=false \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeSourceRevisionInInformationalVersion=false \
      -o "$publish" --nologo -v quiet

    case "$rid" in win-*) exe="declarad.exe" ;; *) exe="declarad" ;; esac
    case "$rid" in
      win-*) ;;
      *) chmod +x "$publish/$exe" ;;
    esac
    case "$rid" in
      osx-*)
        # macOS only runs signed code on Apple Silicon. dotnet signs ad hoc when publishing on a Mac;
        # re-sign to be sure, and warn when cross-building elsewhere.
        if [ "$HOST" = Darwin ]; then
          codesign --force --sign - "$publish/$exe" >/dev/null 2>&1 || echo "   warning: ad-hoc signing failed" >&2
        else
          echo "   note: not signed (built on $HOST); sign on a Mac with: codesign --force --sign - declarad" >&2
        fi
        ;;
    esac

    stage="$OUT/stage-$rid-$variant"
    rm -rf "$stage"; mkdir -p "$stage/declarad"
    cp "$publish/$exe" "$stage/declarad/"
    cp "$ROOT/README.md" "$stage/declarad/"
    [ -f "$ROOT/LICENSE" ] && cp "$ROOT/LICENSE" "$stage/declarad/"
    zipfile="$DIST/declarad-$VERSION-$rid$suffix.zip"
    make_zip "$stage" "declarad" "$zipfile"
    rm -rf "$stage"
    echo "   -> $zipfile ($(du -h "$zipfile" | cut -f1))"
  done
done

# Checksums for every zip in dist/, in the format `sha256sum -c` reads.
(cd "$DIST" && for f in declarad-*.zip; do printf '%s  %s\n' "$(sha256 "$f")" "$f"; done) > "$DIST/SHA256SUMS"

echo ""
echo "Done. Zips and SHA256SUMS are in $DIST"
