@echo off
cd /d "%~dp0"
echo ================================================
echo  DeskPin 桌面布局管理
echo ================================================
echo.
"D:\Tools\desk-layout\DeskPin.exe" list
echo.
echo 常用命令（在本窗口输入）：
echo   保存成某个名字： DeskPin.exe save 游戏
echo   切回某个快照  ： DeskPin.exe restore 游戏
echo   改名          ： DeskPin.exe rename 旧名 新名
echo   删除          ： DeskPin.exe delete 名字
echo   核对是否一致  ： DeskPin.exe verify main
echo   硬还原(兜底)  ： DeskPin.exe restore main --hard
echo   看当前状态    ： DeskPin.exe status
echo.
cmd /k
