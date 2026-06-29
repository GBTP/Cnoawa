#!/bin/bash

echo "[run.sh] Cnoawa 自动更新运行脚本"
echo "[run.sh] 参数: $@"
echo ""

while true; do
    echo "[run.sh] 拉取最新代码..."
    git pull

    echo "[run.sh] 构建项目..."
    dotnet build src/Cnoawa/Cnoawa.csproj -c Release --nologo -v q

    if [ $? -ne 0 ]; then
        echo "[run.sh] 构建失败，30秒后重试..."
        sleep 30
        continue
    fi

    echo "[run.sh] 启动节点..."
    dotnet run --project src/Cnoawa/Cnoawa.csproj -c Release --no-build -- "$@"

    echo "[run.sh] 进程退出 (code: $?)，5秒后重启..."
    sleep 5
done
