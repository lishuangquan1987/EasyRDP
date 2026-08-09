@echo off
REM ============================================================================
REM  build-vpx.bat — 构建 XP 兼容 libvpx (VP8) 的 Windows DLL
REM ============================================================================
REM  目标产物:
REM     x86 (i686) : libs/vpx/vpx-x86.dll   ← XP 服务端主用（EasyRDP.Server.Wpf net40）
REM     x64 (amd64): libs/vpx/vpx-x64.dll   ← 现代客户端/测试用（net8.0）
REM  前置条件:
REM     1. 安装 MSYS2  https://www.msys2.org  （默认 C:\msys64）
REM     2. 在 MSYS2 MINGW32 终端 (C:\msys64\msys2_shell.cmd -mingw32) 中执行:
REM            pacman -S --needed base-devel mingw-w64-i686-toolchain
REM        x64 产物需 MINGW64 终端:
REM            pacman -S --needed base-devel mingw-w64-x86_64-toolchain
REM     3. 源码下载（网络受限环境可手动放入 libs/vpx/libvpx-1.16.0/）:
REM            curl -L https://github.com/webmproject/libvpx/archive/refs/tags/v1.16.0.tar.gz
REM  注意:
REM     - XP 兼容关键: --extra-cflags=-D_WIN32_WINNT=0x0501 将 API 版本压到 XP，
REM       避免链接 Vista+ 独占 API（如 HeapSetInformation 等）。
REM     - --enable-realtime-only 移除非实时编解码路径，DLL 更小（~1MB）。
REM     - --disable-vp9 只编 VP8，进一步减体积（本项目 VP9 仅预留枚举）。
REM ============================================================================

setlocal
cd /d "%~dp0"

set VPX_VER=1.16.0
set VPX_DIR=libvpx-%VPX_VER%
set SRC_DIR=%CD%\%VPX_DIR%

REM ---- 0. 前置检查：MSYS2 环境（由 MINGW32/MINGW64 终端调用时存在） ----
if not defined MINGW_PREFIX (
    echo [ERROR] 请通过 MSYS2 的 MINGW32/MINGW64 终端运行本脚本。
    echo         例如: C:\msys64\msys2_shell.cmd -mingw32
    echo         （32 位 x86 产物用 -mingw32，64 位产物用 -mingw64）
    exit /b 1
)
echo [INFO] MINGW_PREFIX=%MINGW_PREFIX%

REM ---- 1. 下载/解压源码 ----
if not exist "%SRC_DIR%" (
    echo [INFO] 下载 libvpx v%VPX_VER% ...
    curl -L -o %VPX_DIR%.tar.gz https://github.com/webmproject/libvpx/archive/refs/tags/v%VPX_VER%.tar.gz
    if errorlevel 1 (
        echo [ERROR] 下载失败。请手动下载 v%VPX_VER%.tar.gz 到 %CD% 并解压为 %VPX_DIR%\
        exit /b 1
    )
    tar -xzf %VPX_DIR%.tar.gz
    if errorlevel 1 exit /b 1
)

cd /d "%SRC_DIR%" || exit /b 1

REM ---- 2. 按架构配置 ----
REM MINGW_PREFIX: MINGW32 → x86-win32-gcc；MINGW64 → x86_64-win64-gcc
if "%MINGW_PREFIX%"=="MINGW32" (
    set TARGET=x86-win32-gcc
    set OUT=..\vpx-x86.dll
) else (
    set TARGET=x86_64-win64-gcc
    set OUT=..\vpx-x64.dll
)

REM ---- 3. 清理 + 配置（XP 兼容 + 实时 + 仅 VP8 + 共享库） ----
make distclean >nul 2>&1

./configure ^
    --target=%TARGET% ^
    --enable-shared ^
    --enable-realtime-only ^
    --disable-vp9 ^
    --disable-examples ^
    --disable-tools ^
    --disable-docs ^
    --disable-unit-tests ^
    --disable-webm-io ^
    --extra-cflags=-D_WIN32_WINNT=0x0501 ^
    --extra-cflags=-O2
if errorlevel 1 (
    echo [ERROR] configure 失败
    exit /b 1
)

REM ---- 4. 编译 ----
make -j4
if errorlevel 1 (
    echo [ERROR] make 失败
    exit /b 1
)

REM ---- 5. 拷贝产物 ----
REM MinGW 共享库产物通常为 vpx.dll（-Wl,--out-implib）。若为 libvpx.dll 则改名。
if exist vpx.dll (
    copy /y vpx.dll "%OUT%" >nul
) else if exist libvpx.dll (
    copy /y libvpx.dll "%OUT%" >nul
) else (
    echo [ERROR] 未找到 vpx.dll/libvpx.dll 产物
    dir /b *.dll 2>nul
    exit /b 1
)
echo [OK] 构建完成: %OUT%
echo [INFO] 使用: 将 %OUT% 按目标架构部署为程序目录下 vpx.dll
endlocal
exit /b 0
