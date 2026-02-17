#!/bin/bash

pkill EgorBot.Server || true
pkill EgorBot.Github || true
pkill VBCSCompiler || true
pkill dotnet || true

WORK_DIR=$(pwd)
if [ ! -f $WORK_DIR/dotnet-install.sh ]; then
    wget https://dot.net/v1/dotnet-install.sh -O $WORK_DIR/dotnet-install.sh
    chmod +x $WORK_DIR/dotnet-install.sh
    $WORK_DIR/dotnet-install.sh --channel "11.0" --install-dir $WORK_DIR/.dotnet
    $WORK_DIR/dotnet-install.sh --channel "10.0" --install-dir $WORK_DIR/.dotnet
fi
export DOTNET_ROOT=${WORK_DIR}/.dotnet
export PATH=${DOTNET_ROOT}:${DOTNET_ROOT}/tools:$PATH
export NUGET_PLUGINS_CACHE_PATH=${DOTNET_ROOT}/NUGET_PLUGINS_CACHE_PATH
export NUGET_PACKAGES=${DOTNET_ROOT}/NUGET_PACKAGES
export NUGET_HTTP_CACHE_PATH=${DOTNET_ROOT}/NUGET_HTTP_CACHE_PATH
export NUGET_SCRATCH=${DOTNET_ROOT}/NUGET_SCRATCH
export DOTNET_NUGET_SIGNATURE_VERIFICATION=false

# Build
dotnet build src/EgorBot.Server/EgorBot.Server.csproj -c Release
dotnet build src/EgorBot.Github/EgorBot.Github.csproj -c Release

# Run
nohup dotnet run --no-build --no-launch-profile --project src/EgorBot.Github/EgorBot.Github.csproj -c Release > ${WORK_DIR}/EgorBot.github.log 2>&1 &
nohup dotnet run --no-build --no-launch-profile --project src/EgorBot.Server/EgorBot.Server.csproj -c Release > ${WORK_DIR}/EgorBot.server.log 2>&1 &