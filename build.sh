#!/usr/bin/env bash
# Shared only within this repository: each checkout owns its own copy.
set -euo pipefail
mnx_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
mnx_parent=$(dirname -- "$mnx_root")
mnx_component=mnxfile
mnx_primary=MNXFile.csproj
mnx_test=false
mnx_package_only=false
mnx_clean=false
usage() {
    printf 'Usage: ./build.sh [--clean] [--test] [--packages-only]\n'
    printf 'Builds this component on Linux for the host architecture.\n'
    printf 'Use build.ps1 on Windows; Windows Native AOT requires Windows build tools.\n'
    printf 'MNXBuildNumber fixes the release number without advancing BuildNumber.txt.\n'
    printf 'MNXBuildRoot and MNXReleaseRoot override the generated output directories.\n'
    }
for mnx_argument in "$@"; do
    case "$mnx_argument" in
        --test) mnx_test=true ;;
        --clean) mnx_clean=true ;;
        --packages-only) mnx_package_only=true ;;
        --linux-only) ;; # Compatibility with the former combined build.
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n' "$mnx_argument" >&2; usage >&2; exit 2 ;;
        esac
    done
if $mnx_package_only && [[ "$mnx_component" != mnxfile ]]; then
    printf '%s\n' '--packages-only applies to MNXFile only.' >&2
    exit 2
    fi
case "$(uname -s)/$(uname -m)" in
    Linux/x86_64) mnx_rid=linux-x64 ;;
    Linux/aarch64) mnx_rid=linux-arm64 ;;
    *) printf 'Run build.sh on Linux x64/arm64 or use build.ps1 on Windows.\n' >&2; exit 2 ;;
    esac
for mnx_command in dotnet flock python3; do
    command -v "$mnx_command" >/dev/null || { printf 'Missing build tool: %s\n' "$mnx_command" >&2; exit 2; }
    done
mnx_sdk=$(dotnet --version)
if [[ ${mnx_sdk%%.*} -lt 10 ]]; then
    printf '.NET SDK 10 or newer is required; found %s.\n' "$mnx_sdk" >&2
    exit 2
    fi
export MNXBuildRoot=${MNXBuildRoot:-"$mnx_parent/mnx_clutter/build/$mnx_component"}
export MNXReleaseRoot=${MNXReleaseRoot:-"$mnx_parent/MNX_Builds/$mnx_component"}
mkdir -p -- "$MNXBuildRoot" "$MNXReleaseRoot"
# Build scripts serialize each component independently.
exec 9>"$MNXBuildRoot/release.lock"
flock -n 9 || { printf 'Another %s release build is running.\n' "$mnx_component" >&2; exit 2; }
export MSBUILDDISABLENODEREUSE=1
if [[ -z ${MNXBuildNumber:-} ]]; then
    MNXBuildNumber=$(dotnet msbuild "$mnx_root/$mnx_primary" -nologo -verbosity:quiet \
        -target:ReserveMNXBuildNumber -getProperty:MNXBuildNumber 9>&-)
    fi
if [[ ! "$MNXBuildNumber" =~ ^[0-9]+$ ]] || (( MNXBuildNumber < 1 || MNXBuildNumber > 65534 )); then
    printf 'MNXBuildNumber must be an integer from 1 to 65534.\n' >&2
    exit 2
    fi
