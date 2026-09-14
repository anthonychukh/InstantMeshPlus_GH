#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Signing and notarization must run on macOS." >&2
  exit 2
fi

repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
payload_dir="${1:-$repository/artifacts/native/macos-universal/Release}"

required_environment=(
  MACOS_CERTIFICATE_P12
  MACOS_CERTIFICATE_PASSWORD
  APPLE_API_KEY_P8
  APPLE_API_KEY_ID
  APPLE_API_ISSUER_ID
  APPLE_TEAM_ID
)
for variable in "${required_environment[@]}"; do
  if [[ -z "${!variable:-}" ]]; then
    echo "Required signing environment variable is missing: $variable" >&2
    exit 2
  fi
done

worker="$payload_dir/InstantMeshPlus.Worker"
tbb="$payload_dir/libtbb.12.dylib"
for binary in "$worker" "$tbb"; do
  if [[ ! -f "$binary" ]]; then
    echo "Missing macOS release binary: $binary" >&2
    exit 1
  fi
done

temporary="$(mktemp -d)"
keychain="$temporary/signing.keychain-db"
keychain_password="$(openssl rand -hex 24)"
certificate="$temporary/developer-id.p12"
api_key="$temporary/AuthKey_${APPLE_API_KEY_ID}.p8"
notarization_zip="$temporary/InstantMeshPlus-macOS.zip"
notarization_log="$payload_dir/notarization-log.json"
original_keychains=()
while IFS= read -r keychain_path; do
  original_keychains+=("${keychain_path//\"/}")
done < <(security list-keychains -d user)

cleanup() {
  security delete-keychain "$keychain" >/dev/null 2>&1 || true
  if [[ "${#original_keychains[@]}" -gt 0 ]]; then
    security list-keychains -d user -s "${original_keychains[@]}" >/dev/null 2>&1 || true
  fi
  rm -rf "$temporary"
}
trap cleanup EXIT

decode_base64() {
  if base64 --decode >/dev/null 2>&1 <<<''; then
    printf '%s' "$1" | base64 --decode
  else
    printf '%s' "$1" | base64 -D
  fi
}

decode_base64 "$MACOS_CERTIFICATE_P12" > "$certificate"
decode_base64 "$APPLE_API_KEY_P8" > "$api_key"
chmod 600 "$certificate" "$api_key"

security create-keychain -p "$keychain_password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
security list-keychains -d user -s "$keychain"
security import "$certificate" \
  -k "$keychain" \
  -P "$MACOS_CERTIFICATE_PASSWORD" \
  -A \
  -t cert \
  -f pkcs12
security set-key-partition-list \
  -S apple-tool:,apple: \
  -s \
  -k "$keychain_password" \
  "$keychain"

identity="${IMP_APPLE_SIGNING_IDENTITY:-}"
if [[ -z "$identity" ]]; then
  identities=()
  while IFS= read -r discovered_identity; do
    identities+=("$discovered_identity")
  done < <(
    security find-identity -v -p codesigning "$keychain" |
      sed -n 's/.*"\(Developer ID Application:.*\)"/\1/p'
  )
  if [[ "${#identities[@]}" -ne 1 ]]; then
    echo "Expected exactly one Developer ID Application identity; found ${#identities[@]}." >&2
    exit 1
  fi
  identity="${identities[0]}"
fi
if [[ "$identity" != *"($APPLE_TEAM_ID)"* ]]; then
  echo "The selected signing identity does not match APPLE_TEAM_ID." >&2
  exit 1
fi

codesign --force --timestamp --options runtime --sign "$identity" --keychain "$keychain" "$tbb"
codesign --force --timestamp --options runtime --sign "$identity" --keychain "$keychain" "$worker"
codesign --verify --strict --verbose=2 "$tbb"
codesign --verify --strict --verbose=2 "$worker"

notarization_payload="$temporary/InstantMeshPlus"
mkdir -p "$notarization_payload"
cp "$worker" "$tbb" "$notarization_payload/"
ditto -c -k --sequesterRsrc --keepParent "$notarization_payload" "$notarization_zip"

submission_json="$(
  xcrun notarytool submit "$notarization_zip" \
    --key "$api_key" \
    --key-id "$APPLE_API_KEY_ID" \
    --issuer "$APPLE_API_ISSUER_ID" \
    --wait \
    --output-format json
)"
request_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])' <<<"$submission_json")"
status="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["status"])' <<<"$submission_json")"
xcrun notarytool log "$request_id" \
  --key "$api_key" \
  --key-id "$APPLE_API_KEY_ID" \
  --issuer "$APPLE_API_ISSUER_ID" \
  "$notarization_log"
if [[ "$status" != "Accepted" ]]; then
  echo "Apple notarization was not accepted; status: $status" >&2
  exit 1
fi

echo "Signed and notarized macOS payload: $payload_dir"
echo "Notarization request: $request_id"
echo "Standalone command-line binaries cannot carry a stapled ticket; Gatekeeper can retrieve the accepted ticket online."
