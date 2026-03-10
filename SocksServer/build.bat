@echo off
setlocal

set GOOS=windows
set GOARCH=amd64
set CGO_ENABLED=0

echo [BUILD] 清理旧文件...
if exist proxy.exe del /f proxy.exe

echo [BUILD] 编译中 (无窗口 + 压缩)...
go build -ldflags="-s -w -H windowsgui" -trimpath -o proxy.exe .

if %ERRORLEVEL% neq 0 (
    echo [ERROR] 编译失败！
    exit /b 1
)

echo.
echo [OK] 编译完成: proxy.exe
echo.
echo 使用方法:
echo   proxy.exe install  -port 1080 -user admin -pass yourpassword -tls
echo   proxy.exe uninstall
echo   proxy.exe run      -port 1080 -user admin -pass yourpassword     （前台调试）
echo.
echo 说明:
echo   -port    监听端口 (默认 1080)
echo   -user    SOCKS5 用户名 (默认 admin)
echo   -pass    SOCKS5 密码 (默认 changeme，请务必修改)
echo   -tls     启用 TLS 封装 (推荐机房使用)
echo   -cert    TLS 证书文件 (空则自动生成自签证书)
echo   -key     TLS 私钥文件
echo   -log     日志文件路径 (默认 proxy.exe 同级 proxy.log)

endlocal
