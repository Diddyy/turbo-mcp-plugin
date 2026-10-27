#!/usr/bin/env sh
# Builds the plugin folder a Turbo server loads, dist/mcp/: manifest.json and the C# sources
# (server/**/*.cs), which the server compiles as it loads the plugin: the folder holds nothing
# built. The sources are compiled first as the server does (server/TurboMcp.csproj imports the
# server's plugin-sdk/Turbo.Plugin.props), so a folder that would not load is not made.
# TurboBin: the server's bin folder to build against (default: ../turbo-cloud's built Turbo.Main).
set -e
cd "$(dirname "$0")/.."
dotnet build server/TurboMcp.csproj -nologo -v q -clp:NoSummary
mkdir -p dist/mcp && find dist/mcp -mindepth 1 -delete   # the folder stays: a hotel watching it keeps watching
cp manifest.json README.md dist/mcp/
find server -name '*.cs' ! -path '*/bin/*' ! -path '*/obj/*' | tar -cf - -T - | tar -xf - -C dist/mcp
echo "dist/mcp: $(find dist/mcp -type f | wc -l) files"
