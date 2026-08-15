@echo off
rem 打包 Alife.GitHubTool 插件 zip
set SRC=C:\Users\Administrator\Desktop\KiraAI7\data\temp\alife-github-tool
set DST=C:\Users\Administrator\Desktop\KiraAI7\data\temp\alife-github-tool\release
if not exist "%DST%" mkdir "%DST%"

rem 临时目录放置zip根内容（manifest.json + cs 平铺）
set TMP=%DST%\pkg
if exist "%TMP%" rmdir /s /q "%TMP%"
mkdir "%TMP%"
copy /y "%SRC%\manifest.json" "%TMP%\" >nul
copy /y "%SRC%\GitHubToolModule.cs" "%TMP%\" >nul

powershell -Command "Compress-Archive -Path '%TMP%\*' -DestinationPath '%DST%\Alife.GitHubTool.zip' -Force"
echo PACK_DONE