export MNXBuildNumber
mnx_output="$MNXReleaseRoot/$mnx_rid"
mkdir -p -- "$mnx_output"
mnx_config="$MNXBuildRoot/nuget.config"
mnx_feed=${MNXPackageFeed:-"$mnx_parent/MNX_Builds/mnxfile/packages"}
# Encode XML attribute values without changing paths containing spaces or '&'.
mnx_feed=${mnx_feed//&/\&amp;}
mnx_feed=${mnx_feed//</\&lt;}
mnx_feed=${mnx_feed//>/\&gt;}
mnx_feed=${mnx_feed//\"/\&quot;}
if [[ "$mnx_component" == mnxfile ]]; then
    cat > "$mnx_config" <<'CONFIG'
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
CONFIG
else
    cat > "$mnx_config" <<CONFIG
<configuration><packageSources><clear /><add key="MNX client feed" value="$mnx_feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
CONFIG
    fi
mnx_options=(-c Release -p:UseSharedCompilation=false "-p:RestoreConfigFile=$mnx_config")
printf 'Building %s release %s for %s\n' "$mnx_component" "$MNXBuildNumber" "$mnx_rid"
if $mnx_clean; then
    for mnx_clean_project in MNXFile.csproj client/MNXFile.Client.csproj native/MNXFile.Native.csproj; do
        dotnet restore "$mnx_root/$mnx_clean_project" -r "$mnx_rid" "-p:RestoreConfigFile=$mnx_config" 9>&-
        dotnet clean "$mnx_root/$mnx_clean_project" -c Release -r "$mnx_rid" -nologo 9>&-
        done
    fi

mkdir -p -- "$MNXReleaseRoot/packages"
for mnx_project in MNXFile.csproj client/MNXFile.Client.csproj; do
    dotnet pack "$mnx_root/$mnx_project" "${mnx_options[@]}" -o "$MNXReleaseRoot/packages" 9>&-
    done
if ! $mnx_package_only; then
    command -v clang >/dev/null || { printf 'Native AOT needs clang.\n' >&2; exit 2; }
    if [[ -L "$mnx_output/MNXFile.Native.so" ]]; then rm -- "$mnx_output/MNXFile.Native.so"; fi
    dotnet publish "$mnx_root/native/MNXFile.Native.csproj" "${mnx_options[@]}" -r "$mnx_rid" -o "$mnx_output" 9>&-
    mv -f -- "$mnx_output/MNXFile.Native.so" "$mnx_output/libmnxfile.so.1"
    ln -sfn -- libmnxfile.so.1 "$mnx_output/MNXFile.Native.so"
    ln -sfn -- libmnxfile.so.1 "$mnx_output/libmnxfile.so"
    cp -- "$mnx_root/native/include/mnxfile.h" "$mnx_output/"
    fi
if $mnx_test; then
    dotnet run --project "$mnx_root/tests/MNXFile.Smoke/MNXFile.Smoke.csproj" "${mnx_options[@]}" 9>&-
    if ! $mnx_package_only; then
        MNXFILE_NATIVE_LIBRARY="$mnx_output/libmnxfile.so.1" dotnet run \
            --project "$mnx_root/tests/MNXFile.Client.Smoke/MNXFile.Client.Smoke.csproj" "${mnx_options[@]}" 9>&-
        for mnx_mode in --precreate --overwrite; do
            MNXFILE_NATIVE_LIBRARY="$mnx_output/libmnxfile.so.1" dotnet run \
                --project "$mnx_root/tests/MNXFile.Client.Smoke/MNXFile.Client.Smoke.csproj" "${mnx_options[@]}" --no-build -- "$mnx_mode" 9>&-
            done
        python3 "$mnx_root/tests/native/client_protocol.py" "$mnx_output/libmnxfile.so.1" 9>&-
        mnx_test_output="$MNXBuildRoot/tests/$mnx_rid"
        mkdir -p -- "$mnx_test_output"
        cc -std=c11 -Wall -Wextra -Werror -I "$mnx_root/native/include" \
            "$mnx_root/tests/native/smoke.c" "$mnx_output/libmnxfile.so.1" \
            "-Wl,-rpath,$mnx_output" -o "$mnx_test_output/native-smoke"
        cc -shared -fPIC -Wall -Wextra -Werror "$mnx_root/tests/native/deny-icu.c" -o "$mnx_test_output/deny-icu.so"
        mnx_test_work=$(mktemp -d)
        trap 'rm -rf -- "$mnx_test_work"' EXIT
        LD_AUDIT="$mnx_test_output/deny-icu.so" "$mnx_test_output/native-smoke" "$mnx_test_work"
        rm -rf -- "$mnx_test_work"
        trap - EXIT
        fi
    fi

mnx_symbols="$MNXBuildRoot/symbols/$mnx_rid"
mkdir -p -- "$mnx_symbols"
for mnx_symbol in "$mnx_output"/*.dbg "$mnx_output"/*.pdb; do
    if [[ -f "$mnx_symbol" ]]; then mv -f -- "$mnx_symbol" "$mnx_symbols/"; fi
    done
printf 'Release output: %s\n' "$MNXReleaseRoot"
if ! $mnx_package_only; then
    python3 "$mnx_root/tools/stage-linux.py" mnxfile "$mnx_output" "${MNXFinalRoot:-$mnx_parent/MNX_Builds/binaries-final}"
    fi
