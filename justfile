game_plugins := 'D:/steam/steamapps/common/Chill with You Lo-Fi Story/BepInEx/plugins'
dll := 'AIChat/bin/Release/net472/AIChat.dll'

# 构建 Release 版本
build:
  dotnet build AIChat/AIChat.csproj -c Release

# 构建并部署到游戏（覆盖安装）
deploy: build
  rm -f "{{game_plugins}}/AIChat.dll"
  cp "{{dll}}" "{{game_plugins}}/AIChat.dll"

# 仅部署（不重新构建，适合反复调试时跳过编译）
deploy-only:
  rm -f "{{game_plugins}}/AIChat.dll"
  cp "{{dll}}" "{{game_plugins}}/AIChat.dll"

# 首次配置：下载 BepInEx 和 Unity DLL
setup:
  python build-resource/fetch.py
