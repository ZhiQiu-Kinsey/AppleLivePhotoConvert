#!/bin/zsh
# 打出一个当前架构的未签名 LivePhotoConvert.app。
# 不走 Native AOT：Magick.NET 要在运行时按 RID 加载旁边的原生库，裁剪后的单文件带不走它们。
# Windows 发布仍然是 AOT，本脚本只在显式调用时关掉它。
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *)
    echo "不支持的架构: $(uname -m)" >&2
    exit 1
    ;;
esac

version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$root/Directory.Build.props" | head -n 1)
if [[ -z "$version" ]]; then
  echo "无法从 Directory.Build.props 读取版本号" >&2
  exit 1
fi

out="$root/dist/macos"
publish="$out/publish"
app="$out/LivePhotoConvert.app"
rm -rf "$out"

dotnet publish "$root/src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:PublishAot=false -p:PublishTrimmed=false \
  -o "$publish"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"
cp "$root/packaging/macos/LivePhotoConvert.icns" "$app/Contents/Resources/LivePhotoConvert.icns"
sed "s/__VERSION__/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
chmod +x "$app/Contents/MacOS/LivePhotoConvert"

echo "$app"
