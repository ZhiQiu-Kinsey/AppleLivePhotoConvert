#!/usr/bin/env bash
# 判断本次提交相对基线是否改动了桌面端相关路径，结果写入 GITHUB_OUTPUT 的 desktop=true/false。
# 基线：PR 取 base 提交，push 取 before 提交（由工作流通过 BASE_SHA 传入）。
# 拿不到基线（新分支、手动触发、被 release 调用、取不到提交）时一律视为有改动，
# 宁可多传一次截图，也不漏传。
set -euo pipefail

paths=(
  src/LivePhotoConvert.Desktop
  tests/LivePhotoConvert.Desktop.Tests
  tests/LivePhotoConvert.E2E
)

desktop=true
if [[ -n "${BASE_SHA:-}" && ! "${BASE_SHA}" =~ ^0+$ ]] \
  && git fetch --no-tags --quiet --depth=1 origin "${BASE_SHA}" 2>/dev/null; then
  if git diff --quiet "${BASE_SHA}" HEAD -- "${paths[@]}"; then
    desktop=false
  fi
fi

echo "桌面端相关路径有改动：${desktop}"
echo "desktop=${desktop}" >> "${GITHUB_OUTPUT:-/dev/stdout}"
